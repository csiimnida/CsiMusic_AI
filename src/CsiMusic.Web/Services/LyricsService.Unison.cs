using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CsiMusic.Web.Services;

/// <summary>
/// Unison(https://unison.boidu.dev) 가사 조회 — Better Lyrics 확장이 쓰는 크라우드소싱 가사 API.
///
/// LRCLIB 과 결정적으로 다른 점: <b>YouTube videoId 로 직접 조회</b>한다. 보관함이 전부 videoId 기반이라
/// 제목/아티스트 퍼지 매칭(FetchLrclibAsync 의 후보 생성·유사도·길이 가드)을 건너뛰고 정확 매칭이 된다.
/// TTML richsync(단어 단위 타이밍)도 주는데, 이건 Enhanced LRC(A2) 로 변환해 저장한다 —
/// 프론트 parseLRC 가 이미 인라인 &lt;mm:ss.xx&gt; 단어태그를 읽으므로 그대로 가라오케 하이라이트가 된다.
///
/// 읽기는 무인증(투표/제보만 서명 필요)이라 키가 필요 없다. best-effort — 실패·차단이면 조용히 null.
///
/// 데이터 라이선스: ODbL-1.0. 표시할 때 출처 표기가 필요하다 —
/// "Lyrics from Unison (https://unison.boidu.dev)". source 값 "unison" 이 그 표기의 근거다.
/// </summary>
public sealed partial class LyricsService
{
    private const string UnisonBase = "https://unison.boidu.dev/lyrics";
    // videoId 가 어긋난(메타데이터로 매칭된) 결과에만 적용하는 곡名 유사도 하한.
    private const double UnisonTitleSim = 0.6;

    public sealed record UnisonLyric(string? Synced, string? Plain, bool ExactVideoId, string? Track);

