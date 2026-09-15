using System.Text.RegularExpressions;

namespace CsiMusic.Web.Services;

/// <summary>
/// 유튜브 영상 제목에서 <b>곡 제목 / 아티스트</b> 후보를 뽑는다. 가사 검색(LRCLIB·Unison)의 입력.
///
/// 왜 별도 클래스인가: 여기가 가사 검색 품질의 절반을 결정하는데 순수 문자열 처리라 네트워크·DB 없이
/// 단독 검증이 가능하다. LyricsService 안에 있으면 테스트할 방법이 없어 밖으로 뺐다.
///
/// 핵심 원칙 — <b>커버곡은 '부른 사람'이 아니라 '원곡 아티스트'로 검색해야 한다.</b>
/// 가사는 원곡의 것이므로 커버 가수 이름으로 조회하면 못 찾거나 동명의 다른 곡이 붙는다.
/// 그래서 커버 크레딧(`covered by …`, `歌ってみた`, `커버`)은 후보에서 <b>제거</b>하고, 업로더도
/// 커버 영상에서는 아티스트 후보로 쓰지 않는다.
/// </summary>
public static partial class TitleParser
{
    /// <param name="Tracks">곡 제목 후보(우선순위 순). 비어 있으면 가사 검색을 포기해야 한다.</param>
    /// <param name="Artists">아티스트 후보(우선순위 순). 커버면 원곡 아티스트만 담기고, 모르면 빈 목록.</param>
    /// <param name="IsCover">커버/불러봤다 영상인지.</param>
    /// <param name="CoverBy">커버한 사람(있으면). 후보에서는 빠지고 로그·표시용으로만 쓴다.</param>
    public sealed record Result(
        List<string> Tracks, List<string> Artists, bool IsCover, string? CoverBy);

    // 업로더가 곡 제목/아티스트와 같은 사람인지 볼 때의 문턱. 낮추면 오탐(다른 사람을 아티스트로 확정)이 는다.
    private const double UploaderMatch = 0.75;

