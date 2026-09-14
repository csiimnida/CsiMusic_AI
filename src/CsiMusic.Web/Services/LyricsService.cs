using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CsiMusic.Web.Data;
using CsiMusic.Web.Services.Ai;
using CsiMusic.Web.Options;
using Microsoft.Extensions.Options;
using Dapper;

namespace CsiMusic.Web.Services;

/// <summary>
/// 가사 조회 — LRCLIB(무료·키 불필요)에서 싱크(LRC)/플레인 가사를 가져와 lyrics 테이블에 캐시한다.
/// Python services/lyrics.py 의 LRCLIB 경로 포팅. 부정결과도 캐시(NEGATIVE_TTL)하고, 곡 언어와
/// 충돌하는 캐시는 자동 폐기·재조회(언어 가드)한다.
/// (첫 컷: 커버 설명란 원곡추출·NetEase/Bugs 폴백은 후속 — selfsync/yt-dlp 의존이라 Phase E 와 함께.)
/// </summary>
public sealed partial class LyricsService(
    HttpClient http, ISqliteConnectionFactory factory, ILogger<LyricsService> log,
    AiTitleParser aiTitles, AiLyricsVerifier aiVerifier,
    NamuLyricsSource namuSource, AiLyricsLayers aiLayers, IOptions<AppOptions> appOptions)
{
    private const string LrclibBase = "https://lrclib.net/api";
    private const long NegativeTtl = 7 * 86400;
    private const double DurationNear = 5.0, DurationOk = 15.0, DurationHard = 45.0, AliasSlack = 3.0;

    // 언어 자동치유를 적용할(외부 자동매칭) 소스. 수동/자체싱크는 보존.
    private static readonly HashSet<string> LangHealSources = new(StringComparer.Ordinal)
        { "lrclib", "description", "netease", "bugs", "internet", "lyricsovh" };

    public sealed record Result(string VideoId, string? Synced, string? Plain, string Source,
        int OffsetMs, bool Locked, object? Layers, string Status);

    // ---------- 공개 진입점 ----------

    /// <summary>lyrics 테이블 조회, 없거나 재조회 대상이면 LRCLIB 에서 가져와 캐시 후 반환.</summary>
    public async Task<Result> GetOrFetchAsync(
        string videoId, string title, string? uploader, int duration, MusicMeta? music = null,
        CancellationToken ct = default)
    {
        // 1) 캐시 조회는 짧게 열고 닫는다 — 뒤이은 LRCLIB 네트워크 조회(최대 십수 회 순차 HTTP) 동안
        //    SQLite 연결(풀 자원)을 붙잡지 않도록. 캐시 히트면 여기서 바로 반환(연결 1회).
        int existingOffset = 0;
        bool existingLocked = false;
        using (var conn = factory.Create())
        {
            var row = await conn.QueryFirstOrDefaultAsync(
                "SELECT synced, plain, source, offset_ms, locked, layers, fetched_at FROM lyrics WHERE video_id = @vid",
                new { vid = videoId });

            if (row is not null)
            {
                string? synced = row.synced, plain = row.plain, source = row.source;
                int offset = (int)(long)row.offset_ms;
                bool locked = (long)row.locked != 0;
                var layers = ParseLayers((string?)row.layers);
                existingOffset = offset;
                existingLocked = locked;

                if (locked)
                    return Respond(videoId, synced, plain, source, offset, locked, layers);

                // 언어 판정은 영상 제목이 아니라 곡 제목 기준(LanguageRef 주석 참조).
                // 여기는 네트워크 이전이라 AI 힌트가 없다 — 메타/TitleParser 만으로 뽑는다.
                var healRef = LanguageRef(Candidates(title, uploader, music).Tracks, title);
                var syncedBad = !string.IsNullOrEmpty(synced) && source is not null
                    && LangHealSources.Contains(source) && !LyricsLanguageOk(healRef, synced!);
                var isNegative = source == "none" && string.IsNullOrEmpty(synced) && string.IsNullOrEmpty(plain);
                var retryAliasNegative = isNegative && MayNeedTranslatedTitleRetry(title, uploader);
                long fetchedAt = (long)row.fetched_at;
                if (!syncedBad && (!isNegative || (!retryAliasNegative && (Common.AppTime.NowTs() - fetchedAt) < NegativeTtl)))
                    return Respond(videoId, synced, plain, source, offset, locked, layers);
                if (syncedBad) log.LogInformation("lyrics {Vid}: 캐시 싱크 언어 불일치 → 재조회", videoId);
            }
        }

        // 2) 네트워크 조회 — DB 연결을 잡지 않은 상태로. (요청 중단 시 즉시 취소)
        var (newSynced, newPlain, newSource) = await FetchExternalAsync(videoId, title, uploader, duration, music, ct);
        // 클라이언트가 떠났으면 부정 결과를 캐시하지 말고 중단(전송 지연·과거 슬로우로 7일 부정캐시 오염 방지).
        ct.ThrowIfCancellationRequested();

        // 3) 쓰기는 새 짧은 연결로. UPSERT: 신규면 삽입, 있으면 synced/plain/source/fetched_at 만 갱신(offset/locked/layers 보존).
        var now = Common.AppTime.NowTs();
        using (var conn = factory.Create())
        {
            await conn.ExecuteAsync(
                "INSERT INTO lyrics (video_id, synced, plain, source, offset_ms, locked, fetched_at) " +
                "VALUES (@vid, @synced, @plain, @source, @offset, @locked, @now) " +
                "ON CONFLICT(video_id) DO UPDATE SET synced = excluded.synced, plain = excluded.plain, " +
                "source = excluded.source, fetched_at = excluded.fetched_at",
                new { vid = videoId, synced = newSynced, plain = newPlain, source = newSource,
                      offset = existingOffset, locked = existingLocked ? 1 : 0, now });
        }

        log.LogInformation("lyrics {Vid}: {Status} (source={Source})", videoId, Classify(newSynced, newPlain), newSource);
        return Respond(videoId, newSynced, newPlain, newSource, existingOffset, existingLocked, null);
    }

    // ---------- 외부 소스 사다리 ----------

    /// <summary>
    /// 외부 가사 소스를 순서대로 훑어 (synced, plain, source) 를 정한다. 전부 비면 source="none".
    ///
    /// 순서는 "싱크 우선, 그 다음 정확도" 다:
    ///   1) Unison 싱크   — videoId 정확 매칭이라 가장 믿을 만하고 단어 단위 타이밍까지 온다
    ///   2) LRCLIB 싱크   — 제목/아티스트 퍼지 매칭이지만 커버리지가 넓다
    ///   3) Unison 플레인 — 싱크가 없을 때. LRCLIB 플레인보다 매칭이 정확하다
    ///   4) LRCLIB 플레인
    ///   5) 인터넷 폴백(NetEase 싱크 / Bugs·lyrics.ovh 플레인) — 한국 인디/커버/아시아권 곡 보완
    ///
    /// 모두 best-effort(실패·차단 시 무해). 빈 결과도 호출측이 부정캐시로 남겨 반복 조회를 막는다.
    /// (5) 는 곡당 HTTP 왕복이 여러 번이라 재생 요청 경로에서만 쓰고, 배치는 includeInternet=false 로 뺀다.
    ///
    /// AI 보조(켜져 있을 때)가 두 군데 붙는다 — 둘 다 없어도 동작은 이 아래 그대로다:
    ///   (A) 곡당 한 번 제목을 파싱해 곡名·아티스트 힌트를 만들고, 세 소스가 그 힌트를 공유한다.
    ///   (B) 각 소스가 낸 후보를 채택 직전에 검증해, 확신 있는 오탐이면 <b>다음 소스로 내려간다</b>.
    ///       즉 검증은 실패를 만드는 게 아니라 더 나은 후보를 찾을 기회를 만든다.
    /// </summary>
    private async Task<(string? Synced, string? Plain, string Source)> FetchExternalAsync(
        string videoId, string title, string? uploader, int duration, MusicMeta? music, CancellationToken ct,
        bool includeInternet = true)
    {
        // (A) 곡당 한 번만 묻는다. 소스마다 부르면 같은 답에 API 를 세 번 쓴다.
        var hint = await aiTitles.ParseAsync(title, uploader, duration, music, ct);

        // (B) 채택 게이트. AI 가 꺼져 있거나 확신이 없으면 항상 true 라 사다리는 그대로 흐른다.
        //
        // 곡당 검증 API 예산을 둔다. 사다리 단마다 부르면 한 곡에 최대 5회가 쌓이는데, 타임아웃이
        // 겹치면 가사 패널이 수십 초 멈추고 무료 티어도 그만큼 빨리 탄다. 검증은 '2차 소견'이라
        // 위쪽 두 단(가장 유력한 후보들)에만 걸어도 오탐의 대부분을 잡는다. 예산이 떨어지면
        // 규칙 기반 판정을 그대로 믿는다 — 즉 AI 를 끈 것과 같은 동작으로 안전하게 수렴한다.
        // 캐시 히트는 지연·비용이 0 이라 예산을 쓰지 않는다(재조회 때는 전 구간이 다시 검증된다).
        const int VerifyApiBudget = 2;
        var verifyApiUsed = 0;

        async Task<bool> Ok(string source, string? track, string? artist, double? dur, string lyrics)
        {
            var (accept, _, calledApi) = await aiVerifier.AcceptAsync(
                title, uploader, duration,
                new AiLyricsVerifier.Candidate(source, track, artist, dur, lyrics),
                apiBudgetExhausted: verifyApiUsed >= VerifyApiBudget, ct);
            if (calledApi) verifyApiUsed++;
            return accept;
        }

        var unison = await FetchUnisonAsync(videoId, title, uploader, duration, music, hint, ct);
        // videoId 정확매칭은 검증을 건너뛴다 — 그 영상에 직접 달린 가사라 '다른 곡' 오탐이 성립하지 않고,
        // 굳이 물어보면 API 만 쓴다. 메타데이터 폴백 매칭(exact=false)일 때만 검증한다.
        var unisonTrusted = unison?.ExactVideoId == true;

        if (unison?.Synced is { } us && (unisonTrusted || await Ok("unison", unison.Track, null, null, us)))
            return (us, null, "unison");

        var lrclib = await FetchLrclibAsync(title, uploader, duration, music, hint, ct);
        // 같은 LRCLIB 항목의 synced 가 '다른 곡'으로 거부되면 그 항목의 plain 도 같은 곡의 것이 아니다.
        // 다시 물어보면 텍스트가 달라 캐시가 안 먹고 API 만 한 번 더 쓴다 → 플래그로 통째로 건너뛴다.
        var lrclibRejected = false;
        if (Empty(lrclib?.SyncedLyrics) is { } ls)
        {
            if (await Ok("lrclib", lrclib!.TrackName, lrclib.ArtistName, lrclib.Duration, ls))
                return (ls, Empty(lrclib.PlainLyrics), "lrclib");
            lrclibRejected = true;
        }

        if (unison?.Plain is { } up && (unisonTrusted || await Ok("unison", unison.Track, null, null, up)))
            return (null, up, "unison");

        if (!lrclibRejected && Empty(lrclib?.PlainLyrics) is { } lp
            && await Ok("lrclib", lrclib!.TrackName, lrclib.ArtistName, lrclib.Duration, lp))
            return (null, lp, "lrclib");

        if (includeInternet)
        {
            var net = await FetchInternetAsync(title, uploader, duration, music, hint, ct);
            // NetEase/Bugs/lyrics.ovh 는 곡 메타를 안 돌려주므로 가사 본문만으로 판정한다.
            if (net is not null && (net.Synced ?? net.Plain) is { } text
                && await Ok(net.Source, null, null, null, text))
                return (net.Synced, net.Plain, net.Source);
        }

        // 마지막 수단 — 나무위키(계획 §9.6). 보컬로이드·합성엔진 곡은 LRCLIB·Unison 에 없는데
        // 나무위키에는 실려 있는 경우가 흔하다. 여기까지 왔다는 건 다른 소스가 전부 빈손이라는 뜻이다.
        //
        // 스크랩이라 <b>검증 없이는 절대 채택하지 않는다.</b> 다른 소스는 자체 메타(곡명·아티스트·
        // 길이)로 1차 거름이 되지만 나무위키는 그런 게 없어서, AI 검증(작업 B)이 유일한 관문이다.
        // 예산이 없거나 AI 가 꺼져 있으면 채택하지 않고 그냥 없는 것으로 둔다.
        if (appOptions.Value.NamuLayersEnabled && aiLayers.Enabled)
        {
            var namu = await namuSource.FindForLyricsAsync(title, music?.Track, music?.Artist, ct);
            if (namu is not null
                && await aiLayers.ExtractLyricsAsync(namu.Excerpt, ct) is { Count: > 0 } namuLines)
            {
                var text = string.Join("\n", namuLines);
                var (accept, verdict, calledApi) = await aiVerifier.AcceptAsync(
                    title, uploader, duration,
                    new AiLyricsVerifier.Candidate("namu", namu.Document, null, null, text),
                    apiBudgetExhausted: verifyApiUsed >= VerifyApiBudget, ct);
                if (calledApi) verifyApiUsed++;
                // verdict 가 null 이면 '판정 없음'이다. 다른 소스는 그걸 통과로 보지만 여기서는
                // 거부한다 — 검증되지 않은 스크랩을 가사로 띄우느니 없는 편이 낫다.
                if (accept && verdict is not null)
                {
                    log.LogInformation("나무위키 가사 채택: {Doc} — {Lines}줄", namu.Document, namuLines.Count);
                    return (null, text, "namu");
                }
                log.LogInformation("나무위키 가사 거부: {Doc} — {Why}", namu.Document,
                    verdict?.Reason ?? "판정 없음(AI 예산·설정)");
            }
        }

        return (null, null, "none");

    }

    // ---------- 배치 전용 (쓰기를 트랜잭션으로 묶어 USB 부담을 줄이기 위해 조회/쓰기를 분리) ----------

    public sealed record PendingLyricWrite(string Vid, string? Synced, string? Plain, string Source);

    /// <summary>배치용: 캐시를 보고 스킵 여부를 정하고, 필요할 때만 네트워크 조회를 한다(DB 쓰기는 안 함).
    /// Write 가 null 이면 이미 처리된 곡(스킵). 실제 쓰기는 호출측이 PersistManyAsync 로 묶어서 한다.</summary>
    public async Task<(string Status, string Source, PendingLyricWrite? Write)> FetchForBatchAsync(
        string videoId, string title, string? uploader, int duration, bool force, MusicMeta? music = null,
        CancellationToken ct = default)
    {
        using (var conn = factory.Create())
        {
            var row = await conn.QueryFirstOrDefaultAsync(
                "SELECT synced, plain, source, locked, fetched_at FROM lyrics WHERE video_id = @vid",
                new { vid = videoId });
            if (row is not null)
            {
                string? synced = row.synced, plain = row.plain, source = row.source;
                bool locked = (long)row.locked != 0;
                bool hasLyrics = !string.IsNullOrEmpty(synced) || !string.IsNullOrEmpty(plain);
                // 잠긴 곡은 절대 덮어쓰지 않는다. force 가 아니면 이미 가사 있는 곡·최근 부정캐시는 건너뛴다.
                bool negativeRecent = source == "none" && !hasLyrics
                    && (Common.AppTime.NowTs() - (long)row.fetched_at) < NegativeTtl;
                if (locked || (!force && (hasLyrics || negativeRecent)))
                    return (Classify(synced, plain), source ?? "none", null);
            }
        }

        // 배치는 Unison(videoId 정확매칭) → LRCLIB 까지만. 인터넷 폴백은 곡당 왕복이 많아 제외한다.
        var (newSynced, newPlain, newSource) =
            await FetchExternalAsync(videoId, title, uploader, duration, music, ct, includeInternet: false);
        ct.ThrowIfCancellationRequested();
        return (Classify(newSynced, newPlain), newSource,
            new PendingLyricWrite(videoId, newSynced, newPlain, newSource));
    }

    /// <summary>배치용 대량 UPSERT — 여러 곡을 한 트랜잭션으로 묶어 커밋(=USB 쓰기 횟수)을 크게 줄인다.
    /// 신규 행만 offset/locked=0 으로 넣고, 기존 행은 synced/plain/source/fetched_at 만 갱신(offset/locked/layers 보존).</summary>
    public async Task PersistManyAsync(IReadOnlyList<PendingLyricWrite> rows)
    {
        if (rows.Count == 0) return;
        var now = Common.AppTime.NowTs();
        using var conn = factory.Create();
        using var tx = conn.BeginTransaction();
        foreach (var r in rows)
            await conn.ExecuteAsync(
                "INSERT INTO lyrics (video_id, synced, plain, source, offset_ms, locked, fetched_at) " +
                "VALUES (@vid, @synced, @plain, @source, 0, 0, @now) " +
                "ON CONFLICT(video_id) DO UPDATE SET synced = excluded.synced, plain = excluded.plain, " +
                "source = excluded.source, fetched_at = excluded.fetched_at",
                new { vid = r.Vid, synced = r.Synced, plain = r.Plain, source = r.Source, now }, tx);
        tx.Commit();
    }

    private static string? Empty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>싱크 고정/해제. 대상 lyrics 행이 있으면 true.</summary>
    public async Task<bool> SetLockAsync(string videoId, bool locked)
    {
        using var conn = factory.Create();
        var n = await conn.ExecuteAsync(
            "UPDATE lyrics SET locked = @locked WHERE video_id = @vid",
            new { locked = locked ? 1 : 0, vid = videoId });
        return n > 0;
    }

    /// <summary>가사 싱크가 고정돼 있는지.</summary>
    public async Task<bool> IsLockedAsync(string videoId)
    {
        using var conn = factory.Create();
        var locked = await conn.ExecuteScalarAsync<long?>(
            "SELECT locked FROM lyrics WHERE video_id = @vid", new { vid = videoId });
        return locked is not null and not 0;
    }

    private Result Respond(string vid, string? synced, string? plain, string? source, int offset, bool locked, object? layers) =>
        new(vid, synced, plain, source ?? "none", offset, locked, layers, Classify(synced, plain));

    private static string Classify(string? synced, string? plain) =>
        !string.IsNullOrEmpty(synced) ? "synced" : !string.IsNullOrEmpty(plain) ? "plain" : "none";

    private static object? ParseLayers(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    // ---------- LRCLIB 조회 ----------

    private sealed record LrclibItem(
        [property: JsonPropertyName("trackName")] string? TrackName,
        [property: JsonPropertyName("artistName")] string? ArtistName,
        [property: JsonPropertyName("duration")] double? Duration,
        [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics,
        [property: JsonPropertyName("plainLyrics")] string? PlainLyrics);

    private async Task<LrclibItem?> FetchLrclibAsync(
        string title, string? uploader, int duration, MusicMeta? music,
        AiTitleParser.AiTitle? hint, CancellationToken ct)
    {
        var (tracks, artists) = Candidates(title, uploader, music, hint);
        if (tracks.Count == 0) return null;
        var artistCands = MatchArtistCandidates(artists, tracks);
        var langRef = LanguageRef(tracks, title);

        // (artist, track) 쌍 — 정확 매칭용.
        var pairs = new List<(string Artist, string Track)>();
        foreach (var t in tracks.Take(4))
        {
            foreach (var a in artists.Take(5))
                if (!a.Equals(t, StringComparison.OrdinalIgnoreCase)) pairs.Add((a, t));
            pairs.Add(("", t));
        }
        pairs = pairs.Distinct().ToList();

        // 1) 정확 매칭(get).
        foreach (var (artist, track) in pairs.Take(5))
        {
            var qs = $"artist_name={Uri.EscapeDataString(artist.Length > 0 ? artist : track)}&track_name={Uri.EscapeDataString(track)}";
            if (duration > 0) qs += $"&duration={duration}";
            var got = await HttpGetAsync<LrclibItem>($"{LrclibBase}/get?{qs}", ct);
            if (got is not null)
            {
                var gtext = got.SyncedLyrics ?? got.PlainLyrics;
                if (!string.IsNullOrEmpty(gtext) && LyricsLanguageOk(langRef, gtext!)
                    && LrclibResultOk(got, tracks, artistCands, duration))
                    return got;
            }
        }

        // 2) 근사 검색(search).
        var results = new List<LrclibItem>();
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var queries = new List<string>();
        foreach (var (artist, track) in pairs)
        {
            var q = $"{track} {artist}".Trim();
            if (q.Length > 0) queries.Add(q);
        }
        queries.AddRange(artistCands.Take(3));
        foreach (var q in queries)
        {
            if (!tried.Add(q)) continue;
            var arr = await HttpGetAsync<List<LrclibItem>>($"{LrclibBase}/search?q={Uri.EscapeDataString(q)}", ct);
            if (arr is not null) results.AddRange(arr);
            if (tried.Count >= 8) break;
        }
        return PickBest(results, duration, tracks, artistCands, langRef);
    }

    private async Task<T?> HttpGetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct);
        }
        // 요청 중단(클라이언트 이탈)은 위로 전파해 조회를 즉시 멈춘다.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // LRCLIB 자체 지연(HttpClient 8초 타임아웃)·네트워크·파싱 실패는 이 후보만 건너뛴다.
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            log.LogDebug("lrclib unreachable: {Msg}", e.Message);
            return null;
        }
    }

    // ---------- 인터넷 가사 폴백 (NetEase 싱크 / Bugs·lyrics.ovh 플레인) ----------
    // Python selfsync.py 의 fetch_internet_lyrics 포팅. LRCLIB 공백을 메운다(특히 한국 인디/커버).
    // 모두 best-effort: 어떤 실패·차단이든 null 로 조용히 떨어진다(앱에 무해). 모든 소스에 언어(문자체계)
    // 가드를 적용해 틀린 언어 매칭(일본곡에 프랑스어 가사 등)을 막는다.

    public sealed record InternetLyric(string? Synced, string? Plain, string Source);

    private async Task<InternetLyric?> FetchInternetAsync(
        string title, string? uploader, int duration, MusicMeta? music,
        AiTitleParser.AiTitle? hint, CancellationToken ct)
    {
        var (tracks, artists) = Candidates(title, uploader, music, hint);
        if (tracks.Count == 0) return null;
        var track0 = tracks[0];
        // 곡名처럼 보이는 후보는 걸러 진짜 아티스트를 고른다(artists[0] 는 흔히 곡名이라 부정확).
        var artistCands = MatchArtistCandidates(artists, tracks);
        var artist0 = artistCands.Count > 0 ? artistCands[0] : "";
        var langRef = LanguageRef(tracks, title);

        // NetEase 검색 쿼리 후보(중복 제거, 최대 3 — 각 검색이 8개 결과라 대개 한 번이면 충분).
        var queries = new List<string>();
        foreach (var raw in new[] { $"{track0} {artist0}", track0, tracks.Count > 1 ? $"{tracks[1]} {artist0}" : "" })
        {
            var q = raw.Trim();
            if (q.Length > 0 && !queries.Contains(q)) queries.Add(q);
        }

        string? plainHit = null;
        foreach (var q in queries.Take(3))
        {
            var ne = await FetchNeteaseAsync(q, duration, track0, artist0, ct);
            if (ne?.Synced is { } s && LyricsLanguageOk(langRef, s)) return new InternetLyric(s, null, "netease");
            if (plainHit is null && ne?.Plain is { } p && LyricsLanguageOk(langRef, p)) plainHit = p;
        }
        if (plainHit is not null) return new InternetLyric(null, plainHit, "netease");

        // Bugs — 한국 vtuber/인디 등 NetEase·LRCLIB 공백 보완(원곡 언어 그대로).
        var bugs = await FetchBugsAsync(track0, artist0, ct);
        if (bugs is not null && LyricsLanguageOk(langRef, bugs)) return new InternetLyric(null, bugs, "bugs");

        // lyrics.ovh — 1회만(영미권 plain).
        if (artist0.Length > 0)
        {
            var ovh = await FetchLyricsOvhAsync(artist0, track0, ct);
            if (ovh is not null && LyricsLanguageOk(langRef, ovh)) return new InternetLyric(null, ovh, "lyricsovh");
        }
        return null;
    }

    // ----- NetEase(music.163.com) — 무키. 아시아권 커버리지 좋고 사람이 만든 싱크 LRC 를 준다. -----
    private static readonly (string Key, string Value)[] NeteaseHeaders =
        { ("Referer", "https://music.163.com/"), ("Cookie", "appver=2.0.2") };

    private sealed record NeteaseLyric(string? Synced, string? Plain);

    private async Task<NeteaseLyric?> FetchNeteaseAsync(string query, int duration, string track, string artist, CancellationToken ct)
    {
        // GET /search/get/web 는 근래 암호화 응답을 주므로 평문 JSON 을 주는 POST /search/get 사용.
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["s"] = query, ["type"] = "1", ["offset"] = "0", ["total"] = "true", ["limit"] = "8" });
        var searchJson = await HttpTextAsync("https://music.163.com/api/search/get", NeteaseHeaders, form, ct);
        if (searchJson is null) return null;

        long? bestId = null;
        (double, double, double) bestKey = default;
        var have = false;
        try
        {
            using var doc = JsonDocument.Parse(searchJson);
            if (!doc.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var s in songs.EnumerateArray())
            {
                var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var durS = s.TryGetProperty("duration", out var d) && d.TryGetInt64(out var dm) ? dm / 1000.0 : 0;
                var diff = duration > 0 ? Math.Abs(durS - duration) : 0;
                var sim = track.Length > 0 ? Similar(name, track) : 0.5;
                if (duration > 0 && diff > 15) continue;                 // 길이 15초 초과로 빗나감
                if (track.Length > 0 && sim < 0.45) continue;            // 곡名 유사도 매우 낮음
                var asim = artist.Length > 0 ? NeteaseArtistSim(s, artist) : 0.5;
                if (artist.Length > 0 && asim < 0.3) continue;           // 아티스트 힌트와 전혀 안 맞음(동명 타곡)
                var key = (Math.Round(sim, 1), Math.Round(asim, 1), -diff);  // 곡名 → 아티스트 → 길이 근접
                if ((!have || Comparer<(double, double, double)>.Default.Compare(key, bestKey) > 0)
                    && s.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var idv))
                { bestKey = key; have = true; bestId = idv; }
            }
        }
        catch (JsonException) { return null; }
        if (bestId is null) return null;

        var lyricJson = await HttpTextAsync(
            $"https://music.163.com/api/song/lyric?os=pc&id={bestId}&lv=-1&kv=-1&tv=-1", NeteaseHeaders, null, ct);
        if (lyricJson is null) return null;
        string rawLrc;
        try
        {
            using var doc = JsonDocument.Parse(lyricJson);
            rawLrc = doc.RootElement.TryGetProperty("lrc", out var lrc) && lrc.TryGetProperty("lyric", out var ly)
                ? ly.GetString() ?? "" : "";
        }
        catch (JsonException) { return null; }

        var text = StripCredits(rawLrc.Trim());
        if (text.Length == 0) return null;
        // 타임태그가 충분하면 synced, 아니면 plain.
        return LrcTagRe().Matches(text).Count >= 4 ? new NeteaseLyric(text, null) : new NeteaseLyric(null, CleanPlain(text));
    }

    private static double NeteaseArtistSim(JsonElement song, string artist)
    {
        if (!song.TryGetProperty("artists", out var arts) || arts.ValueKind != JsonValueKind.Array) return 0;
        double max = 0;
        foreach (var a in arts.EnumerateArray())
            if (a.TryGetProperty("name", out var nm)) max = Math.Max(max, Similar(nm.GetString() ?? "", artist));
        return max;
    }

    // 가사 줄만 남기고 크레딧 메타 줄(작곡:…, produced by …)을 제거(NetEase LRC 머리에 흔함).
    private static string StripCredits(string lrc)
    {
        var kept = new List<string>();
        foreach (var raw in lrc.Split('\n'))
        {
            var text = LrcTagRe().Replace(raw, "").Trim();
            if (text.Length > 0 && CreditRe().IsMatch(text) && (text.Contains(':') || text.Contains('：'))) continue;
            kept.Add(raw);
        }
        return string.Join("\n", kept).Trim();
    }

    // ----- Bugs(music.bugs.co.kr) — 비로그인 트랙 페이지 HTML 의 <xmp> 가사를 긁는다(플레인). -----
    private static readonly (string Key, string Value)[] BugsHeaders =
    {
        ("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"),
        ("Referer", "https://music.bugs.co.kr/"),
    };

    private async Task<string?> FetchBugsAsync(string track, string artist, CancellationToken ct)
    {
        track = (track ?? "").Trim();
        if (track.Length == 0) return null;
        var tid = await BugsSearchIdAsync($"{track} {artist}".Trim(), ct) ?? await BugsSearchIdAsync(track, ct);
        if (tid is null) return null;
        var page = await HttpTextAsync($"https://music.bugs.co.kr/track/{tid}", BugsHeaders, null, ct);
        if (page is null) return null;

        // 제목 유사도 가드 — 검색 1위가 엉뚱한 곡이면 버린다.
        var og = BugsOgTitleRe().Match(page);
        if (og.Success)
        {
            var ptitle = System.Net.WebUtility.HtmlDecode(og.Groups[1].Value);
            if (Similar(ptitle, track) < 0.4 && !ptitle.Contains(track, StringComparison.OrdinalIgnoreCase)) return null;
        }

        var m = BugsLyricsRe().Match(page);
        if (!m.Success) m = BugsXmpRe().Match(page);
        if (!m.Success) return null;
        var text = System.Net.WebUtility.HtmlDecode(BrRe().Replace(m.Groups[1].Value, "\n")).Trim();
        if (text.Split('\n').Length < 4) return null;   // 너무 짧으면 가사 아님(메타/오추출)
        return CleanPlain(text);
    }

    private async Task<string?> BugsSearchIdAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var html = await HttpTextAsync(
            "https://music.bugs.co.kr/search/track?q=" + Uri.EscapeDataString(query), BugsHeaders, null, ct);
        if (html is null) return null;
        var m = BugsTrackRe().Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ----- lyrics.ovh — 키 불필요, 주로 영미권 plain. -----
    private async Task<string?> FetchLyricsOvhAsync(string artist, string track, CancellationToken ct)
    {
        if (artist.Length == 0 || track.Length == 0) return null;
        var json = await HttpTextAsync(
            $"https://api.lyrics.ovh/v1/{Uri.EscapeDataString(artist)}/{Uri.EscapeDataString(track)}", null, null, ct);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("lyrics", out var l) ? CleanPlain(l.GetString() ?? "") : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? CleanPlain(string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return null;
        text = OvhHeaderRe().Replace(text, "").Trim();   // lyrics.ovh 헤더 제거(타 소스엔 무해)
        return text.Length == 0 ? null : text;
    }

    // GET/POST 로 원문 텍스트(JSON·HTML)를 받는다. 소스별 헤더(Referer/Cookie/UA)를 요청에 실어 보낸다.
    private async Task<string?> HttpTextAsync(
        string url, IReadOnlyList<(string Key, string Value)>? headers, HttpContent? body, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
            if (body is not null) req.Content = body;
            if (headers is not null)
                foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            log.LogDebug("internet lyric unreachable: {Msg}", e.Message);
            return null;
        }
    }

    // ---------- 매칭 판정 (Python 대응) ----------

    private LrclibItem? PickBest(List<LrclibItem> results, int duration, List<string> trackCands, List<string> artistCands, string title)
    {
        LrclibItem? best = null;
        (int, int, double, double, int, double) bestKey = default;
        var have = false;
        foreach (var item in results)
        {
            var text = item.SyncedLyrics ?? item.PlainLyrics;
            if (string.IsNullOrEmpty(text)) continue;
            if (title.Length > 0 && !LyricsLanguageOk(title, text!)) continue;
            var sim = trackCands.Select(t => Similar(item.TrackName ?? "", t)).DefaultIfEmpty(0).Max();
            var artistSim = LrclibArtistSim(item, artistCands);
            double? diff = (duration > 0 && item.Duration is > 0) ? Math.Abs(item.Duration.Value - duration) : null;
            if (!LrclibMatchOk(sim, artistSim, artistCands.Count > 0, diff)) continue;
            var synced = item.SyncedLyrics is { Length: > 0 } ? 1 : 0;
            var durScore = DurationScore(diff);
            var durBucket = durScore >= 0.9 ? 2 : durScore >= 0.4 ? 1 : 0;
            var matchRank = sim >= 0.85 ? 3 : sim >= 0.6 ? 2 : 1;
            var key = (matchRank, durBucket, Math.Round(sim, 1), Math.Round(artistSim, 1), synced,
                       -(diff ?? 99999));
            if (!have || Cmp(key, bestKey) > 0) { bestKey = key; best = item; have = true; }
        }
        return best;
    }

    private static int Cmp((int, int, double, double, int, double) a, (int, int, double, double, int, double) b)
    {
        int c;
        if ((c = a.Item1.CompareTo(b.Item1)) != 0) return c;
        if ((c = a.Item2.CompareTo(b.Item2)) != 0) return c;
        if ((c = a.Item3.CompareTo(b.Item3)) != 0) return c;
        if ((c = a.Item4.CompareTo(b.Item4)) != 0) return c;
        if ((c = a.Item5.CompareTo(b.Item5)) != 0) return c;
        return a.Item6.CompareTo(b.Item6);
    }

    private static bool LrclibResultOk(LrclibItem item, List<string> trackCands, List<string> artistCands, int duration)
    {
        var trackSim = trackCands.Select(t => Similar(item.TrackName ?? "", t)).DefaultIfEmpty(0).Max();
        var artistSim = LrclibArtistSim(item, artistCands);
        double? diff = (duration > 0 && item.Duration is > 0) ? Math.Abs(item.Duration.Value - duration) : null;
        return LrclibMatchOk(trackSim, artistSim, artistCands.Count > 0, diff);
    }

    private static bool LrclibMatchOk(double trackSim, double artistSim, bool hasArtistHints, double? durationDiff)
    {
        if (durationDiff is > DurationHard) return false;
        var durOk = durationDiff is null || durationDiff <= DurationOk;
        if (hasArtistHints && artistSim >= 0.85 && durationDiff is not null && durationDiff <= AliasSlack) return true;
        if (trackSim >= 0.85) return durOk || artistSim >= 0.5;
        if (trackSim < 0.6) return false;
        if (!hasArtistHints) return false;
        return artistSim >= 0.5 && durOk;
    }

    private static double LrclibArtistSim(LrclibItem item, List<string> artistCands)
    {
        var artist = item.ArtistName ?? "";
        if (artist.Length == 0 || artistCands.Count == 0) return 0;
        return artistCands.Select(c => Similar(artist, c)).DefaultIfEmpty(0).Max();
    }

    private static double DurationScore(double? diff)
    {
        if (diff is null) return 0.5;
        if (diff <= DurationNear) return 1.0;
        if (diff >= DurationHard) return 0.0;
        return Math.Max(0.0, 1.0 - (diff.Value - DurationNear) / (DurationHard - DurationNear));
    }

    private static List<string> MatchArtistCandidates(List<string> artists, List<string> trackCands)
    {
        var outList = new List<string>();
        foreach (var artist in artists)
        {
            if (string.IsNullOrEmpty(artist)) continue;
            if (trackCands.Select(t => Similar(artist, t)).DefaultIfEmpty(0).Max() >= 0.85) continue;
            if (outList.All(x => !x.Equals(artist, StringComparison.OrdinalIgnoreCase))) outList.Add(artist);
        }
        return outList;
    }

    // ---------- 제목 파싱 / 후보 추출 ----------


    // 인터넷 폴백용. LRC 시간태그(synced 판정·크레딧 제거), NetEase 크레딧 줄, Bugs HTML 파싱.
    [GeneratedRegex(@"\[\d{1,2}:\d{2}(?:[.:]\d{1,3})?\]")]
    private static partial Regex LrcTagRe();
    [GeneratedRegex(@"作词|作詞|作曲|编曲|編曲|制作|製作|出品|混音|母带|母帶|和声|和聲|录音|錄音|监制|監製|吉他|贝斯|键盘|弦乐|produced\s+by|composed\s+by|written\s+by|arranged\s+by|mixed\s+by|mastered\s+by|lyrics?\s*[:：]|작사|작곡|편곡|제작|믹싱|녹음|편곡자", RegexOptions.IgnoreCase)]
    private static partial Regex CreditRe();
    [GeneratedRegex(@"music\.bugs\.co\.kr/track/(\d+)")]
    private static partial Regex BugsTrackRe();
    [GeneratedRegex(@"lyricsContainer.*?<xmp[^>]*>(.*?)</xmp>", RegexOptions.Singleline)]
    private static partial Regex BugsLyricsRe();
    [GeneratedRegex(@"<xmp[^>]*>(.*?)</xmp>", RegexOptions.Singleline)]
    private static partial Regex BugsXmpRe();
    [GeneratedRegex(@"<meta\s+property=""og:title""\s+content=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex BugsOgTitleRe();
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrRe();
    [GeneratedRegex(@"^paroles de la chanson.*?\n", RegexOptions.IgnoreCase)]
    private static partial Regex OvhHeaderRe();

    /// <summary>
    /// (곡, 아티스트) 후보. <b>유튜브가 준 구조화 메타(music)가 있으면 그게 1순위</b>이고, 제목 문자열
    /// 파싱(TitleParser)은 그 뒤에 폴백으로 붙는다.
    ///
    /// 왜 이 순서인가: music 은 유튜브가 Content ID 로 식별한 값이라 커버 영상이어도 원곡명·원곡
    /// 아티스트가 나온다 — 제목에서 그걸 추측하는 게 TitleParser 가 가장 어려워하는 일이다.
    /// 다만 메타가 없는 영상(VTuber 커버·동인곡 등)이 흔해서 TitleParser 를 지우지는 않는다.
    ///
    /// AI 힌트(hint)는 <b>music 과 TitleParser 사이</b>에 들어간다. music 보다 뒤인 이유는 music 이
    /// 유튜브의 확정 식별값(추측이 아님)이라서고, TitleParser 보다 앞인 이유는 정확히 TitleParser 가
    /// 못 푸는 것(커버 영상의 원곡 아티스트)을 풀라고 넣은 것이기 때문이다.
    /// <b>대체가 아니라 보강</b>이다 — 규칙 기반 후보는 전부 뒤에 그대로 남아 AI 가 틀려도 길이 살아 있다.
    ///
    /// <b>예외 하나</b>: Content ID 도 오매칭한다(엉뚱한 곡이 붙은 영상이 실제로 있다). AI 가 메타를
    /// 보고 "이 영상의 곡이 아니다"라고 <i>확신할 때만</i>(MetaLooksWrong) 둘의 순서를 뒤집어 AI 답을
    /// 앞에 둔다. 메타를 버리지는 않는다 — 바로 뒤에 남겨 AI 가 틀렸을 때의 길을 남긴다.
    /// </summary>
    private static (List<string> Tracks, List<string> Artists) Candidates(
        string title, string? uploader, MusicMeta? music = null, AiTitleParser.AiTitle? hint = null)
    {
        var p = TitleParser.Parse(title, uploader);
        var tracks = p.Tracks;
        var artists = p.Artists;

        // 앞에 끼워 넣되 중복은 만들지 않는다(같은 값이 뒤에 또 있으면 제거).
        static List<string> Prepend(List<string> list, string? head)
        {
            if (string.IsNullOrWhiteSpace(head)) return list;
            var v = head.Trim();
            var rest = list.Where(x => !x.Equals(v, StringComparison.OrdinalIgnoreCase)).ToList();
            rest.Insert(0, v);
            return rest;
        }

        // Prepend 는 맨 앞에 끼우므로, 원하는 최종 순서의 '뒤'부터 넣어야 한다.
        // 기본:      music → hint → 규칙
        // 메타 불신: hint → music → 규칙
        var demoteMeta = hint?.MetaLooksWrong == true;
        var hasMusic = music is not null && !music.IsEmpty;

        if (demoteMeta && hasMusic)
        {
            tracks = Prepend(tracks, music!.Track);
            artists = Prepend(artists, music.Artist);
        }
        if (hint is not null)
        {
            tracks = Prepend(tracks, hint.Track);
            artists = Prepend(artists, hint.Artist);
        }
        if (!demoteMeta && hasMusic)
        {
            tracks = Prepend(tracks, music!.Track);
            artists = Prepend(artists, music.Artist);
        }
        return (tracks, artists);
    }

    private static bool MayNeedTranslatedTitleRetry(string title, string? uploader) =>
        ExpectedScript(title) is null && ExpectedScript(uploader ?? "") is not null;

    // ---------- 언어(문자 체계) 가드 ----------

    private static (int H, int K, int Han, int Lat) ScriptCounts(string text)
    {
        int h = 0, k = 0, han = 0, lat = 0;
        foreach (var ch in text ?? "")
        {
            int o = ch;
            if (o is (>= 0xAC00 and <= 0xD7A3) or (>= 0x1100 and <= 0x11FF) or (>= 0x3130 and <= 0x318F)) h++;
            else if (o is >= 0x3040 and <= 0x30FF) k++;
            else if (o is >= 0x4E00 and <= 0x9FFF) han++;
            else if (o is (>= 0x41 and <= 0x5A) or (>= 0x61 and <= 0x7A)) lat++;
        }
        return (h, k, han, lat);
    }

    private static string? ExpectedScript(string title)
    {
        var (h, k, han, lat) = ScriptCounts(title);
        if (h >= 2) return "ko";
        if (k >= 2) return "ja";
        if (han >= 2 && han >= lat) return "cjk";
        return null;
    }

    /// <summary>
    /// 언어 가드의 기준이 될 문자열 — <b>영상 제목이 아니라 곡 제목</b>이어야 한다.
    ///
    /// 실측 버그: "DAYBREAK FRONTLINE / 텐코 시부키(Tenko Shibuki) cover" 는 커버 가수 이름이 한글이라
    /// 제목 전체로 판정하면 기대 언어가 'ko' 가 되고, 그 결과 <b>올바른 일본어 원곡 가사가 거부</b>됐다.
    /// (AI 가 원곡을 Orangestar 로 정확히 맞혔는데도 source=none 으로 끝났다.)
    ///
    /// 그래서 후보 목록의 1순위 곡名(= 음악메타/AI/TitleParser 가 뽑은 원곡 제목)을 기준으로 쓴다.
    /// 그게 로마자면 기대 언어가 안 나오고 가드는 그냥 적용되지 않는다 — 근거 없이 거부하는 것보다 낫다.
    /// </summary>
    private static string LanguageRef(List<string> tracks, string title) =>
        tracks.Count > 0 && tracks[0].Length > 0 ? tracks[0] : title;

    private static bool LyricsLanguageOk(string title, string lyrics)
    {
        var expect = ExpectedScript(title);
        if (expect is null) return true;
        var (h, k, han, lat) = ScriptCounts(lyrics);
        var cjk = h + k + han;
        var letters = cjk + lat;
        if (letters < 20) return true;
        if ((double)cjk / letters < 0.15) return false;
        if (expect == "ko" && h == 0 && k >= cjk * 0.5) return false;
        if (expect == "ja" && k == 0 && h >= cjk * 0.5) return false;
        return true;
    }

    // ---------- 유사도 (difflib SequenceMatcher.ratio 대응) ----------

    // 문자열 유사도(Ratcliff-Obershelp). 제목 파싱과 같은 척도를 써야 해 TitleParser 것을 공유한다.
    private static double Similar(string a, string b) => TitleParser.Similar(a, b);
}
