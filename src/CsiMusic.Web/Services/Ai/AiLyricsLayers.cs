using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsiMusic.Web.Common;
using CsiMusic.Web.Options;
using Microsoft.Extensions.Options;

namespace CsiMusic.Web.Services.Ai;

/// <summary>
/// (C) 일본어 가사 줄마다 <b>한글 음차와 한국어 번역</b>을 만드는 AI 작업(docs/LYRICS_LAYERS_PLAN.md §2.1).
///
/// 앞의 두 작업(A 제목 파싱 · B 가사 검증)과 성격이 다르다. 저 둘은 짧은 <i>판정</i>이고 실패하면
/// 규칙 기반으로 폴백하면 그만이지만, 이건 실제로 <i>글을 짓는</i> 작업이라 폴백이 없다 —
/// 만들거나 안 만들거나 둘 중 하나다. 그래서 "대충 맞는 값"을 받아 저장하느니 버리는 쪽을 택한다
/// (<c>Validate</c>).
///
/// 정확도를 위해 <b>발음과 번역을 따로 부른다</b>(§2.5b-1). 한 번에 시키면 모델이 양쪽 다 대충 한다.
/// 호출이 2배가 되지만 사람이 버튼을 눌렀을 때만 도는 기능이라(§2.6) 감당 가능하다.
///
/// 이 클래스는 <b>한 번의 호출(한 덩어리)</b>만 책임진다. 긴 가사를 청크로 쪼개고 캐시하는 일은
/// 호출측이 한다(§2.2·§2.3).
/// </summary>
public sealed class AiLyricsLayers(
    AiClient ai, IOptions<AppOptions> options, ILogger<AiLyricsLayers> log)
{
    private readonly AppOptions _opt = options.Value;

    /// <summary>프롬프트/스키마 버전. 프롬프트를 고치면 올려서 옛 캐시를 무효화한다.</summary>
    public const int PromptVersion = 1;

    /// <summary>
    /// 이 작업만의 출력 상한. 기본값 2048 은 짧은 판정용이라 여기엔 빠듯하다 —
    /// 15줄짜리 한국어 문장 배열에 사고 토큰까지 얹히면 본문이 잘린 채 온다(§2.2).
    /// </summary>
    private const int MaxOutputTokens = 4096;

    /// <summary>
    /// <b>장음 표기 규칙 — 바꾸려면 이 한 줄만 고치고 <c>PromptVersion</c> 을 올린다.</b>
    ///
    /// 늘려 쓰는 쪽을 택했다. 이 줄의 쓰임이 '따라 부르기'이기 때문이다 — 한 글자가 한 박이라
    /// 「とうきょう」를 '토쿄'로 줄이면 두 박이 사라진다. 뜻은 아래 번역 줄이 이미 책임진다.
    /// 한국어 외래어 표기 관례(도쿄·유키)를 따르고 싶으면 이 값을 이렇게 바꾼다:
    ///   "Do not stretch long vowels. Write them as one syllable: とうきょう is 토쿄, ゆうき is 유키."
    /// </summary>
    private const string LongVowelRule =
        "Stretch long vowels - write the sound you hold, one hangul letter per beat: " +
        "とうきょう is 토오쿄오, もう is 모오, ゆうき is 유우키, ケーキ is 케에키. " +
        "The ー mark repeats the vowel before it. When the lyrics themselves stretch a sound " +
        "(あーーー, なあああ), keep every beat.";

    // 한 프롬프트에 발음과 번역을 섞지 않는다(§2.5b-1). 아래 둘은 의도적으로 각각 완결된 지시다.

    private static readonly string PronounceInstruction = $"""
        You transcribe Japanese song lyrics into Korean hangul so that a Korean listener can sing along
        while the song plays. You are writing what is SUNG, not what is spelled.

        Rules, in order of importance:
        - Use hangul and spaces only. Never output romaji, kana, kanji, or Latin letters.
        - Particles are written by sound, not by spelling: は is 와, へ is 에, を is 오.
        - Small っ becomes a final consonant: きって is 킷테, ずっと is 즛토.
        - ん becomes a final consonant: せんせい is 센세이, さんぽ is 삼포.
        - Voiceless consonants stay voiceless even at the start of a word - か is 카 and た is 타,
          not 가 or 다. This is a singing guide, not the Korean loanword spelling standard.
        - {LongVowelRule}
        - A line that is not Japanese (English, for example) is still transcribed by sound into hangul.
        - When a kanji is sung with a reading that differs from its usual one, follow the singing.
          When you do not know the reading, read it as written.
        - A line with no words in it (empty, or only marks like ♪ ～ ー -) gets an empty string.
          Never invent words for an instrumental break.

        Output exactly one entry per input line, in the same order, with nothing else in it.
        Never merge, split, drop, or reorder lines. Repeated lines get repeated output.

        Examples:
          夜に駆ける            -> 요루니 카케루
          私は君を見た          -> 와타시와 키미오 미타
          きっと言えなかった     -> 킷토 이에나캇타
          もう一度だけ          -> 모오 이치도다케
          ずっと東京にいる       -> 즛토 토오쿄오니 이루
          Forever in my heart  -> 포에버 인 마이 하트
          ♪                   -> (empty string)

        Respond with JSON only.
        """;

    private static readonly string TranslateInstruction = """
        You translate Japanese song lyrics into natural Korean, line by line, to be shown under the
        original line while the song plays.

        Rules, in order of importance:
        - Write Korean a person would actually say. A word-by-word gloss is a failure, not a safe answer.
        - Keep each line's meaning inside that line. A sentence often runs across two lines; translate
          the part on this line and leave the rest to the next one. Never move words between lines.
        - Never leave kana or kanji in the output. If a line is a name or a shout, write it in hangul.
        - A line with no words in it (empty, or only marks like ♪ ～ ー -) gets an empty string.
        - Lines already in Korean are left as they are. Lines in English are translated too.
        - No notes, no explanations, no romaji, no quotation marks you were not given.

        Output exactly one entry per input line, in the same order, with nothing else in it.
        Never merge, split, drop, or reorder lines. Repeated lines get repeated output.

        Respond with JSON only.
        """;

    // type 값은 Gemini responseSchema 의 OpenAPI Type enum 이라 대문자여야 한다(소문자는 400).
    private static readonly object Schema = new
    {
        type = "OBJECT",
        properties = new
        {
            lines = new { type = "ARRAY", items = new { type = "STRING" } },
        },
        required = new[] { "lines" },
    };

    /// <summary>저장 형식 그대로 — <c>lyrics.layers</c> 배열의 원소이자 app.js 가 읽는 모양.</summary>
    public sealed record LayerLine(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("pronunciation")] string? Pronunciation,
        [property: JsonPropertyName("translation")] string? Translation);

    /// <summary>어떤 층을 만들 것인가 — 프롬프트도 검증 규칙도 이 값에 따라 갈린다.</summary>
    public enum Layer
    {
        /// <summary>한글 음차.</summary>
        Pronunciation,

        /// <summary>한국어 번역.</summary>
        Translation,
    }

    /// <summary>키가 있고 토글이 켜져 있는지. 호출측이 버튼을 그릴지 정할 때 쓴다.</summary>
    public bool Enabled => _opt.AiLyricsEnabled && ai.Configured;

    /// <summary>이 작업에 쓸 모델 — 따로 지정하지 않았으면 기본 모델(§2.5b-2).</summary>
    private string? Model => string.IsNullOrWhiteSpace(_opt.AiLyricsModel) ? null : _opt.AiLyricsModel;

    /// <summary>
    /// 한 덩어리(줄 배열)에 대해 한 층을 만든다. <b>실패하면 null</b> — 부분 성공은 없다.
    ///
    /// 반환이 null 인 경우: 기능이 꺼짐 · AI 사용 불가(회로·상한) · 응답 파싱 실패 ·
    /// 검증 불합격(§2.5b-4). 호출측은 null 을 받으면 그 덩어리를 저장하지도 캐시하지도 말아야 한다 —
    /// 틀린 발음을 캐시하면 다시 눌러도 같은 틀린 값이 나온다.
    /// </summary>
    public async Task<IReadOnlyList<string>?> BuildAsync(
        IReadOnlyList<string> lines, Layer layer, CancellationToken ct = default)
    {
        if (!Enabled || lines.Count == 0) return null;
        if (!ai.Available) return null;

        // 번호를 붙여 보낸다 — 모델이 줄 수를 세기 쉬워지고, 빈 줄이 통째로 사라지는 사고가 준다.
        var numbered = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
            numbered.Append(i + 1).Append(": ").Append(lines[i]).Append('\n');

        var input = $"""
            {lines.Count} lines follow, numbered. Return {lines.Count} entries in "lines", in this order.
            The numbers are not part of the lyrics - do not repeat them in your output.

            {numbered.ToString().TrimEnd()}
            """;

        var instruction = layer == Layer.Pronunciation ? PronounceInstruction : TranslateInstruction;
        var json = await ai.AskJsonAsync(instruction, input, Schema, ct, Model, MaxOutputTokens,
            longRunning: true);
        if (json is not { } root) return null;

        var got = ReadLines(root);
        if (got is null)
        {
            log.LogDebug("AI 가사 {Layer} 응답에 lines 배열이 없다", layer);
            return null;
        }

        if (!Validate(lines, got, layer, out var why))
        {
            // 어느 줄이 왜 걸렸는지 남긴다 — 프롬프트를 고칠 근거가 이 로그뿐이다.
            log.LogWarning("AI 가사 {Layer} 결과를 버린다 — {Why}", layer, why);
            return null;
        }
        return got;
    }

    /// <summary>응답의 lines 배열을 문자열 목록으로. 형태가 다르면 null.</summary>
    private static List<string>? ReadLines(JsonElement root)
    {
        if (!root.TryGetProperty("lines", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>(arr.GetArrayLength());
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) return null;
            list.Add((e.GetString() ?? "").Trim());
        }
        return list;
    }

    /// <summary>
    /// 받은 값을 기계적으로 검사한다(§2.5b-4). 모델을 믿지 않는다 — 하나라도 걸리면 덩어리째 버린다.
    ///
    /// 부분 수용을 하지 않는 이유: 줄이 하나 밀리면 그 뒤 전부가 <i>다른 줄의</i> 발음이 된다.
    /// 눈에 잘 띄지도 않으면서 가장 나쁜 실패라, 의심스러우면 전부 버리는 게 싸다.
    /// </summary>
    /// <summary>
    /// 나무위키 본문에서 <b>일본어 원문 가사</b>만 뽑아 온다(계획 §9.6).
    ///
    /// 발음·번역을 붙이는 <see cref="ExtractAsync"/> 와 달리 <b>대조할 원문이 없다</b> —
    /// 이건 다른 어떤 DB 에도 가사가 없는 곡에 쓰는 마지막 수단이기 때문이다. 그래서 여기서
    /// 돌려준 값은 호출측이 <c>AiLyricsVerifier</c>(작업 B)로 반드시 검증해야 한다.
    ///
    /// 실패하면 null. 줄 수가 너무 적거나 한국어 줄(발음·번역)을 집어 왔으면 버린다.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ExtractLyricsAsync(
        string reference, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(reference)) return null;

        var json = await ai.AskJsonAsync(
            LyricsInstruction, reference, Schema, ct, Model, LyricsMaxOutputTokens);
        if (json is not { } root || ReadLines(root) is not { } got) return null;

        var lines = got.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        // 너무 짧으면 가사가 아니라 인포박스 조각을 집어 온 것이다.
        if (lines.Count < MinLyricLines)
        {
            log.LogDebug("나무위키 가사 추출 버림 — 줄이 {N}개뿐이다", lines.Count);
            return null;
        }

        // 한국어 줄(발음·번역)을 집어 왔는지 본다. 원문 자리에 한글만 있는 줄이 많으면 실패다.
        var hangulOnly = lines.Count(l =>
        {
            var m = JapaneseText.Measure(l);
            return m.Hangul > 0 && m.Kana == 0 && m.Han == 0;
        });
        if (hangulOnly * 2 > lines.Count)
        {
            log.LogInformation("나무위키 가사 추출 버림 — 한글 줄이 {N}/{Total} 로 원문이 아니다",
                hangulOnly, lines.Count);
            return null;
        }
        return lines;
    }

    /// <summary>가사 전문은 층 한 덩어리보다 훨씬 길다 — 잘리면 뒷부분이 통째로 사라진다.</summary>
    private const int LyricsMaxOutputTokens = 8192;

    /// <summary>이보다 짧으면 가사로 보지 않는다.</summary>
    private const int MinLyricLines = 6;

    private const string LyricsInstruction = """
        You are given text copied from a Korean wiki page about a Japanese song. Extract the song's
        ORIGINAL JAPANESE lyrics, line by line, in order.

        - Output only the Japanese lyric lines, exactly as they appear on the page.
        - The page usually also shows a Korean pronunciation line and a Korean translation line for
          each Japanese line - either as two rows underneath it, or as two more columns beside it.
          Do NOT output those. You want only the Japanese.
        - Ignore everything that is not lyrics: section headings, singer part labels and colour
          legends (for example 나나오아카리 / Sou / 함께), footnote markers, edit links, infobox
          fields, release dates, and any commentary about the song.
        - If the page carries more than one version of the song (a TV size next to the full
          version), output the longest complete one, and only that one.
        - Keep the page's own line breaks and order. Never merge, split, reorder, translate, or
          transliterate a line.
        - Blank lines between verses may be dropped.
        - If the page has no Japanese lyrics at all, output an empty list. An empty answer is far
          better than a guess - the caller has no other way to tell that you were wrong.
        """;

    /// <summary>
    /// 나무위키 본문에서 층을 <b>베껴 온다</b>(계획 §9.4). 만드는 게 아니라 찾는 일이다.
    ///
    /// 생성(<see cref="BuildAsync"/>)보다 안전한 작업이다 — 정답이 입력 안에 있으니 지어낼
    /// 이유가 없고, 결과가 원문과 맞는지 호출측이 기계로 대조할 수 있다. 대신 <b>못 찾은 줄은
    /// 비워 두는 것이 정답이다</b>(§9.5 의 층 단위 폴백이 그 빈 자리를 AI 생성으로 메운다).
    /// </summary>
    /// <param name="reference">나무위키 가사 구간 텍스트(<c>NamuWikiClient.LyricsExcerpt</c>).</param>
    public async Task<IReadOnlyList<string>?> ExtractAsync(
        IReadOnlyList<string> lines, string reference, Layer layer, CancellationToken ct = default)
    {
        if (!Enabled || lines.Count == 0 || string.IsNullOrWhiteSpace(reference)) return null;

        var numbered = string.Join("\n", lines.Select((l, i) => $"{i + 1}: {l}"));
        var input = $"""
            The song's lines, numbered:
            {numbered}

            Text copied from the wiki page:
            ---
            {reference}
            ---
            """;

        var json = await ai.AskJsonAsync(
            ExtractInstruction(layer), input, Schema, ct, Model, MaxOutputTokens);
        if (json is not { } root) return null;

        if (ReadLines(root) is not { } got)
        {
            log.LogDebug("나무위키 추출 응답 형식 오류 ({Layer})", layer);
            return null;
        }
        if (!Validate(lines, got, layer, out var why, extracting: true))
        {
            log.LogInformation("나무위키 추출 버림 ({Layer}) — {Why}", layer, why);
            return null;
        }
        return got;
    }

    /// <summary>
    /// 추출 프롬프트. 실제 문서에서 확인된 함정을 못 박는다(§9.10) —
    /// 파트 분배 색상표의 가수 이름, 한 문서 안의 여러 버전, 두 가지 배치.
    /// </summary>
    private static string ExtractInstruction(Layer layer)
    {
        var want = layer == Layer.Pronunciation
            ? """
              You are copying the PRONUNCIATION line: the hangul that spells out how the Japanese
              SOUNDS. It reads like the Japanese when spoken aloud (돗토 사가시테탄다요), and it does
              not make sense as a Korean sentence.
              """
            : """
              You are copying the TRANSLATION line: the hangul that gives the MEANING in natural
              Korean (계속 찾고 있었어). It reads as an ordinary Korean sentence and does not sound
              like the Japanese.
              """;

        return $"""
            You are given (1) a Japanese song's lines, numbered, and (2) text copied from a Korean
            wiki page about that song. People have already written a Korean pronunciation line and a
            Korean translation line under each Japanese line on that page.

            Your job is to COPY the right value for each numbered line. You never write your own
            pronunciation or translation - you only find what is already there.

            {want}

            Both the pronunciation and the translation are written in hangul, so tell them apart by
            what they do, not by where they sit.

            Rules:
            - Copy the text from the page exactly as written, including its spacing and marks.
            - If a numbered line does not appear on the page, output an empty string for that line.
              An empty answer is correct and expected. An invented one is not - never fill a gap
              with your own writing.
            - The page lays the lyrics out in one of two ways, and you must handle both:
              (a) three rows per line - Japanese, then pronunciation, then translation;
              (b) three columns headed 원문 / 발음 / 번역, each cell holding many lines in order.
            - The page contains a lot that is NOT lyrics. Ignore all of it: singer part labels and
              colour legends (for example 나나오아카리 / Sou / 함께, or 카가미네 린 / 미키토P / 합창),
              section headings, footnote markers, edit links, and commentary about the song.
            - The page may carry more than one version of the song, such as a TV size next to the
              full version. Use whichever one matches the numbered lines you were given.
            - Match by the Japanese text itself, not by position. The page may begin at a different
              point, repeat a chorus, or skip lines.
            - A numbered line with no words in it (empty, or only ♪ ～ -) gets an empty string.
            - Output exactly one entry per numbered input line, in the same order, and nothing else.
            """;
    }

    /// <summary>
    /// 받은 값을 기계로 검사한다(§2.5b-4). <b>기준은 생성이든 추출이든 같다</b> — 발음은 한글만,
    /// 번역에 가나가 남으면 안 된다.
    ///
    /// 다른 것은 <b>걸렸을 때 버리는 단위</b>다. 생성은 우리가 만든 값이라 하나라도 틀리면 그
    /// 덩어리를 통째로 버린다. 베껴 오는 경우는 사람이 쓴 원본에 영어가 섞여 있는 일이 흔해서
    /// (`오카시쿠앗테 와랏타(Fu Fu！)`) 통째로 버리면 멀쩡한 열네 줄까지 같이 날아간다.
    /// 그래서 <b>걸린 줄만 비우고</b> 나머지는 살린다 — 빈 자리는 AI 생성이 메운다(§9.5).
    ///
    /// 다만 여러 줄이 한꺼번에 걸리는 건 '영어가 섞였다'가 아니라 <b>줄이 밀렸다</b>는 뜻이다.
    /// 그때는 남은 줄도 못 믿으므로 덩어리를 통째로 버린다.
    /// </summary>
    private static bool Validate(
        IReadOnlyList<string> src, List<string> got, Layer layer, out string? why, bool extracting = false)
    {
        // 줄 수가 어긋나면 이후 줄이 전부 밀린다 — 다른 검사를 볼 것도 없다.
        if (got.Count != src.Count)
        {
            why = $"줄 수가 다르다(보낸 {src.Count}, 받은 {got.Count})";
            return false;
        }

        int checkable = 0, dropped = 0;
        string? firstBad = null;

        for (var i = 0; i < src.Count; i++)
        {
            var line = src[i];
            var value = got[i];
            var source = JapaneseText.Measure(line);

            // 글자가 하나도 없는 줄(빈 줄·간주 ♪)에 값을 붙였으면 가사를 지어낸 것이다.
            if (source.Total == 0)
            {
                if (value.Length == 0) continue;
                if (!Reject(i, $"{i + 1}번 줄은 가사가 없는데 값이 붙었다: \"{Clip(value)}\"", out why)) return false;
                continue;
            }

            checkable++;

            // 베껴 오는 경우엔 못 찾은 줄이 있는 게 정상이다 — 그 자리는 AI 생성이 메운다(§9.5).
            if (value.Length == 0 && extracting) continue;

            // 반대로 가사가 있는 줄이 비어 오면 그 줄만 층이 사라진다 — 이것도 실패로 본다.
            if (value.Length == 0)
            {
                why = $"{i + 1}번 줄이 비어 왔다: \"{Clip(line)}\"";
                return false;
            }

            // 사람이 쓴 음차에는 장음 기호가 그대로 남아 있다(세ー노) — 베껴 온 값에서는 허용한다(§9.4).
            var result = JapaneseText.Measure(extracting
                ? value.Replace("ー", "").Replace("ｰ", "").Replace("〜", "").Replace("～", "")
                : value);

            if (layer == Layer.Pronunciation)
            {
                // 발음에 원문 글자가 남아 있으면 음차에 실패한 것이다(로마자도 마찬가지 — 한글로 적어야 한다).
                if (result.Kana > 0 || result.Han > 0 || result.Latin > 0)
                {
                    if (!Reject(i, $"{i + 1}번 줄 발음에 한글이 아닌 글자가 남았다: \"{Clip(value)}\"", out why)) return false;
                    continue;
                }
                if (result.Hangul == 0)
                {
                    if (!Reject(i, $"{i + 1}번 줄 발음에 한글이 없다: \"{Clip(value)}\"", out why)) return false;
                    continue;
                }
            }
            // 번역에 가나가 남아 있으면 번역이 안 된 줄이다. 한자는 한국어 문장에도 쓰이므로 보지 않는다.
            else if (result.Kana > 0)
            {
                if (!Reject(i, $"{i + 1}번 줄 번역에 가나가 남았다: \"{Clip(value)}\"", out why)) return false;
                continue;
            }
        }

        // 검사 대상 줄 중 이만큼 넘게 걸렸으면 영어가 섞인 게 아니라 줄이 밀린 것이다.
        if (extracting && dropped > 0 && dropped * 3 > checkable)
        {
            why = $"{checkable}줄 중 {dropped}줄이 걸렸다 — 줄이 밀린 것으로 본다. 첫 사유: {firstBad}";
            return false;
        }

        why = null;
        return true;

        // 생성이면 그대로 실패, 추출이면 그 줄만 비우고 계속한다.
        bool Reject(int i, string reason, out string? failWhy)
        {
            firstBad ??= reason;
            if (!extracting) { failWhy = reason; return false; }
            got[i] = "";
            dropped++;
            failWhy = null;
            return true;
        }
    }


    private static string Clip(string s) => s.Length <= 30 ? s : s[..30] + "…";
}
