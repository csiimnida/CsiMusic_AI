using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CsiMusic.Web.Options;
using Microsoft.Extensions.Options;

namespace CsiMusic.Web.Services.Ai;

/// <summary>
/// (B) 찾아온 가사가 <b>정말 이 곡의 가사인지</b> 판정하는 AI 검증기.
///
/// 왜 필요한가: 기존 게이트(<c>LrclibMatchOk</c>)는 제목 유사도 0.85 / 아티스트 0.5 / 길이차 15초 같은
/// 문자열·숫자 임계값이다. 이건 "제목이 비슷한 <i>다른</i> 곡"을 못 거른다 — 리메이크·동명이곡·같은
/// 앨범의 인접 트랙·번안곡이 전형적인 오탐이고, 사용자 눈엔 '엉뚱한 가사가 뜬다'로 보인다.
/// 가사 <i>본문</i>을 실제로 읽고 판단할 수 있는 건 모델뿐이다.
///
/// <b>판정은 비대칭이다.</b> 확신 있는 부정일 때만 거부하고, 애매하면 통과시킨다. 이유:
/// 규칙 기반 게이트를 이미 통과한 후보라 사전 확률이 이미 높고, 잘못 거부하면 멀쩡한 가사가 사라진다
/// (오탐 하나를 막으려다 여러 곡의 가사를 잃는 건 손해다). 그래서 이 클래스는 '2차 소견'이지
/// 1차 판정자가 아니다.
///
/// 거부되면 호출측은 사다리의 <i>다음</i> 소스로 내려간다 — 검증이 실패를 만드는 게 아니라
/// 더 나은 후보를 찾을 기회를 만든다.
/// </summary>
public sealed class AiLyricsVerifier(
    AiClient ai, AiCache cache, IOptions<AppOptions> options, ILogger<AiLyricsVerifier> log)
{
    private readonly AppOptions _opt = options.Value;

    /// <summary>프롬프트/스키마 버전. 프롬프트를 고치면 올려서 옛 캐시를 무효화한다.</summary>
    private const int PromptVersion = 1;

    // 모델에 보내는 가사 발췌 상한. 판정에는 앞부분 몇 줄이면 충분하고(곡 식별은 후렴 전에 끝난다),
    // 전문을 보내면 토큰이 곡당 수천으로 뛰어 무료 티어를 금방 태운다.
    private const int MaxLines = 24;
    private const int MaxChars = 1200;

    // 이 값 이상으로 "아니다"라고 해야 실제로 거부한다. 위 비대칭 원칙의 수치화.
    private const double RejectConfidence = 0.75;

    // 줄태그 [mm:ss.xx] 와 단어태그 <mm:ss.xx> — 발췌에서 떼어낸다. 떼야 같은 곡의 synced/plain 이
    // 같은 캐시 키로 모여 호출이 절반이 되고, 모델에게도 타임코드는 잡음일 뿐이다.
    private static readonly Regex TimeTagRe =
        new(@"[\[<]\d{1,2}:\d{2}(?:[.:]\d{1,3})?[\]>]", RegexOptions.Compiled);

    /// <param name="Match">이 가사가 그 곡의 것인지.</param>
    /// <param name="Confidence">0~1. 판단의 확신도.</param>
    /// <param name="Reason">짧은 근거(로그·관리자 화면 표시용).</param>
    public sealed record AiVerdict(
        [property: JsonPropertyName("match")] bool Match,
        [property: JsonPropertyName("confidence")] double Confidence,
        [property: JsonPropertyName("reason")] string? Reason);

    private const string Instruction = """
        You judge whether a block of lyrics belongs to a specific song, to catch wrong matches from
        a fuzzy lyrics-database lookup. The lyrics passed the string-similarity check already, so
        most of them are correct; your job is to catch the ones that are confidently wrong.

        Typical real failures you should catch:
        - A DIFFERENT song that happens to share a title (covers of unrelated songs, common titles).
        - A different song by the same artist, or an adjacent track from the same album.
        - A translated or rewritten version whose lyrics are not the original text.
        - Lyrics in a language the song plainly cannot be in.
        - Content that is not song lyrics at all (a description, credits, an instrumental note).

        Do NOT reject for any of these — they are normal and expected:
        - The excerpt is truncated. You only receive the opening lines.
        - Minor differences in punctuation, line breaks, romanization, or spelling.
        - A live, acoustic, remix, or re-recorded version of the same song.
        - Metadata that is merely incomplete (a missing or generic artist name).
        - You simply do not know the song. Not knowing is not evidence of a mismatch.

        Set match=false ONLY when you have positive evidence the lyrics belong to a different work.
        When you are unsure, set match=true with a low confidence — an uncertain answer must not
        remove lyrics from the user.

        confidence is your honest probability (0..1) that your verdict is right.
        reason is one short clause, in Korean, naming the actual evidence.

        Respond with JSON only.
        """;

    // type 값은 Gemini responseSchema 의 OpenAPI Type enum 이라 대문자여야 한다(소문자는 400).
    private static readonly object Schema = new
    {
        type = "OBJECT",
        properties = new
        {
            match = new { type = "BOOLEAN" },
            confidence = new { type = "NUMBER" },
            reason = new { type = "STRING" },
        },
        required = new[] { "match", "confidence", "reason" },
    };

    /// <summary>검증 대상 후보의 신원 — 소스가 스스로 주장하는 곡/아티스트/길이.</summary>
    /// <param name="Source">lrclib · unison · namu 등.</param>
    public sealed record Candidate(
        string Source, string? Track, string? Artist, double? Duration, string Lyrics);

    /// <summary>
    /// 후보 가사를 검증한다. <b>true = 채택</b>(통과 또는 판단 불가), <b>false = 거부</b>.
    ///
    /// AI 가 꺼져 있거나·실패하거나·확신이 부족하면 항상 true 다 — AI 없이도 동작이 지금과 같아야 한다.
    ///
    /// 반환의 CalledApi 는 호출측이 곡당 API 예산을 셀 때 쓴다 — 캐시 히트는 지연도 비용도 0 이라
    /// 예산에서 빼면 안 된다. 반대로 <paramref name="apiBudgetExhausted"/> 가 true 면 캐시에 답이
    /// 없을 때 API 를 부르지 않고 그냥 통과시킨다(판정 없이 채택 = AI 를 끈 것과 같은 안전한 쪽).
    /// 이 판단은 <b>캐시 조회 뒤</b>에 해야 한다 — 그래야 캐시된 곡은 예산과 무관하게 계속 검증된다.
    /// </summary>
    public async Task<(bool Accept, AiVerdict? Verdict, bool CalledApi)> AcceptAsync(
        string title, string? uploader, int duration, Candidate candidate,
        bool apiBudgetExhausted = false, CancellationToken ct = default)
    {
        // Configured 를 먼저 본다 — 키를 안 넣은 설치에서는 캐시 조회(SQLite 왕복)조차 하지 않는다.
        if (!_opt.AiVerifyEnabled || !ai.Configured) return (true, null, false);

        var excerpt = Excerpt(candidate.Lyrics);
        if (excerpt.Length == 0) return (true, null, false);

        var key = AiCache.Key("verify", PromptVersion,
            title, uploader, duration.ToString(), candidate.Source,
            candidate.Track, candidate.Artist, excerpt);

        var verdict = await cache.GetAsync<AiVerdict>(key);
        var calledApi = false;
        if (verdict is null)
        {
            if (apiBudgetExhausted || !ai.Available) return (true, null, false);
            calledApi = true;

            var input = $"""
                The song we are looking up:
                  youtube_title: {title}
                  youtube_channel: {uploader ?? "(unknown)"}
                  duration_seconds: {(duration > 0 ? duration.ToString() : "(unknown)")}

                The lyrics we found:
                  from_source: {candidate.Source}
                  claimed_track: {candidate.Track ?? "(none)"}
                  claimed_artist: {candidate.Artist ?? "(none)"}
                  claimed_duration_seconds: {(candidate.Duration is > 0 ? candidate.Duration.Value.ToString("F0") : "(unknown)")}

                Opening lines of those lyrics:
                {excerpt}
                """;

            var json = await ai.AskJsonAsync(Instruction, input, Schema, ct);
            if (json is not { } root) return (true, null, calledApi);

            try
            {
                // match 가 아예 없으면 '판정 없음'이지 '아니다'가 아니다. 없는 걸 false 로 접으면
                // 망가진 응답이 confidence 만 높게 실려 올 때 멀쩡한 가사를 지워 버린다
                // (responseSchema 가 required 로 걸어 두지만, 응답을 믿고 거부까지 갈 이유는 없다).
                if (!root.TryGetProperty("match", out var m)
                    || m.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    log.LogDebug("AI 가사 검증 응답에 match 가 없다 — 판정 없음으로 통과");
                    return (true, null, calledApi);
                }
                verdict = new AiVerdict(
                    m.ValueKind == JsonValueKind.True,
                    root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                        ? c.GetDouble() : 0,
                    root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                        ? r.GetString() : null);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                log.LogDebug("AI 가사 검증 응답 형식 오류: {Msg}", e.Message);
                return (true, null, calledApi);
            }

            await cache.SetAsync(key, verdict);
        }
        // verdict 는 AiVerdict? 로 시작해 위 블록에서만 채워진다 — 널 흐름 분석에 기대지 않고 명시적으로 막는다.
        if (verdict is null) return (true, null, calledApi);

        // 확신 있는 부정일 때만 거부. 그 외(일치·저확신 부정)는 전부 통과.
        if (!verdict.Match && verdict.Confidence >= RejectConfidence)
        {
            log.LogInformation("AI 가사 거부 [{Source}] {Title} — {Reason} (conf={Conf:F2})",
                candidate.Source, title, verdict.Reason ?? "-", verdict.Confidence);
            return (false, verdict, calledApi);
        }

        return (true, verdict, calledApi);
    }

    /// <summary>가사 앞부분 발췌 — LRC 타임태그·빈 줄을 걷어내고 줄 수/글자 수로 자른다.</summary>
    private static string Excerpt(string? lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics)) return "";
        var sb = new StringBuilder();
        var lines = 0;
        foreach (var raw in lyrics.Split('\n'))
        {
            var line = TimeTagRe.Replace(raw, "").Trim();
            if (line.Length == 0) continue;
            if (sb.Length + line.Length + 1 > MaxChars) break;
            sb.Append(line).Append('\n');
            if (++lines >= MaxLines) break;
        }
        return sb.ToString().TrimEnd();
    }
}