    public static Result Parse(string? rawTitle, string? uploader)
    {
        var title = (rawTitle ?? "").Trim();

        // ---- 1) 커버 크레딧을 '먼저' 떼어낸다 ------------------------------------------------
        // 순서가 중요하다: 떼기 전에 구분자로 쪼개면 "covered by しぐれうい" 가 통째로 곡 제목
        // 후보가 돼 버린다(실측 사례). 먼저 제거해야 남은 조각이 진짜 제목/아티스트가 된다.
        string? coverBy = null;
        var isCover = false;
        // ① 이름이 붙은 형태(`covered by X`, `커버: X`) — 이름까지 통째로 제거.
        var work = CoverCreditRe().Replace(title, m =>
        {
            isCover = true;
            var who = m.Groups["who"].Value.Trim(' ', '-', '–', '—', ':', '：', '/', '|', '"', '\'');
            if (who.Length > 0 && coverBy is null) coverBy = who;
            return " ";
        });
        // ② 이름 없는 표식(`(Cover)`, `【歌ってみた】`) — 표식 단어만 지운다. 뒤에 오는 말은 곡 제목이므로
        //    같이 지우면 안 된다(`【歌ってみた】強風オールバック` 에서 제목이 통째로 날아갔던 버그).
        work = CoverMarkRe().Replace(work, m => { isCover = true; return " "; });

        // ---- 2) 원곡 아티스트 힌트(있으면 가장 강한 단서) ------------------------------------
        var artistHints = new List<string>();
        work = OriginalArtistRe().Replace(work, m =>
        {
            var who = m.Groups["who"].Value.Trim(' ', '-', '–', '—', '"', '\'');
            if (who.Length > 0) artistHints.Add(who);
            return " ";
        });
        // `YOASOBI「アイドル」` / `NewJeans (뉴진스) 'Ditto'` 처럼 곡명 따옴표 <b>앞</b>에 오는 이름 = 아티스트.
        // 따옴표 종류(전각·직선)를 가리지 않도록, 첫 따옴표 앞부분을 잘라 마지막 조각을 본다.
        var firstQuote = QuotedRe().Match(work);
        if (firstQuote.Success && firstQuote.Index > 0)
        {
            var before = Clean(ParenRe().Replace(work[..firstQuote.Index], " "));
            var beforeSegs = SplitSegments(before);
            if (beforeSegs.Count > 0 && beforeSegs[^1].Length <= 40) artistHints.Add(beforeSegs[^1]);
        }

        // ---- 3) 괄호 / 따옴표 / 구분자 분해 ---------------------------------------------------
        var parens = ParenRe().Matches(work).Select(m => m.Groups[1].Value.Trim())
            .Where(p => p.Length > 0).ToList();
        var quoted = QuotedRe().Matches(work).Select(m => m.Groups[1].Value.Trim())
            .Where(q => q.Length > 0).ToList();
        var noparen = Clean(ParenRe().Replace(work, " "));
        var segs = SplitSegments(noparen);

        // 괄호 안이 `A - B` 꼴이면 (아티스트, 곡) 쌍으로 본다.
        var parenPairs = new List<(string Artist, string Track)>();
        foreach (var p in parens)
        {
            var sub = SplitSegments(p);
            if (sub.Count >= 2) parenPairs.Add((sub[0], sub[^1]));
        }

        // ---- 4) 어느 조각이 아티스트인가 -------------------------------------------------------
        // 기존 구현은 무조건 segs[0]=아티스트로 봤는데, "곡명 - 아티스트" 순서인 제목이 흔해 자주 뒤집혔다.
        // 업로더·따옴표라는 실제 근거가 있으면 그걸로 정하고, 근거가 없을 때만 관례(앞=아티스트)를 쓴다.
        var uploaderName = string.IsNullOrEmpty(uploader)
            ? "" : UploaderSuffixRe().Replace(uploader, "").Trim();

        string? artistSeg = null;
        var trackSegs = new List<string>();
        if (segs.Count >= 2)
        {
            var byUploader = uploaderName.Length > 0
                ? segs.FirstOrDefault(s => Similar(s, uploaderName) >= UploaderMatch || StartsWithName(uploaderName, s))
                : null;
            // 따옴표로 곡명이 명시됐으면, 그와 닮은 조각이 곡이고 나머지가 아티스트다.
            var byQuote = quoted.Count > 0
                ? segs.FirstOrDefault(s => quoted.Any(q => Similar(s, q) >= 0.85))
                : null;

            if (byUploader is not null) artistSeg = byUploader;
            else if (byQuote is not null) artistSeg = segs.FirstOrDefault(s => s != byQuote);
            // 커버 영상에서 `/` 로 나뉜 제목은 일본 `歌ってみた` 관례상 `곡명 / 원곡아티스트` 순이다
            // (`-` 로 나뉜 일반 뮤비의 `아티스트 - 곡명` 과 반대).
            else if (isCover && SlashSepRe().IsMatch(noparen)) artistSeg = segs[^1];
            else artistSeg = segs[0];                          // 근거 없음 → 관례(Artist - Title)

            trackSegs.AddRange(segs.Where(s => s != artistSeg));
        }
        else
        {
            // 조각이 하나뿐이면 그건 곡 제목이다. 아티스트 후보로도 쓰면 곡명으로 아티스트를 검색하게 된다.
            trackSegs.AddRange(segs);
        }

        // ---- 5) 후보 조립(우선순위 순) ---------------------------------------------------------
        var tracks = new List<string>();
        var artists = new List<string>();

        foreach (var q in quoted)                               // 따옴표가 가장 확실한 곡명
        {
            // `'ニャニャニャチュニャ (NNNCN)'` 처럼 곡명에 별칭 괄호가 붙는 경우가 흔하다.
            // 괄호를 뗀 쪽이 대개 정식 곡명이라 먼저 시도한다.
            var bare = Clean(ParenRe().Replace(q, " "));
            if (bare.Length > 0 && !bare.Equals(q, StringComparison.OrdinalIgnoreCase)) Add(tracks, bare);
            Add(tracks, q);
        }
        foreach (var s in trackSegs) Add(tracks, s);
        foreach (var (_, trk) in parenPairs) Add(tracks, trk);

        foreach (var a in artistHints) Add(artists, a);          // 原曲:/원곡: · 따옴표 앞 이름
        if (artistSeg is not null) Add(artists, artistSeg);
        foreach (var (art, _) in parenPairs) Add(artists, art);

        // 여기부터는 확신이 낮은 폴백 — 앞뒤가 뒤집힌 제목이나 괄호 안에 곡명이 든 경우를 위해
        // 남은 조각을 양쪽에 모두 넣는다. 위에서 이미 담긴 값은 Add 가 중복으로 걸러낸다.
        // 단 조각이 하나뿐이면 넣지 않는다 — 그건 곡 제목이 확실하다.
        if (segs.Count >= 2)
            foreach (var s in segs) { Add(tracks, s); Add(artists, s); }
        foreach (var p in parens) { Add(tracks, p); Add(artists, p); }

        // 업로더는 커버가 아닐 때만 아티스트로 쓴다. 커버 영상의 업로더는 '부른 사람'이라
        // 원곡 가사 검색에 넣으면 오히려 해가 된다.
        if (!isCover && uploaderName.Length > 0) Add(artists, uploaderName);

        // 커버한 사람은 어떤 경로로 들어왔든 아티스트 후보에서 뺀다.
        if (coverBy is { Length: > 0 })
            artists.RemoveAll(a => Similar(a, coverBy) >= 0.85);

        return new Result(tracks, artists, isCover, coverBy);

        static void Add(List<string> list, string? value)
        {
            value = FeatRe().Replace(value ?? "", "").Trim().Trim('"', '\'').Trim();
            if (value.Length == 0) return;
            if (list.All(x => !x.Equals(value, StringComparison.OrdinalIgnoreCase))) list.Add(value);
        }
    }

