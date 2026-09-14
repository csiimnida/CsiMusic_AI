using System.Net;
using System.Text;
using System.Text.Json;
using CsiMusic.Web.Options;
using Microsoft.Extensions.Options;

namespace CsiMusic.Web.Services.Ai;

/// <summary>
/// Google Generative Language API(Gemini) 호출기 — 가사 검색의 보조 판단(제목 파싱 A / 가사 검증 B)과
/// 사람이 요청할 때만 도는 생성 작업(발음·번역 C)에 쓴다.
///
/// 설계 원칙은 하나다: <b>AI 는 절대 필수 경로가 아니다.</b> 키가 없거나, 할당량이 끝났거나, 네트워크가
/// 막혔거나, 응답이 깨졌으면 조용히 null 을 돌려주고 호출측은 기존 규칙 기반 로직으로 그대로 간다.
/// 라파(오프라인일 수 있는 홈서버)에서 도는 서버라 이 폴백이 없으면 가사 기능 전체가 인질이 된다.
///
/// 왜 Gemini 인가: A·B 는 곡당 1회이고 결과를 DB 에 캐시하므로 총 호출량이 작다(보관함 수백 곡 =
/// 수백 회). flash-lite 무료 티어로 충분하고, Pi 5(4GB) CPU 를 전혀 쓰지 않는다 — 로컬 3B 모델은
/// RAM 은 되지만 SelfSync whisper 사이드카와 공존이 어렵고 곡당 수십 초라 배치가 성립하지 않는다.
///
/// 응답은 항상 JSON 으로 강제한다(responseMimeType + responseSchema). 자유 텍스트를 파싱하는 것보다
/// 훨씬 안정적이고, 스키마를 어긴 응답은 그냥 실패로 처리하면 된다.
/// </summary>
public sealed class AiClient(
    IHttpClientFactory httpFactory, IOptions<AppOptions> options, ILogger<AiClient> log)
{
    private readonly AppOptions _opt = options.Value;

    /// <summary>IHttpClientFactory 의 명명 클라이언트 이름(Program.cs 등록과 짝).</summary>
    public const string HttpClientName = "ai";

    /// <summary>
    /// 오래 걸리는 작업(C 발음·번역)용 명명 클라이언트. 위 12초 타임아웃은 짧은 판정 기준이라
    /// 15줄짜리 문장을 짓는 작업에는 모자란다 — 특히 AiLyricsModel 로 상위 모델을 지정했을 때
    /// 조용히 타임아웃으로 실패한다. 판정 두 작업의 폴백 지연은 그대로 두고 이 작업만 길게 준다.
    /// </summary>
    public const string LongHttpClientName = "ai-long";

    private const string Base = "https://generativelanguage.googleapis.com/v1beta/models";

    // 연속 실패가 이만큼 쌓이면 회로를 열어 CooldownSeconds 동안 호출을 아예 멈춘다.
    // (키 오타·할당량 소진 상태에서 배치가 곡마다 8초씩 까먹는 걸 막는다.)
    private const int FailuresToTrip = 3;
    private const int CooldownSeconds = 300;

    // 두 작업의 답은 JSON 몇 줄이라 이보다 훨씬 짧다. 넉넉히 잡아 두는 이유는 사고를 못 끄는 모델
    // (AiThinkingBudget 음수)에서 사고 토큰이 여기서 나가기 때문 — 빠듯하면 본문이 빈 응답이 온다.
    private const int MaxOutputTokens = 2048;

    private readonly object _lock = new();
    private int _consecutiveFailures;
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;
    private DateOnly _quotaDay;
    private int _callsToday;

    /// <summary>키가 있고 기능 토글이 켜져 있는지. 화면·로그에서 "AI 보조 켜짐"의 근거.</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(_opt.GoogleAiApiKey);

    /// <summary>지금 이 순간 실제로 호출을 시도할 수 있는지(회로 차단·일일 상한 반영).</summary>
    public bool Available
    {
        get
        {
            if (!Configured) return false;
            lock (_lock) return !IsTrippedLocked() && !IsOverDailyCapLocked();
        }
    }

    /// <summary>관리자 화면용 상태 스냅샷.</summary>
    public Dictionary<string, object?> Status()
    {
        lock (_lock)
        {
            RollDayLocked();
            return new Dictionary<string, object?>
            {
                ["configured"] = Configured,
                ["model"] = _opt.AiModel,
                ["available"] = Configured && !IsTrippedLocked() && !IsOverDailyCapLocked(),
                ["callsToday"] = _callsToday,
                ["dailyCap"] = _opt.AiDailyCap,
                ["thinkingBudget"] = _opt.AiThinkingBudget,
                ["thinkingLevel"] = _opt.AiThinkingLevel,
                ["consecutiveFailures"] = _consecutiveFailures,
                ["cooldownUntil"] = _openUntil > DateTimeOffset.UtcNow
                    ? _openUntil.ToUnixTimeSeconds() : (long?)null,
            };
        }
    }

    /// <summary>
    /// 프롬프트를 보내고 JSON 응답을 파싱해 돌려준다. 실패(미설정·차단·할당량·네트워크·스키마 위반)는
    /// 전부 null — 호출측은 규칙 기반으로 폴백해야 한다.
    /// </summary>
    /// <param name="instruction">시스템 지시(역할·출력 규칙).</param>
    /// <param name="input">사용자 입력(판단 대상 데이터).</param>
    /// <param name="schema">responseSchema 로 넘길 JSON 스키마. 모델 출력 형태를 고정한다.</param>
    /// <param name="model">이 호출에만 쓸 모델. 비우면 기본 <c>AiModel</c>.</param>
    /// <param name="maxOutputTokens">이 호출에만 쓸 출력 상한. 비우면 <c>MaxOutputTokens</c>(2048).</param>
    /// <param name="longRunning">긴 응답을 기다리는 클라이언트를 쓸지(C 발음·번역).</param>
    public async Task<JsonElement?> AskJsonAsync(
        string instruction, string input, object schema, CancellationToken ct = default,
        string? model = null, int? maxOutputTokens = null, bool longRunning = false)
    {
        if (!Configured) return null;
        lock (_lock)
        {
            RollDayLocked();
            if (IsTrippedLocked()) return null;
            if (IsOverDailyCapLocked())
            {
                log.LogDebug("AI 일일 상한({Cap}) 도달 — 호출 생략", _opt.AiDailyCap);
                return null;
            }
            _callsToday++;
        }

        // 판정 작업이라 temperature 0 — 같은 입력이면 같은 답이 나오는 쪽이 캐시와도 맞는다.
        // 사고 설정은 모델 세대에 따라 키가 달라서 사전으로 조립한다(익명 타입으로는 조건부 필드가 안 된다).
        // 모델은 작업별로 바꿀 수 있다 — 음차·번역처럼 무거운 작업만 상위 모델로 돌리기 위해서다(계획 §2.5b-2).
        // 사고 설정은 세대에 따라 키가 다르므로 반드시 실제로 부를 모델을 기준으로 정해야 한다.
        var useModel = string.IsNullOrWhiteSpace(model) ? _opt.AiModel : model.Trim();
        var generationConfig = new Dictionary<string, object?>
        {
            ["temperature"] = 0.0,
            ["responseMimeType"] = "application/json",
            ["responseSchema"] = schema,
            ["maxOutputTokens"] = maxOutputTokens is > 0 ? maxOutputTokens.Value : MaxOutputTokens,
        };
        if (ThinkingConfig(useModel, _opt.AiThinkingBudget, _opt.AiThinkingLevel) is { } thinking)
            generationConfig["thinkingConfig"] = thinking;

        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = instruction } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = input } } } },
            generationConfig,
        };

        var url = $"{Base}/{useModel}:generateContent";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            // 키는 헤더로 보낸다 — 쿼리스트링에 넣으면 프록시·서버 로그에 그대로 남는다.
            req.Headers.Add("x-goog-api-key", _opt.GoogleAiApiKey);

            // 싱글턴이 타입 클라이언트를 붙잡으면 핸들러가 영원히 회전하지 않는다 → 매번 팩터리에서
            // 받는다(반환된 HttpClient 는 핸들러를 소유하지 않으므로 Dispose 하지 않는 게 관례다).
            var http = httpFactory.CreateClient(longRunning ? LongHttpClientName : HttpClientName);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await SafeReadAsync(resp, ct);
                // 429(할당량)·403(키 문제)은 재시도해도 같은 결과다 → 즉시 회로를 연다.
                var hard = resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden
                           or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest;
                log.LogWarning("AI 호출 실패 {Status}: {Detail}", (int)resp.StatusCode, detail);
                RecordFailure(hard);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var text = ExtractText(doc.RootElement);
            if (string.IsNullOrWhiteSpace(text))
            {
                // 조용히 실패하면 원인을 못 찾는다. 빈 본문은 대개 MAX_TOKENS(사고 토큰이 다 먹음)
                // 아니면 SAFETY 차단이고, 둘은 대응이 전혀 다르다.
                log.LogWarning("AI 응답에 본문이 없다 (finishReason={Reason}) — 사고 예산/안전필터를 확인하라",
                    FinishReason(doc.RootElement) ?? "unknown");
                RecordFailure(false);
                return null;
            }

            using var parsed = JsonDocument.Parse(text);
            RecordSuccess();
            return parsed.RootElement.Clone();
        }
        // 요청 중단(클라이언트 이탈)은 위로 — 실패로 세지 않는다.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                  or OperationCanceledException or JsonException)
        {
            log.LogDebug("AI 호출 예외: {Msg}", e.Message);
            RecordFailure(false);
            return null;
        }
    }

    /// <summary>
    /// 모델 세대에 맞는 thinkingConfig 를 만든다. <b>Gemini 3 이상은 thinkingLevel, 2.5 계열은
    /// thinkingBudget</b> 을 쓰고 둘을 같이 보내면 안 된다 — 세대를 안 보고 한쪽만 고정하면
    /// 모델을 바꾸는 순간 조용히 잘못된 사고 설정이 나가거나 400 이 난다.
    ///
    /// 우리 작업은 짧은 판정이거나 형식이 고정된 생성이라 사고가 필요 없다. 사고가 켜져 있으면 사고 토큰이
    /// maxOutputTokens 를 먼저 다 써서 본문이 빈 응답이 오므로 최소치로 누른다.
    /// (Gemini 3 Flash 계열의 최저는 MINIMAL 이고 완전한 '끔'은 없다.)
    ///
    /// null 을 돌려주면 호출측은 사고 설정을 아예 보내지 않는다(budget 음수 = 탈출구).
    /// </summary>
    internal static object? ThinkingConfig(string model, int budget, string? level)
    {
        if (budget < 0) return null;
        if (!string.IsNullOrWhiteSpace(level))
            return new { thinkingLevel = level.Trim().ToUpperInvariant() };
        return ModelMajorVersion(model) >= 3
            ? new { thinkingLevel = "MINIMAL" }
            : new { thinkingBudget = budget };
    }

    /// <summary>"gemini-3.5-flash-lite" → 3. 세대를 못 읽으면 0(=구세대 취급).</summary>
    internal static int ModelMajorVersion(string? model)
    {
        const string prefix = "gemini-";
        if (model is null) return 0;
        var i = model.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return 0;
        var rest = model[(i + prefix.Length)..];
        var end = 0;
        while (end < rest.Length && char.IsAsciiDigit(rest[end])) end++;
        return end > 0 && int.TryParse(rest[..end], out var v) ? v : 0;
    }

    /// <summary>candidates[0].content.parts[*].text 를 이어붙인다. 응답 형태가 다르면 null.</summary>
    private static string? ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var cands) || cands.ValueKind != JsonValueKind.Array
            || cands.GetArrayLength() == 0) return null;
        if (!cands[0].TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            return null;
        var sb = new StringBuilder();
        foreach (var p in parts.EnumerateArray())
            if (p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                sb.Append(t.GetString());
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>candidates[0].finishReason — 빈 본문의 원인 진단용(STOP·MAX_TOKENS·SAFETY…).</summary>
    private static string? FinishReason(JsonElement root) =>
        root.TryGetProperty("candidates", out var c) && c.ValueKind == JsonValueKind.Array
        && c.GetArrayLength() > 0 && c[0].TryGetProperty("finishReason", out var f)
        && f.ValueKind == JsonValueKind.String ? f.GetString() : null;

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var s = await resp.Content.ReadAsStringAsync(ct);
            return s.Length > 300 ? s[..300] : s;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return "(본문 읽기 실패)";
        }
    }

    private void RecordSuccess()
    {
        lock (_lock) { _consecutiveFailures = 0; _openUntil = DateTimeOffset.MinValue; }
    }

    private void RecordFailure(bool hard)
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            if (hard || _consecutiveFailures >= FailuresToTrip)
            {
                _openUntil = DateTimeOffset.UtcNow.AddSeconds(CooldownSeconds);
                log.LogWarning("AI 호출을 {Sec}초 동안 중단한다(연속 실패 {N}회)", CooldownSeconds, _consecutiveFailures);
            }
        }
    }

    private bool IsTrippedLocked() => DateTimeOffset.UtcNow < _openUntil;

    private bool IsOverDailyCapLocked() => _opt.AiDailyCap > 0 && _callsToday >= _opt.AiDailyCap;

    private void RollDayLocked()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_quotaDay == today) return;
        _quotaDay = today;
        _callsToday = 0;
    }
}
