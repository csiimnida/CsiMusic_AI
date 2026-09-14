using CsiMusic.Web.Services.Ai;

namespace CsiMusic.Web.Services;

/// <summary>
/// 화면·디스코드 카드에 적을 "곡 제목 / 가수"를 정한다.
///
/// 규칙 기반 <see cref="TitleParser.ForDisplay(TitleParser.Result, string?, string?)"/> 만으로는
/// <c>"Ditto - NewJeans"</c> 처럼 <b>구분자 양쪽 중 어느 쪽이 곡명인지 문자열로는 알 수 없는</b> 제목을
/// 못 가른다. 관례(Artist - Title)로 찍기 때문에 반대 순서로 올라온 영상은 두 값이 뒤바뀐다.
/// 그건 지식이 있어야 아는 문제라, 그 경우에만 <see cref="AiTitleParser"/> 에 물어본다.
///
/// AI 호출을 아끼는 두 가지 장치:
/// - 유튜브 뮤직 메타(music_track/music_artist)가 하나라도 있으면 규칙만으로 충분하므로 묻지 않는다.
/// - 물어보더라도 AiTitleParser 가 90일 캐시를 먼저 본다. 캐시 키가 가사 경로와 같아서, 이미 가사를
///   찾아 둔 곡이면 API 를 한 번도 부르지 않고 그 답을 그대로 재사용한다.
///
/// AI 가 꺼져 있거나·실패·저신뢰면 <see cref="AiTitleParser.ParseAsync"/> 가 null 을 돌려주고,
/// 그러면 규칙 기반 값이 그대로 나간다 — 기능이 없던 때와 같은 동작이다.
/// </summary>
public sealed class DisplayNames(AiTitleParser aiTitles)
{
    public async Task<(string? Song, string? Singer)> ResolveAsync(
        string title, string? uploader, int duration, MusicMeta? music, CancellationToken ct = default)
    {
        var parsed = TitleParser.Parse(title, uploader);
        var (song, singer) = TitleParser.ForDisplay(parsed, title, music?.Track, music?.Artist);

        // 메타가 있으면 어느 쪽이 곡명인지 이미 갈렸다. 굳이 AI 를 부르지 않는다.
        if (music is not null && !music.IsEmpty) return (song, singer);

        if (await aiTitles.ParseAsync(title, uploader, duration, music, ct) is not { } ai)
            return (song, singer);

        // 커버 영상에서 AI 가 주는 아티스트는 <b>원곡</b> 쪽이다(프롬프트가 그렇게 지시한다 — 가사는
        // 원곡으로 찾아야 하므로). 화면에는 지금 부르는 사람을 적어야 하니 가수는 규칙 기반 값을
        // 그대로 두고, 곡 제목만 AI 것을 쓴다. AI 는 【歌ってみた】 같은 장식을 떼는 데도 낫다.
        var isCover = parsed.IsCover || ai.IsCover;

        // AI 답도 검색 키라 괄호가 떨어져 있다 — 표시용으로는 원본 제목의 별칭 괄호를 되살린다.
        return (
            TitleParser.WithTitleAlias(Blank(ai.Track), title) ?? song,
            isCover ? singer : Blank(ai.Artist) ?? singer);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
