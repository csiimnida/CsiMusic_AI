using System.Net;
using CsiMusic.Web.Common;
using System.Text.RegularExpressions;

namespace CsiMusic.Web.Services;

/// <summary>
/// 나무위키에서 곡 문서를 찾아 본문 텍스트를 가져온다(docs/LYRICS_LAYERS_PLAN.md §9.1·§9.2).
///
/// <b>이 클래스는 가사를 해석하지 않는다.</b> 문서를 찾아 태그를 걷어낸 텍스트까지만 만들고,
/// 거기서 발음·번역을 뽑아내는 일은 AI 가 한다(§9.4). 나무위키는 문서마다 마크업이 딴판이라
/// (줄마다 3행 · 3열 표 · 2열 · 접기 틀) 구조 파서를 만들면 유지가 불가능하기 때문이다.
///
/// 공개 경로만 쓴다. 내부 검색 API(<c>/i/…</c>)는 클라이언트 JS 가 만드는 봇 차단 토큰
/// (<c>x-chika</c>·<c>x-riko</c>·<c>x-you</c>)을 요구하므로 쓰지 않는다 — 공개 경로로
/// 검색·판정이 다 되므로 그럴 이유도 없다(§9.1).
///
/// 나무위키 본문은 CC BY-NC-SA 2.0 KR 이라 <b>출처 표기가 필요하다</b>(§9.7).
/// </summary>
public sealed partial class NamuWikiClient(HttpClient http, ILogger<NamuWikiClient> log)
{
    private const string Base = "https://namu.wiki";

    /// <summary>AI 에 보낼 본문 상한. 가사 구간만 잘라 보내므로 이만하면 넉넉하다.</summary>
    public const int MaxExcerptChars = 16000;

    /// <summary>검색 결과에서 곡 문서가 아닌 것이 섞여 나온다 — 이름공간으로 거른다(§9.2).</summary>
    private static readonly string[] SkipPrefixes =
        ["사용자:", "틀:", "분류:", "파일:", "나무위키:", "토론:", "역사:"];

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRe();

