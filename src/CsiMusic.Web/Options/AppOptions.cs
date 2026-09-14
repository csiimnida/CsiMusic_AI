namespace CsiMusic.Web.Options;

/// <summary>환경설정 (.env / appsettings 의 "App" 섹션) — Python app/config.py Settings 대응.</summary>
public sealed class AppOptions
{
    // 필수
    public string AppPassword { get; set; } = "";
    public string SessionSecret { get; set; } = "";

    // 관리자 (둘 다 있어야 활성)
    public string AdminPassword { get; set; } = "";
    public string AdminTotpSecret { get; set; } = "";

    // 선택 (기본값 제공)
    public double CacheLimitGb { get; set; } = 100.0;
    public int MaxConcurrentDownloads { get; set; } = 2;
    public int SearchCacheTtlSeconds { get; set; } = 3600;
    public string LanBindIp { get; set; } = "127.0.0.1";
    public string? CookiesFile { get; set; }

    /// <summary>
    /// 라우드니스 정규화 목표(LUFS). 비워 두면(기본) 라이브러리 라우드니스 중앙값으로 자동 산출한다 —
    /// 그래야 전체 체감 크기가 지금과 같게 유지된다. 직접 넣으면 그 값으로 고정(디버그/취향 조정용).
    /// </summary>
    public double? TargetLufs { get; set; }

    // 외부 도구 경로 (언어 무관 — Process 로 호출)
    public string YtDlpPath { get; set; } = "yt-dlp";
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>
    /// 상세 화면 루프 클립 생성 여부. 끄면 ClipWorker 가 아예 뜨지 않는다 —
    /// 이미 만들어 둔 클립은 그대로 재생된다(끄는 건 '생산'이지 '소비'가 아니다).
    /// 재생된 곡마다 유튜브에서 30초 영상 창을 새로 받으므로, 대역폭이 아까운 설치에선 이걸 끈다.
    /// </summary>
    public bool ClipsEnabled { get; set; } = true;

    // 자체 싱크(자동 정렬) Python 사이드카 — 컨테이너 기본 경로. 미설치면 --check 가 실패해 '엔진 없음'으로 안내.
    public string SelfSyncPython { get; set; } = "/opt/selfsync-venv/bin/python";
    public string SelfSyncCli { get; set; } = "/app/selfsync/selfsync_cli.py";
    // 곡당 하드 타임아웃(초) — 워커가 한 곡에 멈추면 그 곡만 실패로 처리하고 워커를 재기동.
    public int SelfSyncTimeoutSeconds { get; set; } = 180;
    // 큐가 이만큼(초) 비어 있어야 워밍 워커를 내려 RAM 을 반납한다(그 전엔 모델 유지 → 재로드 방지).
    public int SelfSyncIdleShutdownSeconds { get; set; } = 90;

    // --- AI 보조 (Google Generative Language API / Gemini) ---
    // 가사 검색의 두 판단을 돕고(A 제목 → 곡/아티스트 추출, B 찾은 가사가 그 곡이 맞는지 검증),
    // 사람이 요청할 때만 도는 세 번째 작업이 있다(C 일본어 가사에 한글 발음·번역 붙이기).
    // 키가 비면 기능 전체가 꺼지고 기존 규칙 기반 경로가 그대로 쓰인다(오프라인 홈서버 대비).

    /// <summary>Google AI Studio 에서 발급한 API 키. 비우면 AI 보조가 꺼진다.</summary>
    public string GoogleAiApiKey { get; set; } = "";

    /// <summary>사용할 모델. 두 작업 다 짧은 판정이라 flash-lite 로 충분하고 무료 티어가 있다.</summary>
    public string AiModel { get; set; } = "gemini-3.5-flash-lite";

    /// <summary>하루 최대 호출 수(0 = 무제한). 무료 티어를 넘겨 과금되는 사고를 막는 안전판.</summary>
    public int AiDailyCap { get; set; } = 1000;

    /// <summary>
    /// 사고(thinking) 토큰 예산 — <b>Gemini 2.5 계열에만</b> 쓰인다. 0 = 끔(기본).
    ///
    /// 우리 두 작업은 짧은 판정이라 사고가 필요 없는데, 켜져 있으면 사고 토큰이 maxOutputTokens 를
    /// 먼저 다 써서 <b>본문이 빈 응답</b>(finishReason=MAX_TOKENS)이 돌아온다.
    /// 음수면 사고 설정을 아예 보내지 않는다 — 사고를 못 끄는 모델을 쓸 때의 탈출구.
    /// </summary>
    public int AiThinkingBudget { get; set; }

