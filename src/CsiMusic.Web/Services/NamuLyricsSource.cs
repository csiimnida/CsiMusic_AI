using System.Text.RegularExpressions;
using CsiMusic.Web.Common;

namespace CsiMusic.Web.Services;

/// <summary>
/// 곡에 맞는 나무위키 문서를 찾아 가사 구간을 돌려준다(docs/LYRICS_LAYERS_PLAN.md §9.2).
///
/// <b>찾았다는 판단을 제목 유사도로 하지 않는다.</b> 후보 문서를 실제로 열어 <b>우리 가사 줄이
/// 그 안에 있는지 세어</b> 고른다(§9.4). 나무위키는 문서 제목이 한국어라 제목만으로는 맞는지
/// 알 수 없고(`アクマバライ` → `구마(음성 합성 엔진 오리지널 곡)`), 동명이곡·번안곡·리듬게임
/// 수록 문서가 검색 결과에 섞여 나오기 때문이다.
///
/// <see cref="AiLyricsLayers"/> 와 분리해 둔 이유: 나중에 <b>가사 자체의 소스</b>로도 쓸 수 있다
/// (§9.6 — 보컬로이드 곡은 LRCLIB 에 없는데 나무위키에는 있는 경우가 흔하다).
/// </summary>
public sealed partial class NamuLyricsSource(NamuWikiClient namu, ILogger<NamuLyricsSource> log)
{
    /// <summary>우리 줄 중 이만큼이 문서 안에 있어야 '이 곡의 문서'로 인정한다.</summary>
    public const double MinLineMatch = 0.5;

    /// <summary>검색 결과 중 실제로 열어 볼 문서 수. 정답이 뒤에 있는 일이 흔해 넉넉히 본다(§9.9).</summary>
    private const int MaxCandidates = 6;

    // 유튜브 제목에서 곡명이 아닌 꼬리표들. 커버곡 제목이 길어 이걸 떼지 않으면 검색어가 망가진다.
    [GeneratedRegex(@"(?i)\b(covered?\s*by|cover|MV|Official|feat\.?|ft\.?|4K|3D\s*(LIVE|MV)?|" +
                    @"歌ってみた|オリジナル(楽曲|MV)?|Music\s*Video)\b")]
    private static partial Regex NoiseRe();

    [GeneratedRegex(@"[\[［(（【]([^\]］)）】]{2,40})[\]］)）】]")]
    private static partial Regex BracketRe();

    /// <param name="Document">나무위키 문서 제목.</param>
    /// <param name="Excerpt">그 문서의 가사 구간 텍스트.</param>
    /// <param name="LineMatch">우리 줄 중 문서 안에서 찾은 비율 — 로그·판단 근거.</param>
    public sealed record Found(string Document, string Excerpt, double LineMatch);

    /// <summary>
    /// 곡 문서를 찾는다. 못 찾으면 null — 호출측은 AI 생성으로 폴백한다(§9.5).
    /// </summary>
    /// <param name="title">유튜브 제목(커버곡이면 원곡명이 여기 섞여 있다).</param>
    /// <param name="musicTrack">유튜브 구조화 메타의 곡명. 있으면 가장 믿을 만한 검색어다.</param>
    /// <param name="ourLines">우리 가사 줄 — 문서가 맞는지 대조하는 기준이다.</param>
    public async Task<Found?> FindAsync(
        string title, string? musicTrack, string? musicArtist,
        IReadOnlyList<string> ourLines, CancellationToken ct = default)
    {
        var probe = ourLines.Where(l => JapaneseText.Measure(l).Kana > 0).ToList();
        if (probe.Count == 0) return null;

        var queries = Queries(title, musicTrack, musicArtist);
        if (queries.Count == 0)
        {
            log.LogDebug("나무위키 검색어를 못 뽑았다: {Title}", title);
            return null;
        }

        var seen = new List<string>();
        foreach (var q in queries)
            foreach (var doc in await namu.SearchAsync(q, MaxCandidates, ct))
                if (!seen.Contains(doc)) seen.Add(doc);

        Found? best = null;
        foreach (var doc in seen.Take(MaxCandidates))
        {
            var html = await namu.FetchAsync(doc, ct);
            if (html is null) continue;
            var excerpt = NamuWikiClient.LyricsExcerpt(NamuWikiClient.ExtractText(html));
            if (excerpt.Length == 0) continue;

            var ratio = LineMatchRatio(probe, excerpt);
            if (best is null || ratio > best.LineMatch) best = new Found(doc, excerpt, ratio);
            // 충분히 맞으면 나머지 후보를 열지 않는다 — 남의 서버를 덜 두드린다(§9.7).
            if (ratio >= 0.9) break;
        }

        if (best is null || best.LineMatch < MinLineMatch)
        {
            log.LogDebug("나무위키 문서를 못 찾았다: {Title} (최고 일치 {Ratio:P0})", title, best?.LineMatch ?? 0);
            return null;
        }
        log.LogInformation("나무위키 문서 채택: {Doc} — 원문 일치 {Ratio:P0}", best.Document, best.LineMatch);
        return best;
    }