    /// <summary>Unison 조회. 정확 매칭(videoId 일치)이면 그대로, 메타데이터 매칭이면 곡名·언어 가드를 건다.</summary>
    private async Task<UnisonLyric?> FetchUnisonAsync(
        string videoId, string title, string? uploader, int duration, MusicMeta? music,
        Ai.AiTitleParser.AiTitle? hint, CancellationToken ct)
    {
        var (tracks, artists) = Candidates(title, uploader, music, hint);
        var track0 = tracks.Count > 0 ? tracks[0] : "";
        var artistCands = MatchArtistCandidates(artists, tracks);
        var artist0 = artistCands.Count > 0 ? artistCands[0] : "";

        // videoId 가 주 키. song/artist/duration 은 videoId 로 못 찾았을 때 서버가 쓰는 폴백 힌트다.
        var qs = new StringBuilder("?v=").Append(Uri.EscapeDataString(videoId));
        if (track0.Length > 0) qs.Append("&song=").Append(Uri.EscapeDataString(track0));
        if (artist0.Length > 0) qs.Append("&artist=").Append(Uri.EscapeDataString(artist0));
        if (duration > 0) qs.Append("&duration=").Append(duration);

        var json = await HttpTextAsync(UnisonBase + qs, null, null, ct);
        if (json is null) return null;   // 404(가사 없음) 포함 — 비성공 응답은 HttpTextAsync 가 null

        string? raw, format, respVid, respSong;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;
            raw = Str(data, "lyrics");
            format = Str(data, "format");
            respVid = Str(data, "videoId");
            respSong = Str(data, "song");
        }
        catch (JsonException) { return null; }
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrEmpty(format)) return null;

        var exact = string.Equals(respVid, videoId, StringComparison.Ordinal);

        var parsed = format switch
        {
            "ttml" => TtmlToLrc(raw!),
            "lrc" => NormalizeUnisonLrc(raw!),
            "plain" => (Synced: (string?)null, Plain: CleanPlain(raw)),
            _ => (null, null),
        };
        var text = parsed.Synced ?? parsed.Plain;
        if (string.IsNullOrEmpty(text)) return null;

        // videoId 가 일치하면 그 곡이 맞다는 뜻이라 추가 가드 없이 받는다.
        // 어긋났으면(메타데이터 폴백 매칭) 곡名 유사도 + 언어(문자체계) 가드로 오매칭을 막는다.
        if (!exact)
        {
            if (track0.Length > 0 && Similar(respSong ?? "", track0) < UnisonTitleSim) return null;
            if (!LyricsLanguageOk(LanguageRef(tracks, title), text!)) return null;
        }

        return new UnisonLyric(parsed.Synced, parsed.Plain, exact, respSong);
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------- Unison LRC 정규화 ----------

    /// <summary>Unison 의 LRC 를 싱크/플레인으로 가른다. 타임태그가 충분하면 싱크(인라인 단어태그는 보존).</summary>
    private static (string? Synced, string? Plain) NormalizeUnisonLrc(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return (null, null);
        if (LrcTagRe().Matches(text).Count >= 4) return (text, null);
        // 타임태그가 거의 없으면 싱크로 쓸 수 없다 — 태그를 벗겨 플레인으로.
        var stripped = UnisonWordTagRe().Replace(LrcTagRe().Replace(text, ""), "");
        return (null, CleanPlain(stripped));
    }

    // ---------- TTML → Enhanced LRC(A2) ----------

    // 한 줄을 이루는 조각. Time 이 있으면 그 지점부터 단어태그가 붙는다.
    private sealed record TtmlChunk(double? Time, string Text);

    /// <summary>
    /// TTML(Apple/Musixmatch 계열) 을 LRC 로 변환한다. &lt;p&gt; 가 한 줄이고, begin 이 붙은 말단 &lt;span&gt; 이
    /// 단어다. 단어 타이밍이 있으면 Enhanced LRC(A2) 인라인 태그로, 없으면 줄 단위 LRC 로 낸다.
    /// ttm:role 이 x- 로 시작하는 조각(배경보컬·번역 등)은 원문 줄을 오염시키므로 버린다.
    /// </summary>
    private static (string? Synced, string? Plain) TtmlToLrc(string ttml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(ttml, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException) { return (null, null); }
        if (doc.Root is null) return (null, null);

        var lines = new List<string>();
        var plain = new List<string>();
        var anyTiming = false;

        foreach (var p in doc.Root.Descendants().Where(e => e.Name.LocalName == "p"))
        {
            var chunks = new List<TtmlChunk>();
            CollectTtml(p, chunks);

            var body = string.Concat(chunks.Select(c => c.Text));
            var lineText = WhitespaceRe().Replace(body, " ").Trim();
            if (lineText.Length == 0) continue;
            plain.Add(lineText);

            var begin = TtmlTime(Attr(p, "begin"));
            if (begin is null) continue;
            anyTiming = true;

            // 줄태그는 마지막에 붙인다 — 들여쓰기(TTML 원문 공백)가 태그와 첫 단어 사이에 끼지 않도록.
            var sb = new StringBuilder();
            var wordTagged = false;
            foreach (var c in chunks)
            {
                var t = WhitespaceRe().Replace(c.Text, " ");
                if (c.Time is { } wt && t.Trim().Length > 0)
                {
                    // 단어 앞 공백은 태그 밖에 둔다 — 태그가 단어 시작을 가리켜야 하므로.
                    if (t.StartsWith(' ') && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                    sb.Append(LrcStamp(wt, '<', '>')).Append(t.TrimStart());
                    wordTagged = true;
                }
                else sb.Append(t);
            }

            var stamp = LrcStamp(begin.Value, '[', ']');
            // 단어태그가 하나도 없으면 줄 단위로만 낸다(정규화된 본문 사용).
            lines.Add(wordTagged ? stamp + sb.ToString().Trim() : stamp + lineText);
        }

        if (plain.Count == 0) return (null, null);
        return anyTiming && lines.Count >= 2
            ? (string.Join("\n", lines), null)
            : (null, CleanPlain(string.Join("\n", plain)));
    }

    /// <summary>&lt;p&gt; 안을 훑어 (시각, 텍스트) 조각으로 편다. begin 이 있는 말단 span 이 단어 한 개.</summary>
    private static void CollectTtml(XElement parent, List<TtmlChunk> outChunks)
    {
        foreach (var node in parent.Nodes())
        {
            switch (node)
            {
                case XText text:
                    if (text.Value.Length > 0) outChunks.Add(new TtmlChunk(null, text.Value));
                    break;

                case XElement el when el.Name.LocalName == "br":
                    outChunks.Add(new TtmlChunk(null, " "));
                    break;

                case XElement el:
                    // 배경보컬(x-bg)·번역 등 부가 역할은 원문 줄에 섞지 않는다.
                    var role = Attr(el, "role");
                    if (role is not null && role.StartsWith("x-", StringComparison.OrdinalIgnoreCase)) break;

                    var begin = TtmlTime(Attr(el, "begin"));
                    var hasTimedChild = el.Descendants().Any(d => TtmlTime(Attr(d, "begin")) is not null);
                    if (begin is not null && !hasTimedChild) outChunks.Add(new TtmlChunk(begin, el.Value));
                    else CollectTtml(el, outChunks);
                    break;
            }
        }
    }

    // 네임스페이스(ttm:, tts:, xml:)를 가리지 않고 로컬名으로 속성을 집는다.
    private static string? Attr(XElement el, string localName) =>
        el.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    /// <summary>TTML 시간 → 초. clock(hh:mm:ss.fff, mm:ss.fff)과 offset(12.5s, 500ms, 1m, 1h) 둘 다 받는다.</summary>
    private static double? TtmlTime(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return null;

        var offset = TtmlOffsetRe().Match(s);
        if (offset.Success)
        {
            if (!double.TryParse(offset.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)) return null;
            return offset.Groups[2].Value.ToLowerInvariant() switch
            {
                "h" => v * 3600, "m" => v * 60, "s" => v, "ms" => v / 1000.0,
                _ => null,   // f(프레임)·t(틱)는 프레임률/틱률을 알아야 해 지원하지 않는다
            };
        }

        // clock: [hh:]mm:ss[.fff] — 마지막 구분자가 ':' 인 프레임 표기(hh:mm:ss:ff)는 프레임을 버린다.
        var parts = s.Split(':');
        if (parts.Length is < 2 or > 4) return null;
        double total = 0;
        var secIdx = parts.Length >= 4 ? 2 : parts.Length - 1;   // 4개면 마지막은 프레임
        for (var i = 0; i <= secIdx; i++)
        {
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)) return null;
            total = total * 60 + v;
        }
        return total;
    }

    /// <summary>초 → LRC 타임스탬프. 줄태그는 [mm:ss.xx], 단어태그는 &lt;mm:ss.xx&gt;.</summary>
    private static string LrcStamp(double seconds, char open, char close)
    {
        if (seconds < 0) seconds = 0;
        var cs = (int)Math.Round(seconds * 100);          // 1/100초
        var mm = cs / 6000;
        var ss = cs % 6000 / 100;
        var ff = cs % 100;
        return $"{open}{mm:00}:{ss:00}.{ff:00}{close}";
    }

    [GeneratedRegex(@"^([0-9]*\.?[0-9]+)(h|ms|m|s|f|t)$", RegexOptions.IgnoreCase)]
    private static partial Regex TtmlOffsetRe();
    [GeneratedRegex(@"<\d{1,2}:\d{2}(?:[.:]\d{1,3})?>")]
    private static partial Regex UnisonWordTagRe();
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRe();
}