    /// <summary>
    /// 사고 단계 — <b>Gemini 3 이상</b>에서 쓰는 파라미터(MINIMAL·LOW·MEDIUM·HIGH). 2.5 의
    /// thinkingBudget 을 대체하며 <b>둘을 같이 보내면 안 된다</b>.
    ///
    /// 비워 두면(기본) 모델 이름의 세대를 보고 자동으로 고른다 — gemini-3 이상이면 MINIMAL,
    /// 그 아래면 thinkingBudget. 특정 값을 강제하고 싶을 때만 채운다.
    /// </summary>
    public string AiThinkingLevel { get; set; } = "";

    /// <summary>(A) 제목 → 곡/아티스트 AI 보조를 쓸지.</summary>
    public bool AiTitleEnabled { get; set; } = true;

    /// <summary>
    /// (B) 찾은 가사가 그 곡의 것인지 AI 로 검증할지.
    ///
    /// (A)는 후보를 늘리기만 해서 손해볼 게 없지만, (B)는 유일하게 <i>가사를 없앨 수 있는</i> 기능이다:
    /// 모델이 맞는 가사를 잘못 거부하면 그 곡은 source="none" 으로 7일간 부정캐시된다(7일 뒤 자동
    /// 재조회되고, 관리자 화면에서 즉시 재조회할 수도 있다). 오거부가 보이면
    /// <c>AiLyricsVerifier.RejectConfidence</c>(0.75)를 올린다.
    ///
    /// '자막에서 가사 만들기'는 이 검증을 마지막 안전장치로 쓴다 — 그 경로를 쓸 거면 켜 두는 게 좋다.
    /// </summary>
    public bool AiVerifyEnabled { get; set; } = true;
    /// <summary>
    /// (C) 일본어 가사에 한글 발음·번역을 AI 로 붙일지.
    ///
    /// (A)·(B)와 달리 가사 검색 경로에 끼어들지 않는다 — 사람이 버튼을 눌렀을 때만 도는 별개 기능이라
    /// 꺼도 기존 동작이 전혀 바뀌지 않는다. 곡당 호출이 여러 번이라(청크 × 발음/번역) 토글을 따로 둔다.
    /// </summary>
    public bool AiLyricsEnabled { get; set; } = true;

    /// <summary>
    /// (C) 발음·번역에만 쓸 모델. <b>비우면 <c>AiModel</c> 을 그대로 쓴다.</b>
    ///
    /// (A)·(B)는 짧은 판정이라 flash-lite 로 충분하지만 음차·번역은 실제로 글을 짓는 작업이라 급이
    /// 다르다. 결과가 어설프면 이 값만 상위 모델로 올려 본다 — 나머지 두 작업의 비용은 그대로 둔 채
    /// 이 기능만 무겁게 돌릴 수 있다(계획 §2.5b-2).
    /// </summary>
    public string AiLyricsModel { get; set; } = "";
    /// <summary>
    /// 발음·번역을 만들 때 <b>나무위키를 먼저 보는지</b>(계획 §9).
    ///
    /// 사람이 쓴 음차·번역이 있으면 그것을 베껴 오고, 없는 자리만 AI 가 만든다. 꺼도 기능은
    /// 그대로 동작한다 — AI 생성만으로 돈다(§9.5). <b>외부 사이트에 의존하는 유일한 부분이라
    /// 끄는 길을 열어 둔다</b>: 나무위키가 막히거나 구조가 바뀌어 엉뚱한 값이 붙으면 이걸 끈다.
    /// </summary>
    public bool NamuLayersEnabled { get; set; } = true;



    /// <summary>AI 보조가 실제로 동작하는 조건 — 키가 있어야 한다.</summary>
    public bool AiEnabled => !string.IsNullOrWhiteSpace(GoogleAiApiKey);

    /// <summary>비번과 TOTP 시크릿이 모두 있어야 관리자 기능이 켜진다.</summary>
    public bool AdminEnabled => !string.IsNullOrEmpty(AdminPassword) && !string.IsNullOrEmpty(AdminTotpSecret);

    public long CacheLimitBytes => (long)(CacheLimitGb * 1024 * 1024 * 1024);

    /// <summary>존재하는 cookies 파일 경로만 반환, 없으면 null.</summary>
    public string? CookiesPath => !string.IsNullOrEmpty(CookiesFile) && File.Exists(CookiesFile) ? CookiesFile : null;
}