    /// <summary>가사가 없는 곡은 대조할 것이 없어 후보를 적게 본다 — 틀린 문서를 열수록 검증 비용만 는다.</summary>
    private const int LyricsCandidates = 3;

    /// <summary>가사 구간에 가나가 이만큼은 있어야 '가사가 실린 문서'로 본다.</summary>
    private const int MinKanaForLyrics = 60;

    /// <summary>
    /// <b>가사가 아예 없는 곡</b>을 위해 문서를 찾는다(계획 §9.6).
    ///
    /// <see cref="FindAsync"/> 와 결정적으로 다르다 — 대조할 원문이 없으므로 <b>여기서는 맞는
    /// 문서인지 확정할 수 없다.</b> 가나가 가장 많이 실린 후보를 돌려줄 뿐이고, 진짜 판정은
    /// 호출측이 <c>AiLyricsVerifier</c>(작업 B)로 한다. 그래서 <see cref="Found.LineMatch"/> 는
    /// 여기서 늘 0 이다.
    /// </summary>
    public async Task<Found?> FindForLyricsAsync(
        string title, string? musicTrack, string? musicArtist, CancellationToken ct = default)
    {
        var queries = Queries(title, musicTrack, musicArtist);
        if (queries.Count == 0) return null;

        var seen = new List<string>();
        foreach (var q in queries)
            foreach (var doc in await namu.SearchAsync(q, MaxCandidates, ct))
                if (!seen.Contains(doc)) seen.Add(doc);

        Found? best = null;
        var bestKana = 0;
        foreach (var doc in seen.Take(LyricsCandidates))
        {
            var html = await namu.FetchAsync(doc, ct);
            if (html is null) continue;
            var excerpt = NamuWikiClient.LyricsExcerpt(NamuWikiClient.ExtractText(html));
            var kana = JapaneseText.Measure(excerpt).Kana;
            if (kana < MinKanaForLyrics) continue;
            if (kana > bestKana) { bestKana = kana; best = new Found(doc, excerpt, 0); }
        }
        if (best is null) log.LogDebug("나무위키에서 가사 실린 문서를 못 찾았다: {Title}", title);
        else log.LogInformation("나무위키 가사 후보: {Doc} (가나 {Kana}) — 검증 대기", best.Document, bestKana);
        return best;
    }

    /// <summary>우리 줄 중 문서 본문에 실제로 있는 것의 비율. 공백·문장부호를 지우고 대조한다.</summary>
    internal static double LineMatchRatio(IReadOnlyList<string> ourLines, string excerpt)
    {
        if (ourLines.Count == 0) return 0;
        var flat = Squeeze(excerpt);
        var hit = ourLines.Count(l => flat.Contains(Squeeze(l), StringComparison.Ordinal));
        return (double)hit / ourLines.Count;
    }

    private static string Squeeze(string s) =>
        new(s.Where(ch => !char.IsWhiteSpace(ch) && !char.IsPunctuation(ch) && ch != '　').ToArray());