    /// <summary>
    /// 화면·디스코드 카드에 보여 줄 "실제 곡 제목·가수"를 정한다.
    ///
    /// 가사 검색용 <see cref="Parse"/> 와 기준이 다르다: 가사는 <b>원곡</b> 아티스트로 찾아야 하지만,
    /// 화면에는 <b>지금 들리는 목소리</b>를 적어야 한다 — 커버 영상이면 원곡 가수가 아니라 커버한 사람이다.
    /// 유튜브 뮤직 구조화 메타(music_track/music_artist)가 있으면 그쪽을 먼저 믿는다.
    /// 아무것도 못 알아내면 null 을 돌려주고, 표시하는 쪽에서 원래 제목/채널명으로 폴백한다.
    ///
    /// <b>두 값은 반드시 짝으로 정해야 한다.</b> <see cref="Parse"/> 가 주는 Tracks/Artists 는
    /// 가사 검색 사다리용 후보 목록이라 <i>양쪽에 같은 조각이 들어 있다</i>. 각각 독립적으로 첫 항목을
    /// 뽑으면 같은 값이 두 필드에 다 들어가거나(메타가 한쪽만 있을 때) 좌우가 뒤바뀐 조합이 나온다.
    /// 그래서 한쪽이 정해지면 나머지는 <b>그것과 다른</b> 후보에서 고른다.
    /// </summary>
    public static (string? Song, string? Singer) ForDisplay(
        string? rawTitle, string? uploader, string? musicTrack, string? musicArtist) =>
        ForDisplay(Parse(rawTitle, uploader), rawTitle, musicTrack, musicArtist);

    /// <summary>
    /// 이미 <see cref="Parse"/> 한 결과로 표시용 값을 정한다. 커버 여부·커버한 사람까지 함께 봐야 하는
    /// 호출측(AI 보강)이 같은 제목을 두 번 파싱하지 않도록 나눠 뒀다.
    /// </summary>
    public static (string? Song, string? Singer) ForDisplay(
        Result parsed, string? rawTitle, string? musicTrack, string? musicArtist)
    {
        // 커버 영상에서 지금 들리는 목소리는 커버한 사람이다 — 메타의 원곡 아티스트가 아니라.
        var singer = parsed.IsCover ? Blank(parsed.CoverBy) : null;
        singer ??= Blank(musicArtist);
        var song = Blank(musicTrack);

        // 확실한 쪽(메타·커버 크레딧)을 먼저 박고, 남은 쪽을 그와 겹치지 않는 후보에서 고른다.
        // 이 순서 덕분에 메타가 한쪽만 있어도 제목의 어느 조각이 무엇인지 갈라진다.
        song ??= FirstUnlike(parsed.Tracks, singer) ?? FirstUnlike(parsed.Artists, singer);
        singer ??= FirstUnlike(parsed.Artists, song) ?? FirstUnlike(parsed.Tracks, song);

        return (WithTitleAlias(song, rawTitle), singer);
    }

