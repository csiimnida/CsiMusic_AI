using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsiMusic.Web.Common;
using CsiMusic.Web.Data;
using CsiMusic.Web.Services.Ai;
using CsiMusic.Web.Options;
using Microsoft.Extensions.Options;
using Dapper;

namespace CsiMusic.Web.Services;

/// <summary>
/// 일본어 가사에 한글 발음·번역을 붙이는 작업의 <b>조립자</b>(docs/LYRICS_LAYERS_PLAN.md §2.2·§2.3·§2.6).
///
/// <see cref="AiLyricsLayers"/> 는 한 덩어리만 만든다. 여기서 그 앞뒤를 맡는다 —
/// 대상인지 가르고(§2.4), 청크로 쪼개고, 캐시를 보고, 결과를 <c>lyrics.layers</c> 에 넣는다.
///
/// <b>돈이 새지 않는 것이 이 클래스의 첫 번째 책임이다.</b> AI 를 부르기 전에 거를 수 있는 건 전부
/// 거른다: 일본어가 아니면 부르지 않고(§2.4), 이미 만들어 둔 곡은 다시 만들지 않으며(§2.6),
/// 같은 가사는 캐시로 0원에 재사용한다(§2.3). 실수로 버튼을 두 번 눌러도 두 번째는 공짜여야 한다.
/// </summary>
public sealed partial class LyricsLayersService(
    ISqliteConnectionFactory factory, AiLyricsLayers ai, AiCache cache, NamuLyricsSource namuSource,
    IOptions<AppOptions> options, ILogger<LyricsLayersService> log)
{
    /// <summary>한 번에 보낼 줄 수(§2.2). 출력 토큰 한계와 문맥 길이의 절충.</summary>
    private const int ChunkLines = 15;

    /// <summary>청크 경계에서 문맥이 끊기지 않도록 앞뒤로 겹쳐 보낼 줄 수(§2.5b-5).</summary>
    private const int ContextLines = 2;

    // 줄태그 [mm:ss.xx] 와 A2 단어태그 <mm:ss.xx>. app.js parseLRC 와 같은 것을 떼야
    // 층이 붙을 줄 텍스트가 화면의 줄 텍스트와 정확히 같아진다(§2.5).
    [GeneratedRegex(@"\[\d{1,2}:\d{2}(?:[.:]\d{1,3})?\]")]
    private static partial Regex LineTagRe();

    [GeneratedRegex(@"<\d{1,2}:\d{2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordTagRe();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRe();

    /// <param name="Ok">층을 만들어 저장했는지.</param>
    /// <param name="Reason">실패·생략의 사유(화면에 그대로 띄울 수 있는 한국어 한 줄).</param>
    /// <param name="Lines">대상 줄 수.</param>
    /// <param name="Filled">실제로 발음이나 번역이 붙은 줄 수.</param>
    /// <param name="ApiCalls">이번에 새로 시도한 덩어리 수 — 캐시 히트는 세지 않는다.
    /// 회로가 열려 있거나 일일 상한에 걸리면 요청이 나가지 않고도 여기 잡힌다(그때는 FailedChunks 도 같이 는다).</param>
    /// <param name="FailedChunks">검증에 걸려 버린 덩어리 수 — 0 이 아니면 그만큼 층이 비어 있다.</param>
    /// <param name="Entries">저장한 층. 화면이 다시 조회하지 않고 그대로 그릴 수 있게 실어 보낸다.</param>
    public sealed record Result(
        bool Ok, string? Reason, int Lines, int Filled, int ApiCalls, int FailedChunks,
        IReadOnlyList<AiLyricsLayers.LayerLine>? Entries = null,
        /// <summary>나무위키에서 가져온 층이 있으면 그 문서 제목 — 화면에 출처를 밝힌다(§9.7).</summary>
        string? LayersSource = null);

    /// <summary>
    /// 한 곡의 발음·번역을 만들어 저장한다(§2.6).
    ///
    /// <paramref name="force"/> 없이는 이미 층이 있는 곡을 다시 만들지 않는다 — 버튼을 두 번 눌러
    /// 돈이 새는 일이 없어야 한다. <c>locked</c> 인 가사는 사람이 손본 것이라 force 여도 건드리지 않는다.
    /// </summary>
    public async Task<Result> BuildAsync(string videoId, bool force = false, CancellationToken ct = default)
    {
        if (!ai.Enabled) return new Result(false, "AI 발음·번역이 꺼져 있습니다", 0, 0, 0, 0);

        using var conn = factory.Create();
        var row = await conn.QueryFirstOrDefaultAsync(
            "SELECT synced, plain, locked, layers FROM lyrics WHERE video_id = @vid", new { vid = videoId });
        if (row is null) return new Result(false, "가사가 없습니다", 0, 0, 0, 0);

        // 사람이 손본 가사는 덮지 않는다. force 로도 뚫지 않는다 — force 는 '다시 만들라'는 뜻이지
        // '내가 고친 것을 지우라'는 뜻이 아니다.
        if ((long?)row.locked is not null and not 0)
            return new Result(false, "잠긴 가사는 수정하지 않습니다", 0, 0, 0, 0);

        var hasLayers = !string.IsNullOrEmpty((string?)row.layers);
        if (hasLayers && !force)
            return new Result(false, "이미 만들어져 있습니다", 0, 0, 0, 0);

        var lines = SourceLines((string?)row.synced, (string?)row.plain);
        if (lines.Count == 0) return new Result(false, "가사 본문이 비어 있습니다", 0, 0, 0, 0);

        // AI 를 부르기 전에 거른다(§2.4·§7). 판별은 유니코드 범위만 보는 순수 함수다.
        var joined = string.Join("\n", lines);
        if (!JapaneseText.IsJapanese(joined))
            return new Result(false, "일본어 가사가 아닙니다", lines.Count, 0, 0, 0);

        // 1순위는 사람이 만든 것이다(§9). 나무위키에서 먼저 찾고, 못 채운 자리만 AI 가 만든다(§9.5).
        // 못 찾으면 null 이고 아래는 지금까지와 똑같이 AI 생성만으로 돈다.
        var track = await conn.QueryFirstOrDefaultAsync(
            "SELECT title, music_track, music_artist FROM tracks WHERE video_id = @vid", new { vid = videoId });
        var namu = track is null || !options.Value.NamuLayersEnabled ? null : await namuSource.FindAsync(
            (string)track.title, (string?)track.music_track, (string?)track.music_artist, lines, ct);

        // 캐시 키의 입력은 videoId 가 아니라 가사 본문의 해시다 — 가사가 교체되면 옛 발음이
        // 따라오면 안 된다(§2.3).
        var hash = Sha256(joined);

        var pron = await BuildLayerAsync(lines, hash, AiLyricsLayers.Layer.Pronunciation, namu, ct);
        var trans = await BuildLayerAsync(lines, hash, AiLyricsLayers.Layer.Translation, namu, ct);

        var entries = new List<AiLyricsLayers.LayerLine>(lines.Count);
        var filled = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var p = pron.Values[i];
            var t = trans.Values[i];
            if (p.Length > 0 || t.Length > 0) filled++;
            entries.Add(new AiLyricsLayers.LayerLine(lines[i], Nullify(p), Nullify(t)));
        }

        var apiCalls = pron.ApiCalls + trans.ApiCalls;
        var failedChunks = pron.Failed + trans.Failed;

        // 한 줄도 못 채웠으면 저장하지 않는다 — 빈 layers 를 넣어 두면 다음 요청이 '이미 있다'로
        // 막혀서 영영 다시 시도하지 못한다.
        if (filled == 0)
        {
            log.LogWarning("가사 발음·번역 실패 {Vid} — 채운 줄이 없다(API {Calls}회, 버린 덩어리 {Failed}개)",
                videoId, apiCalls, failedChunks);
            return new Result(false, "만들지 못했습니다 — 응답이 검증을 통과하지 못했습니다",
                lines.Count, 0, apiCalls, failedChunks);
        }

        // 한 층이라도 나무위키에서 베껴 왔으면 그 문서를 남긴다. 전부 AI 가 만들었으면 null 이다 —
        // 기여하지 않은 곳을 출처로 다는 것도 잘못이다(§9.7).
        var layersSource = pron.UsedNamu || trans.UsedNamu ? namu?.Document : null;

        var json = JsonSerializer.Serialize(entries);
        await conn.ExecuteAsync(
            "UPDATE lyrics SET layers = @layers, layers_source = @source WHERE video_id = @vid",
            new { vid = videoId, layers = json, source = layersSource });


        log.LogInformation("가사 발음·번역 저장 {Vid} — {Filled}/{Lines}줄 (API {Calls}회, 버린 덩어리 {Failed}개)",
            videoId, filled, lines.Count, apiCalls, failedChunks);
        return new Result(true, null, lines.Count, filled, apiCalls, failedChunks, entries, layersSource);
    }

    /// <summary>
    /// 덩어리 하나의 캐시 내용. <b>값과 함께 출처를 남긴다</b> — 나무위키에서 베껴 온 것이면
    /// 화면에 출처를 밝혀야 하는데(§9.7), 값만 캐시하면 다음 빌드에서 그 사실을 잃는다.
    /// </summary>
    internal sealed record Chunk(List<string> Values, bool FromNamu);

    /// <summary>한 층(발음 또는 번역) 전체를 청크로 나눠 만든다. 실패한 덩어리는 빈 값으로 남는다.</summary>
    private async Task<(string[] Values, int ApiCalls, int Failed, bool UsedNamu)> BuildLayerAsync(
        IReadOnlyList<string> lines, string hash, AiLyricsLayers.Layer layer, NamuLyricsSource.Found? namu, CancellationToken ct)
    {
        var values = new string[lines.Count];
        Array.Fill(values, "");
        int apiCalls = 0, failed = 0;
        var usedNamu = false;

        for (var start = 0; start < lines.Count; start += ChunkLines)
        {
            var len = Math.Min(ChunkLines, lines.Count - start);
            var index = start / ChunkLines;
            // 캐시 키에 문서 제목이 들어간다 — 나중에 더 맞는 문서로 바뀌면 옛 결과가 따라오지 않는다.
            var key = AiCache.Key("lyricslayer2", AiLyricsLayers.PromptVersion,
                hash, layer.ToString(), index.ToString(), namu?.Document ?? "-");

            var got = await cache.GetAsync<Chunk>(key);
            if (got?.Values is null || got.Values.Count != len)
            {
                var (made, calls, fromNamu) = await FillChunkAsync(lines, start, len, layer, namu, ct);
                apiCalls += calls;
                if (made is null)
                {
                    // 이 덩어리만 버린다(§2.5b-4). 캐시하지 않으므로 다시 누르면 다시 시도한다.
                    failed++;
                    continue;
                }
                got = new Chunk(made, fromNamu);
                await cache.SetAsync(key, got);
            }

            if (got.FromNamu) usedNamu = true;
            for (var i = 0; i < len; i++) values[start + i] = got.Values[i];
        }
        return (values, apiCalls, failed, usedNamu);

    }

    /// <summary>
    /// 한 덩어리를 채운다 — <b>나무위키에서 베껴 오고, 남은 줄만 AI 가 만든다</b>(§9.5).
    ///
    /// 나무위키가 그 덩어리를 전부 덮으면 생성 호출이 <b>아예 나가지 않는다</b>. 사람이 쓴 값이
    /// 들어가고 돈도 안 든다. 반대로 문서를 못 찾았거나 추출이 검증에 걸리면 지금까지와 똑같이
    /// 생성만으로 돈다 — 나무위키는 얹는 것이지 대체하는 것이 아니다.
    /// </summary>
    private async Task<(List<string>? Values, int Calls, bool FromNamu)> FillChunkAsync(
        IReadOnlyList<string> lines, int start, int len, AiLyricsLayers.Layer layer,
        NamuLyricsSource.Found? namu, CancellationToken ct)
    {
        var slice = Slice(lines, start, len);
        List<string>? values = null;
        var calls = 0;
        var fromNamu = false;

        if (namu is not null)
        {
            calls++;
            if (await ai.ExtractAsync(slice, namu.Excerpt, layer, ct) is { } got)
            {
                values = got.ToList();
                // 한 줄이라도 실제로 베껴 왔을 때만 출처로 인정한다 — 전부 빈 값이면
                // 나무위키가 기여한 게 없으므로 크레딧을 달면 안 된다(§9.7).
                fromNamu = values.Any(v => v.Length > 0);
            }
        }

        // 가사가 있는 줄인데 아직 비어 있으면 생성이 필요하다. 빈 줄·간주는 비어 있는 게 정답이라 세지 않는다.
        var needAi = values is null;
        if (values is not null)
            for (var i = 0; i < len; i++)
                if (values[i].Length == 0 && JapaneseText.Measure(slice[i]).Total > 0) { needAi = true; break; }
        if (!needAi) return (values, calls, fromNamu);

        calls++;
        var made = await AskAsync(lines, start, len, layer, ct);
        // 생성이 실패해도 나무위키가 준 것은 살린다 — 절반이라도 있는 편이 낫다.
        if (made is null) return (values, calls, fromNamu);
        if (values is null) return (made, calls, false);

        // 빈 자리만 생성값으로 메운다 — 나무위키가 준 줄은 그대로 둔다.
        for (var i = 0; i < len; i++)
            if (values[i].Length == 0) values[i] = made[i];
        return (values, calls, fromNamu);
    }


    /// <summary>
    /// 한 덩어리를 모델에 묻는다. 번역만 앞뒤 문맥을 겹쳐 보내고 <b>가운데만 취한다</b>(§2.5b-5) —
    /// 15줄에서 뚝 끊으면 이어지는 문장의 번역이 어색해진다. 발음은 문맥과 무관해 겹치지 않는다.
    /// </summary>
    private async Task<List<string>?> AskAsync(
        IReadOnlyList<string> lines, int start, int len, AiLyricsLayers.Layer layer, CancellationToken ct)
    {
        if (layer == AiLyricsLayers.Layer.Pronunciation)
        {
            var slice = Slice(lines, start, len);
            var got = await ai.BuildAsync(slice, layer, ct);
            return got?.ToList();
        }

        var from = Math.Max(0, start - ContextLines);
        var to = Math.Min(lines.Count, start + len + ContextLines);
        var window = Slice(lines, from, to - from);
        var result = await ai.BuildAsync(window, layer, ct);
        if (result is null) return null;
        return result.Skip(start - from).Take(len).ToList();
    }

    private static string[] Slice(IReadOnlyList<string> lines, int start, int len)
    {
        var slice = new string[len];
        for (var i = 0; i < len; i++) slice[i] = lines[start + i];
        return slice;
    }

    /// <summary>
    /// 층을 붙일 줄 목록. <b>화면이 실제로 그리는 줄과 같아야 한다.</b>
    ///
    /// 싱크 가사가 있으면 그쪽이 화면에 나오므로(app.js <c>parseLRC</c>) 태그를 뗀 줄 텍스트를 쓴다 —
    /// <c>attachLayers</c> 가 줄 텍스트로 짝을 찾기 때문에 공백 정규화까지 같아야 붙는다.
    /// 싱크가 없으면 plain 이 그대로 그려지므로(<c>renderLyrics</c>) 원문을 손대지 않고 쓴다.
    /// </summary>
    private static List<string> SourceLines(string? synced, string? plain)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(synced))
        {
            foreach (var raw in synced.Split('\n'))
            {
                // 시각태그가 없는 줄은 parseLRC 가 버린다(메타 [ar:] 포함) — 여기서도 버려야 짝이 맞는다.
                if (!LineTagRe().IsMatch(raw)) continue;
                var body = WordTagRe().Replace(LineTagRe().Replace(raw, ""), "");
                result.Add(SpaceRe().Replace(body, " ").Trim());
            }
            if (result.Count > 0) return result;
        }

        if (!string.IsNullOrWhiteSpace(plain))
            foreach (var raw in plain.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
                result.Add(raw.TrimEnd());
        return result;
    }

    private static string? Nullify(string s) => s.Length == 0 ? null : s;

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
