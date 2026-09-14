namespace CsiMusic.Web.Common;

/// <summary>
/// 가사가 일본어인지 글자 코드값만으로 가른다 — AI 에 묻지 않는다(docs/LYRICS_LAYERS_PLAN.md §2.4).
/// 발음·번역을 만들기 전에 대상을 거르는 용도라, 놓치는 쪽이 엉뚱한 곡에 일본어 발음을 붙이는 것보다 낫다.
/// </summary>
public static class JapaneseText
{
    /// <summary>가나가 이보다 적으면 대상이 아니다 — 한두 글자 섞인 것을 일본어로 보지 않는다.</summary>
    private const int MinKana = 3;

    /// <summary>글자 종류를 센 것 중 가나 비율이 이보다 낮으면 대상이 아니다.</summary>
    private const double MinKanaRatio = 0.05;

    /// <summary>가사 본문이 일본어인가.</summary>
    public static bool IsJapanese(string? text) => Measure(text).IsJapanese;

    /// <summary>판별에 쓴 글자 수 — 거절 이유를 알려 줄 때 쓴다.</summary>
    public readonly record struct Counts(int Kana, int Han, int Hangul, int Latin)
    {
        /// <summary>비율의 분모. 문장부호·숫자·공백은 어느 언어에나 있어 세지 않는다.</summary>
        public int Total => Kana + Han + Hangul + Latin;

        public double KanaRatio => Total == 0 ? 0 : (double)Kana / Total;

        /// <summary>한자만 있고 가나가 없으면 중국어일 수 있으므로 대상에서 뺀다.</summary>
        public bool IsJapanese => Kana >= MinKana && KanaRatio >= MinKanaRatio;
    }

    /// <summary>글자 종류별로 센다. 판별 근거가 되는 것은 가나뿐이고, 나머지는 분모로만 쓴다.</summary>
    public static Counts Measure(string? text)
    {
        int kana = 0, han = 0, hangul = 0, latin = 0;
        foreach (var ch in text ?? "")
        {
            int o = ch;
            if (IsKana(o)) kana++;
            else if (o is >= 0x4E00 and <= 0x9FFF) han++;          // CJK 통합 한자 — 중국어와 겹쳐 단독 근거로 쓰지 않는다.
            else if (o is >= 0xAC00 and <= 0xD7A3) hangul++;       // 한글 음절.
            else if (o is (>= 0x41 and <= 0x5A) or (>= 0x61 and <= 0x7A)) latin++;
        }
        return new Counts(kana, han, hangul, latin);
    }

    /// <summary>히라가나·가타카나·반각 가타카나. 가나가 있다는 것이 일본어의 결정적 증거다.</summary>
    private static bool IsKana(int o) =>
        o is (>= 0x3040 and <= 0x309F)     // 히라가나(반복 기호 ゝゞ 포함).
          or (>= 0x30A0 and <= 0x30FF)     // 가타카나(장음 ー 포함).
          or (>= 0xFF66 and <= 0xFF9D)     // 반각 가타카나.
        // ゠(U+30A0)·・(U+30FB)는 가나가 아니라 문장부호이고 한국어 표기에도 쓰여 세지 않는다.
        && o is not 0x30A0 and not 0x30FB;
}