    /// <summary>
    /// 원본 제목에서 곡명 <b>바로 뒤</b>에 붙어 있던 별칭·번역 괄호를 되살린다.
    ///
    /// 가사 검색 키로는 괄호를 뗀 <c>死神</c> 이 맞다 — <c>死神(사신)</c> 으로는 가사 DB 에서 못 찾는다.
    /// 하지만 화면에는 업로더가 적어 둔 <c>死神(사신)</c> 이 더 친절하다. 그래서 검색용 값은 그대로 두고
    /// 표시용으로만 괄호를 다시 붙인다.
    ///
    /// 곡명에 <b>인접한</b> 괄호만 되살린다. 제목 아무 데나 있는 괄호를 붙이면 "(Official MV)" 같은
    /// 홍보 문구가 딸려 온다.
    /// </summary>
    public static string? WithTitleAlias(string? song, string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(song) || string.IsNullOrWhiteSpace(rawTitle)) return song;

        var trimmed = song.Trim();
        var m = Regex.Match(
            Clean(rawTitle),
            Regex.Escape(trimmed) + @"\s*[(（\[]([^)）\]]{1,40})[)）\]]",
            RegexOptions.CultureInvariant);
        if (!m.Success) return song;

        var inside = m.Groups[1].Value.Trim();
        // feat. 크레딧은 곡명이 아니고, 로마자 표기처럼 곡명과 같은 말이면 붙일 이유가 없다.
        if (inside.Length == 0 || FeatRe().IsMatch(inside) || Similar(inside, trimmed) >= 0.85) return song;