    // 후리가나(<ruby>神<rt>かみ</rt></ruby>)의 <rt>·<rp> 는 읽기 표시라 <b>본문에서 빼야 한다.</b>
    // 그냥 태그만 걷어내면 읽기가 한자 사이에 끼어들어(神かみっぽいな) 원문 대조가 전부 깨진다.
    [GeneratedRegex(@"<(rt|rp)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RubyReadingRe();

    // 줄바꿈을 만드는 태그 — 떼기 전에 개행으로 바꿔야 줄이 뭉치지 않는다.
    [GeneratedRegex(@"<(br|/p|/div|/td|/tr|/li|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRe();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRe();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRunRe();
    // "3. 가사", "가사[편집]", ". 가사" 처럼 나온다 — 편집 링크가 붙어 오므로 꼬리를 허용한다.
    // 본문 문장에 든 '가사'(…가사의 내용과 MV가…)를 잡지 않도록 줄 전체를 앵커로 묶는다.
    [GeneratedRegex(@"^(?:[\d.]+\s*)?가사\s*(?:\[편집\])?$")]
    private static partial Regex LyricsHeadingRe();

    // 접기 틀 안에 든 가사 — 문단 제목이 아예 없는 문서가 있다(夜に駆ける).
    [GeneratedRegex(@"^\[?\s*가사\s*(?:보기|열기)")]
    private static partial Regex LyricsFoldRe();

    [GeneratedRegex(@"href=""/w/([^""#]+)""")]
    private static partial Regex DocHrefRe();

    /// <summary>문서 본문 HTML. 없는 문서(404)면 null — 나무위키는 없는 문서에 404 를 준다(§9.1).</summary>
    public async Task<string?> FetchAsync(string title, CancellationToken ct = default)
    {
        var url = $"{Base}/w/{Uri.EscapeDataString(title)}";
        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode)
            {
                log.LogDebug("나무위키 조회 실패 {Status}: {Title}", (int)resp.StatusCode, title);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogDebug("나무위키 조회 예외 [{Title}]: {Msg}", title, e.Message);
            return null;
        }
    }

    /// <summary>
    /// 전문검색으로 후보 문서 제목을 찾는다(§9.2 의 <b>주 경로</b>).
    ///
    /// 나무위키는 문서 제목이 한국어라 일본어 원제로 <c>/w/</c> 직행이 빗나가는 일이 흔하다
    /// (`アクマバライ` → 문서는 `구마(음성 합성 엔진 오리지널 곡)`). 전문검색은 본문에 원제가
    /// 있으면 찾아내므로 이쪽이 주 경로다.
    /// </summary>
    public async Task<IReadOnlyList<string>> SearchAsync(
        string query, int max = 8, CancellationToken ct = default)
    {
        var url = $"{Base}/Search?q={Uri.EscapeDataString(query)}";
        string html;
        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return [];
            html = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogDebug("나무위키 검색 예외 [{Query}]: {Msg}", query, e.Message);
            return [];
        }
        return ParseSearchResults(html, max);
    }

    /// <summary>검색 결과 HTML 에서 문서 제목만 뽑는다. 순서(=관련도)를 지키고 중복·이름공간을 거른다.</summary>
    internal static IReadOnlyList<string> ParseSearchResults(string html, int max = 8)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<string>();
        foreach (Match m in DocHrefRe().Matches(html))
        {
            var title = Uri.UnescapeDataString(m.Groups[1].Value);
            if (SkipPrefixes.Any(p => title.StartsWith(p, StringComparison.Ordinal))) continue;
            if (!seen.Add(title)) continue;
            found.Add(title);
            if (found.Count >= max) break;
        }
        return found;
    }

    /// <summary>HTML 에서 태그를 걷어낸 본문 텍스트. 구조를 해석하지 않고 줄만 살린다.</summary>
    internal static string ExtractText(string html)
    {
        var s = ScriptRe().Replace(html, "\n");
        s = RubyReadingRe().Replace(s, "");
        s = BreakRe().Replace(s, "\n");
        s = TagRe().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        var lines = s.Split('\n').Select(l => l.Replace(' ', ' ').Trim());
        s = string.Join("\n", lines);
        return BlankRunRe().Replace(s, "\n\n").Trim();
    }

    /// <summary>
    /// 가사 구간만 잘라 낸다. 곡이 큰 문서 안의 한 구획일 수 있어서(§9.2 의 구마) 통째로 보내면
    /// 토큰이 샌다.
    ///
    /// 세 단계로 찾는다 — 문단 제목 → 접기 틀 → <b>가나 밀도</b>. 마지막이 핵심이다:
    /// 한국어 문서에서 가나가 몰려 있는 곳이 곧 일본어 가사다. 마크업이 문서마다 딴판이라(§9.3)
    /// 구조를 보지 않고 글자만 보는 이 방법이 가장 잘 버틴다.
    /// </summary>
    internal static string LyricsExcerpt(string text, int maxChars = MaxExcerptChars)
    {
        var lines = text.Split('\n');
        var start = Array.FindIndex(lines, l => LyricsHeadingRe().IsMatch(l.Trim()));
        if (start < 0) start = Array.FindIndex(lines, l => LyricsFoldRe().IsMatch(l.Trim()));
        if (start < 0) start = DensestKanaStart(lines, maxChars);
        var body = string.Join("\n", lines.Skip(Math.Max(start, 0)));
        return body.Length > maxChars ? body[..maxChars] : body;
    }

    /// <summary>가나가 가장 많이 몰린 구간의 시작 줄. 창을 밀며 세기만 하므로 O(n).</summary>
    private static int DensestKanaStart(string[] lines, int window)
    {
        if (lines.Length == 0) return 0;
        var kana = Array.ConvertAll(lines, l => JapaneseText.Measure(l).Kana);
        int best = 0, bestKana = -1, sum = 0, chars = 0, end = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (end < i) { end = i; sum = 0; chars = 0; }
            // end == i 일 때는 길이를 무시하고 한 줄은 반드시 넣는다 — 안 그러면 창이 비어 돈다.
            while (end < lines.Length && (end == i || chars + lines[end].Length + 1 <= window))
            {
                sum += kana[end];
                chars += lines[end].Length + 1;
                end++;
            }
            if (sum > bestKana) { bestKana = sum; best = i; }
            if (end > i) { sum -= kana[i]; chars -= lines[i].Length + 1; }
        }
        return best;
    }
}
