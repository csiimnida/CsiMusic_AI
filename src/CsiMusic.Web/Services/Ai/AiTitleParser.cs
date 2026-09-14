using System.Text.Json;
using System.Text.Json.Serialization;
using CsiMusic.Web.Options;
using Microsoft.Extensions.Options;

namespace CsiMusic.Web.Services.Ai;

/// <summary>
/// (A) 유튜브 제목 → 곡명·아티스트 추출의 AI 보조.
///
/// 왜 필요한가: <see cref="TitleParser"/> 는 정규식 15개로 "【歌ってみた】強風オールバック / covered by
/// しぐれうい" 같은 제목을 처리하는데, 이 문법은 채널마다 제각각이라 규칙으로는 한계가 뚜렷하다.
/// 특히 <b>커버 영상에서 원곡 아티스트를 알아내는 일</b>은 제목 문자열에 답이 없는 경우가 많다 —
/// 모델의 사전 지식이 실제로 값을 하는 지점이다(가사는 원곡의 것이라 원곡 아티스트로 찾아야 한다).
///
/// 통합 방식은 <b>대체가 아니라 보강</b>이다: AI 결과를 후보 목록 <i>앞</i>에 끼워 넣고 규칙 기반
/// 후보는 그대로 뒤에 남긴다. AI 가 틀려도 기존 경로가 살아 있고, 맞으면 첫 시도에 걸린다.
/// 아래 사다리(LRCLIB 길이·유사도 가드)가 어차피 후보를 한 번 더 검증하므로 이 보강은 안전하다.
/// </summary>
public sealed class AiTitleParser(
    AiClient ai, AiCache cache, IOptions<AppOptions> options, ILogger<AiTitleParser> log)
{
    private readonly AppOptions _opt = options.Value;

    /// <summary>프롬프트/스키마 버전. 프롬프트를 고치면 올려서 옛 캐시를 무효화한다.</summary>
    private const int PromptVersion = 2;

    /// <param name="Track">원곡 제목(모르면 null).</param>
    /// <param name="Artist">원곡 아티스트 — 커버여도 <b>원곡</b> 쪽(모르면 null).</param>
    /// <param name="IsCover">커버/불러봤다 영상인지.</param>
    /// <param name="Confidence">0~1. 낮으면 후보 맨 앞에 두지 않는다.</param>
    /// <param name="MetaLooksWrong">
    /// 유튜브가 준 음악 메타(music_track/music_artist)가 이 영상의 곡이 아닌 것으로 보이는지.
    /// 기본은 false — 확신할 때만 true 다. true 면 후보 순서에서 메타를 AI 답 뒤로 내린다.
    /// </param>
    public sealed record AiTitle(
        [property: JsonPropertyName("track")] string? Track,
        [property: JsonPropertyName("artist")] string? Artist,
        [property: JsonPropertyName("isCover")] bool IsCover,
        [property: JsonPropertyName("confidence")] double Confidence,
        [property: JsonPropertyName("metaLooksWrong")] bool MetaLooksWrong = false);

    // 이 값 미만이면 결과를 버린다. 모델이 "모르겠다"를 낮은 confidence 로 표현하도록 프롬프트에
    // 명시해 뒀고, 애매한 추측을 후보 1순위로 올리면 규칙 기반보다 오히려 나빠진다.
    private const double MinConfidence = 0.5;

    private const string Instruction = """
        You extract the ORIGINAL SONG title and the ORIGINAL RECORDING ARTIST from a YouTube video
        title, so that the song's lyrics can be looked up in a lyrics database.

        Rules:
        - Return the ORIGINAL artist, never the cover singer, the uploader, or the channel.
          For a cover ("covered by", "歌ってみた", "커버", "cover"), name the artist of the ORIGINAL
          release. If you do not actually know who released the original, set artist to null.
        - Strip promo/format noise: Official MV, M/V, Lyric Video, Audio, 4K, HD, ENG SUB, 가사,
          【】 tags, view counts, release dates, and similar.
        - Keep the song title in its ORIGINAL language and script. Do not translate or romanize it.
          If the title appears in two scripts, prefer the one the lyrics would be written in.
        - Drop feat./with credits from the track title; they belong to neither field.
        - This is a lookup key, not a guess: if you are not reasonably sure of a field, use null
          rather than inventing something. A null field is handled fine downstream.
        - confidence is your honest probability (0..1) that BOTH non-null fields are correct.
          Use below 0.5 when you are unsure — that makes the result be discarded.

        The input may also carry youtube_music_track / youtube_music_artist: structured metadata
        YouTube attached to the video via Content ID. It is usually right and is normally trusted
        ahead of your answer, so judge it separately:
        - Set metaLooksWrong=true ONLY when that metadata clearly describes a DIFFERENT song than
          the video is (a mis-fired Content ID match, a totally unrelated track or artist).
        - Leave it false when the metadata is merely formatted differently, romanized, translated,
          less complete, names a different release of the same song, or when no metadata was given.
          Being unsure is not evidence it is wrong.

        Respond with JSON only.
        """;

    // type 값은 Gemini responseSchema 의 OpenAPI Type enum 이라 대문자여야 한다(소문자는 400).
    private static readonly object Schema = new
    {
        type = "OBJECT",
        properties = new
        {
            track = new { type = "STRING", nullable = true },
            artist = new { type = "STRING", nullable = true },
            isCover = new { type = "BOOLEAN" },
            confidence = new { type = "NUMBER" },
            metaLooksWrong = new { type = "BOOLEAN" },
        },
        required = new[] { "track", "artist", "isCover", "confidence", "metaLooksWrong" },
    };

    /// <summary>
    /// 제목에서 (곡, 아티스트)를 추출한다. AI 가 꺼져 있거나·실패·저신뢰면 null → 호출측은 규칙 기반만 쓴다.
    /// </summary>
    public async Task<AiTitle?> ParseAsync(
        string title, string? uploader, int duration, MusicMeta? music = null,
        CancellationToken ct = default)
    {
        // Configured 를 먼저 본다 — 키를 안 넣은 설치에서는 캐시 조회(SQLite 왕복)조차 하지 않는다.
        // 라파는 DB 가 USB 에 있어 곡마다 쓸데없는 쿼리가 실제로 비용이다.
        if (!_opt.AiTitleEnabled || !ai.Configured || string.IsNullOrWhiteSpace(title)) return null;

        // 메타도 입력이므로 키에 들어가야 한다 — 안 넣으면 메타가 생기기 전의 답이 계속 재사용된다.
        var key = AiCache.Key("title", PromptVersion, title, uploader, music?.Track, music?.Artist);
        if (await cache.GetAsync<AiTitle>(key) is { } hit)
            return hit.Confidence >= MinConfidence ? hit : null;

        if (!ai.Available) return null;

        var input = $"""
            title: {title}
            channel: {uploader ?? "(unknown)"}
            duration_seconds: {(duration > 0 ? duration.ToString() : "(unknown)")}
            youtube_music_track: {NullIfBlank(music?.Track) ?? "(none)"}
            youtube_music_artist: {NullIfBlank(music?.Artist) ?? "(none)"}
            """;

        var json = await ai.AskJsonAsync(Instruction, input, Schema, ct);
        if (json is not { } root) return null;

        AiTitle result;
        try
        {
            result = new AiTitle(
                Str(root, "track"), Str(root, "artist"),
                root.TryGetProperty("isCover", out var c) && c.ValueKind == JsonValueKind.True,
                root.TryGetProperty("confidence", out var f) && f.ValueKind == JsonValueKind.Number
                    ? f.GetDouble() : 0,
                // 메타가 없었으면 판정 자체가 무의미하다 — 모델이 뭐라 하든 무시한다.
                music is not null && !music.IsEmpty
                    && root.TryGetProperty("metaLooksWrong", out var w) && w.ValueKind == JsonValueKind.True);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            log.LogDebug("AI 제목 파싱 응답 형식 오류: {Msg}", e.Message);
            return null;
        }

        // 저신뢰 결과도 캐시한다 — 다시 물어봐도 같은 답이라 API 만 낭비된다.
        await cache.SetAsync(key, result);

        if (result.Confidence < MinConfidence)
        {
            log.LogDebug("AI 제목 파싱 신뢰도 미달 {Conf:F2}: {Title}", result.Confidence, title);
            return null;
        }
        if (result.Track is null && result.Artist is null) return null;

        log.LogInformation("AI 제목 파싱: {Title} → {Track} / {Artist} (cover={Cover}, conf={Conf:F2}{Meta})",
            title, result.Track ?? "-", result.Artist ?? "-", result.IsCover, result.Confidence,
            result.MetaLooksWrong ? ", 음악메타 불일치" : "");
        return result;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>JSON 문자열 필드 — null/빈 문자열/공백은 전부 null 로 접는다.</summary>
    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && v.GetString() is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