    /// <summary>
    /// 검색어 후보를 뽑는다. <b>커버곡이 많아 유튜브 제목을 그대로 쓰면 안 된다</b> —
    /// 실측에서 `群青 - YOASOBI / covered by しぐれうい` 의 검색어가 커버 가수로 잡혀
    /// 문서를 놓쳤다(§9.9).
    /// </summary>
    /// <summary>
    /// 검색어 후보를 뽑는다.
    ///
    /// <b>조각마다 일본어를 요구하면 안 된다.</b> 나무위키는 한국어 위주라 곡 문서 제목이
    /// 한국어이거나 영문인 경우가 흔하고(`Earth Sci.` → `지구과학(VOCALOID 오리지널 곡)`),
    /// 그런 조각을 버리면 <b>남는 게 보컬 이름뿐</b>이 된다. 실제로 `初音ミク` 로만 검색해
    /// 엉뚱한 미쿠 곡을 물어 왔다. 일본어 여부는 제목 <i>전체</i>로 한 번만 본다.
    ///
    /// 가수·보컬 이름은 검색어에서 뺀다 — 곡이 아니라 그 사람이 참여한 문서가 전부 걸린다.
    /// </summary>
    internal static List<string> Queries(string title, string? musicTrack, string? musicArtist = null)
    {
        // 제목 어디에도 일본어가 없으면 애초에 대상이 아니다(네트워크 요청 없이 끝난다).
        var whole = JapaneseText.Measure(title + " " + (musicTrack ?? ""));
        if (whole.Kana == 0 && whole.Han < 2) return [];

        var qs = new List<string>();

        // 유튜브 구조화 메타의 곡명이 가장 믿을 만하다 — 제목 파싱을 거치지 않은 값이다.
        if (!string.IsNullOrWhiteSpace(musicTrack)) Add(qs, musicTrack);

        // 커버곡은 원곡명이 대괄호 안에 오는 일이 많다: 메이드지상주의 [メイド☆至上主義]ㅣ…
        // 다만 괄호 안이 커버 가수인 경우도 그만큼 많아서(…(covered by 犬山たまき)) 잡음 제거를 똑같이 건다.
        foreach (Match m in BracketRe().Matches(title)) Add(qs, m.Groups[1].Value);

        // 그 밖에는 구분자로 잘라 각 조각을 후보로 본다.
        foreach (var seg in Regex.Split(title, @"[/|ㅣ｜\[\]（）()【】]")) Add(qs, seg);

        // 곡명은 대개 제목 맨 앞에 온다 — 앞에 있던 것부터 검색해야 첫 시도에서 맞고 요청도 덜 나간다.
        var meta = string.IsNullOrWhiteSpace(musicTrack) ? null : qs.FirstOrDefault();
        return qs.Where(q => !IsArtist(q, musicArtist))
                 .Distinct()
                 .OrderBy(q => q == meta ? -1 : title.IndexOf(q, StringComparison.Ordinal) is var i and >= 0 ? i : int.MaxValue)
                 .Take(3).ToList();
    }

    /// <summary>가수 이름과 같은 조각인가. 그것으로 검색하면 그 사람이 참여한 문서가 전부 걸린다.</summary>
    private static bool IsArtist(string q, string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return false;
        var a = artist.Trim();
        return q.Equals(a, StringComparison.OrdinalIgnoreCase)
               || (a.Length >= 3 && q.Contains(a, StringComparison.OrdinalIgnoreCase) && q.Length <= a.Length + 4);
    }


    /// <summary>꼬리표를 떼고 후보에 넣는다. 남는 게 없으면 버린다.</summary>
    private static void Add(List<string> qs, string raw)
    {
        var t = NoiseRe().Replace(raw, " ");
        t = Regex.Replace(t, @"\s{2,}", " ").Trim(' ', '-', '~', '·', ',', '.', '\'', '"', '“', '”', '‘', '’', '「', '」');
        if (t.Length is >= 2 and <= 40) qs.Add(t);
    }
}