        return m.Value.Trim();
    }

    /// <summary>
    /// 채널명이 이 조각으로 <b>시작</b>하는가.
    ///
    /// "아라하시 타비 ARAHASHI TABI" 처럼 이름 뒤에 로마자 표기를 덧붙인 채널이 흔하다. 그러면
    /// 조각("아라하시 타비")과의 전체 유사도가 뒤쪽 로마자에 희석돼 <see cref="UploaderMatch"/> 를
    /// 못 넘고, 업로더라는 근거가 있는데도 관례(앞=아티스트)로 찍어 좌우가 뒤집힌다
    /// ("死神(사신) / 아라하시 타비" 에서 死神 이 가수가 됐던 실측 사례).
    ///
    /// 채널명이 곡 제목으로 시작하는 경우는 드물어 오탐 위험이 낮다.
    /// </summary>
    private static bool StartsWithName(string uploaderName, string seg) =>
        seg.Length >= 2 && uploaderName.StartsWith(seg, StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="other"/> 와 사실상 같지 않은 첫 후보. 없으면 null.</summary>
    private static string? FirstUnlike(List<string> candidates, string? other) =>
        candidates.FirstOrDefault(c => other is null || Similar(c, other) < 0.85);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static List<string> SplitSegments(string s) =>
        SepRe().Split(s).Select(x => x.Trim(' ', '\'', '"')).Where(x => x.Length > 0).ToList();

    /// <summary>제목에서 홍보/포맷 노이즈(Official MV, 가사, 4K …)를 반복 제거한다.</summary>
    public static string Clean(string s)
    {
        string? prev = null;
        while (prev != s)
        {
            prev = s;
            s = NoiseRe().Replace(s, "").Trim();
            s = BareTailRe().Replace(s, "").Trim();
            s = TailRe().Replace(s, "").Trim();
        }
        return s.Trim(' ', '\'', '"', '-', '–', '—', '|', '/', '~', ':');
    }

    // ---------- 정규식 ----------

    // 커버 크레딧 중 <b>이름이 붙은</b> 형태만. `by` 나 `:` 가 반드시 있어야 이름을 삼킨다 —
    // 이 조건이 없으면 `【歌ってみた】強風オールバック` 에서 뒤따르는 곡 제목까지 크레딧으로 먹어버린다.
    // who 는 구분자·괄호(전각 포함) 전까지만 — 뒤에 다른 정보가 붙는 제목이 많다.
    [GeneratedRegex(
        @"[\(\[\{（［【]?\s*(?:cover(?:ed)?\s*by|커버\s*(?:by|[:：])|불러봄\s*by|歌ってみた\s*by|うたってみた\s*by)\s*[:：]?\s*(?<who>[^)\]\}/|「」『』【】［］（）]*)\s*[\)\]\}）］】]?",
        RegexOptions.IgnoreCase)]
    private static partial Regex CoverCreditRe();

    // 이름 없이 '커버다'만 표시하는 형태(`(Cover)`, `【歌ってみた】`, `커버`). 표식 단어만 지운다.
    [GeneratedRegex(@"\bcover(?:ed)?\b|커버|불러봄|불러보았\S*|불러봤\S*|歌ってみた|うたってみた|唄ってみた", RegexOptions.IgnoreCase)]
    private static partial Regex CoverMarkRe();

    // 커버 제목의 `곡명 / 아티스트` 관례 판정용.
    [GeneratedRegex(@"\s*/\s*")]
    private static partial Regex SlashSepRe();

    // 원곡 아티스트를 명시한 형태 — 커버 영상에서 가장 신뢰할 수 있는 단서.
    [GeneratedRegex(@"(?:原曲|原唱|원곡|original(?:\s+(?:song|artist))?)\s*[:：]\s*(?<who>[^)\]\}/|,]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex OriginalArtistRe();

    [GeneratedRegex(@"\s*[\(\[\{（［]\s*(?:official\s*(?:music\s*)?(?:video|audio|lyric[s]?|m/?v)|music\s*video|lyric[s]?|audio|m/?v|mv|visualizer|color(?:ed)?\s*coded|han[/\s]?rom[/\s]?eng|가사|뮤직\s*비디오|feat\.?[^)\]\}]*|ft\.?[^)\]\}]*|prod\.?[^)\]\}）］]*)\s*[\)\]\}）］]", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRe();
    [GeneratedRegex(@"\s*(?:feat\.?|ft\.?)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatRe();
    [GeneratedRegex(@"\s*-\s*topic$|\s*VEVO$", RegexOptions.IgnoreCase)]
    private static partial Regex UploaderSuffixRe();
    [GeneratedRegex(@"\s*(?:official\s*)?(?:music\s*)?(?:lyric[s]?\s*)?(?:video|audio|m/?v|mv|visualizer)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BareTailRe();
    [GeneratedRegex(@"['""‘’“”「『《]\s*([^'""‘’“”」』》]+?)\s*['""‘’“”」』》]")]
    private static partial Regex QuotedRe();
    [GeneratedRegex(@"\s*[\(\[\{（［]?\s*(?:remix(?:ed)?|bootleg|acoustic|live|inst(?:rumental)?\.?|karaoke|official|lyric[s]?|full\s*ver(?:sion)?\.?|ver\.?|version|hd|hq|4k|8k|mv|m/?v|audio|visualizer|가사|라이브|어쿠스틱|리믹스|풀\s*버전|버전)\s*[\)\]\}）］]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TailRe();
    [GeneratedRegex(@"\s*[-–—|/~:]+\s*|\s+_+\s+")]
    private static partial Regex SepRe();
    [GeneratedRegex(@"[\(\[［（【]([^)\]］）】]*)[\)\]］）】]")]
    private static partial Regex ParenRe();

    // ---------- 문자열 유사도 (Ratcliff-Obershelp) ----------

    private static string Norm(string s) => WsRe().Replace((s ?? "").ToLowerInvariant(), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WsRe();

    public static double Similar(string a, string b)
    {
        a = Norm(a); b = Norm(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        if (a.Contains(b) || b.Contains(a))
            return 0.6 + 0.4 * ((double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length));
        return 2.0 * MatchingChars(a, b) / (a.Length + b.Length);
    }

    private static int MatchingChars(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var (ai, bi, len) = LongestCommon(a, b);
        if (len == 0) return 0;
        return len + MatchingChars(a[..ai], b[..bi]) + MatchingChars(a[(ai + len)..], b[(bi + len)..]);
    }

    private static (int Ai, int Bi, int Len) LongestCommon(string a, string b)
    {
        int bestA = 0, bestB = 0, bestLen = 0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : 0;
                if (cur[j] > bestLen) { bestLen = cur[j]; bestA = i - bestLen; bestB = j - bestLen; }
            }
            (prev, cur) = (cur, prev);
            Array.Clear(cur);
        }
        return (bestA, bestB, bestLen);
    }
}
