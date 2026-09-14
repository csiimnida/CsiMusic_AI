using System.Threading.RateLimiting;
using CsiMusic.Web.Auth;
using CsiMusic.Web.Data;
using CsiMusic.Web.Infrastructure;
using CsiMusic.Web.Options;
using CsiMusic.Web.Repositories;
using CsiMusic.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Serilog;
using Serilog.Events;

// Microsoft.Data.Sqlite 는 진짜 async 가 아니라 매 쿼리가 스레드풀 스레드를 통째로 블로킹한다.
// Pi(소수 코어)에서 기본 최소 워커 수 = 코어 수라, 페이지 로드 시 몰리는 병렬 요청 몇 개만으로도
// 풀이 고갈되고(초당 1~2개씩만 증설) 나머지 요청이 pending 된다. 최소치를 올려 버스트를 흡수한다.
ThreadPool.GetMinThreads(out _, out var minCompletionPortThreads);
ThreadPool.SetMinThreads(Math.Max(Environment.ProcessorCount * 8, 32), minCompletionPortThreads);

var builder = WebApplication.CreateBuilder(args);

// .env(Python 호환) 로더 — docker 밖(systemd/직접 실행)에서도 .env 를 읽어 "App" 설정에 반영한다.
// docker 배포에선 .dockerignore 가 .env 를 빼 컨테이너 안엔 없으므로 no-op(= compose 의 App__* 매핑 사용).
// app_settings.json(관리자 편집)보다 먼저 로드해 관리자 편집이 여전히 최우선이 되게 한다.
CsiMusic.Web.Infrastructure.DotEnvLoader.Apply(builder);

// 관리자 /settings 편집 오버라이드 — data/app_settings.json (env/appsettings 보다 우선, 재시작 시 반영).
// Python 의 .env 편집(재시작 후 반영)에 대응. 컨테이너에선 .env 를 못 만지므로 마운트된 data 볼륨에 저장.
builder.Configuration.AddJsonFile(
    Path.Combine(builder.Environment.ContentRootPath, "data", "app_settings.json"),
    optional: true, reloadOnChange: false);

// --- 로깅 (Serilog rolling file — Python RotatingFileHandler 대응) ---
// 기본 레벨을 Warning 으로 낮춘다. ASP.NET Core 는 Information 에서 요청당 ~7줄(request/routing/action/result…)을
// USB 의 롤링 파일에 동기로 쓰는데, Pi 에선 이 디스크 I/O 가 오디오·DB 와 경쟁해 요청 전체를 느리게 만든다.
// (Python/uvicorn 은 요청당 1줄 수준이었다.) 앱 자체 로그(CsiMusic.*)와 起動 배너만 Information 으로 남긴다.
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .MinimumLevel.Warning()
    .MinimumLevel.Override("CsiMusic", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(builder.Environment.ContentRootPath, "logs", "server.log"),
        rollOnFileSizeLimit: true, fileSizeLimitBytes: 5 * 1024 * 1024, retainedFileCountLimit: 3));

// --- 설정 ---
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));

// --- 경로 / 데이터 ---
var paths = new AppPaths(builder.Environment.ContentRootPath);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
builder.Services.AddSingleton<DatabaseInitializer>();

// --- 리포지토리 ---
// 프로필 존재 확인은 요청마다(=[RequireProfile]) 나가는 조회라 싱글턴 캐시로 받는다.
builder.Services.AddSingleton<ProfileExistsCache>();
builder.Services.AddScoped<ITrackRepository, TrackRepository>();
builder.Services.AddScoped<IProfileRepository, ProfileRepository>();
builder.Services.AddScoped<IQueueRepository, QueueRepository>();
builder.Services.AddScoped<IPlaylistRepository, PlaylistRepository>();
builder.Services.AddScoped<IHistoryRepository, HistoryRepository>();
builder.Services.AddScoped<ILikeRepository, LikeRepository>();

// --- 서비스 ---
builder.Services.AddScoped<YtDlpClient>();
builder.Services.AddScoped<CacheService>();
builder.Services.AddSingleton<RuntimeSettingsService>();
// 호스트 재배포 트리거(신호파일 → Pi 의 update-watcher.sh). 상태는 DATA_DIR 볼륨에 오간다.
builder.Services.AddSingleton<HostUpdateService>();
builder.Services.AddSingleton<LyricsBatchService>();
// 자체 싱크(자동 정렬) — 온디맨드 Python 사이드카 워밍 워커. 배치는 자체 Task 라 HostedService 불필요.
builder.Services.AddSingleton<SelfSyncService>();
// 싱크 결과 저장 공용부(자막 싱크·브라우저 계산·사이드카가 같은 규칙으로 저장).
builder.Services.AddSingleton<LyricsSyncStore>();
builder.Services.AddSingleton<SubtitleSyncBatchService>();
builder.Services.AddSingleton<MusicMetaBatchService>();
// 유튜브 자막 싱크(B-1) — 자막 파일(timedtext) 다운로드용 HttpClient. 자막은 수십 KB 라 20초면 충분.
builder.Services.AddHttpClient<YoutubeSubtitleService>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("CsiMusic/1.0 (LAN music server)");
});
// 가사 조회(LRCLIB) — 식별 가능한 User-Agent 권장, 8초 타임아웃.
builder.Services.AddHttpClient<LyricsService>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(8);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("CsiMusic/1.0 (https://github.com/; LAN music server)");
});
// 나무위키(발음·번역의 1순위 소스, 계획 §9) — 문서가 크고(수백 KB) 사람이 버튼을 눌렀을 때만
// 도는 경로라 20초로 넉넉히 준다. 재생 경로를 붙잡지 않는다.
builder.Services.AddHttpClient<NamuWikiClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("CsiMusic/1.0 (LAN music server)");
});
// --- AI 보조 (가사 검색의 제목 파싱 A / 가사 검증 B) ---
// 키가 없으면 AiClient 가 스스로 비활성이 되고 각 서비스가 전부 null/통과를 돌려주므로, 등록 자체는
// 무조건 해 둔다(조건부 등록은 LyricsService 생성자가 못 채워져 부팅이 죽는다).
// 타임아웃 12초: 판정 프롬프트는 짧지만 flash-lite 라도 콜드 스타트가 몇 초 걸린다. 실패해도
// 규칙 기반으로 폴백하므로 재생 요청이 이 시간만큼 늦어지는 게 최악이다.
builder.Services.AddSingleton<CsiMusic.Web.Services.Ai.AiCache>();
builder.Services.AddSingleton<CsiMusic.Web.Services.Ai.AiTitleParser>();
builder.Services.AddSingleton<CsiMusic.Web.Services.DisplayNames>();
builder.Services.AddSingleton<CsiMusic.Web.Services.Ai.AiLyricsVerifier>();
builder.Services.AddSingleton<CsiMusic.Web.Services.Ai.AiLyricsLayers>();
builder.Services.AddSingleton<CsiMusic.Web.Services.NamuLyricsSource>();
builder.Services.AddSingleton<CsiMusic.Web.Services.LyricsLayersService>();
builder.Services.AddHttpClient(CsiMusic.Web.Services.Ai.AiClient.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(12);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("CsiMusic/1.0 (LAN music server)");
});
// (C) 발음·번역은 판정이 아니라 생성이라 응답이 훨씬 길다. 60초: 상위 모델 + 15줄 문장 기준.
// 사람이 버튼을 눌러 도는 기능이라(계획 §2.6) 재생 경로를 붙잡지 않는다.
builder.Services.AddHttpClient(CsiMusic.Web.Services.Ai.AiClient.LongHttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("CsiMusic/1.0 (LAN music server)");
});

builder.Services.AddSingleton<CsiMusic.Web.Services.Ai.AiClient>();

builder.Services.AddSingleton<DownloadProgressStore>();
builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DownloadQueue>());
// 라우드니스 측정(다운로드 직후) + 기존 곡 백필. 백필은 다운로드가 없을 때만 도는 저우선 워커.
builder.Services.AddSingleton<LoudnessAnalyzer>();
builder.Services.AddSingleton<LoudnessTargetService>();
builder.Services.AddSingleton<LoudnessBackfillService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LoudnessBackfillService>());

builder.Services.AddSingleton<ClipEncoder>();
builder.Services.AddSingleton<ClipQueue>();
builder.Services.AddSingleton<ClipWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClipWorker>());

// --- 인증/인가 ---
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<AdminTotpService>();
builder.Services.AddScoped<CurrentProfileFilter>();

builder.Services.AddAuthentication(AuthSchemes.User)
    .AddCookie(AuthSchemes.User, o => ConfigureApiCookie(o, "session", isAdmin: false))
    .AddCookie(AuthSchemes.Admin, o => ConfigureApiCookie(o, "admin_session", isAdmin: true));

builder.Services.AddAuthorization(o =>
    o.AddPolicy(AuthSchemes.AdminPolicy, p =>
        p.AddAuthenticationSchemes(AuthSchemes.Admin).RequireAuthenticatedUser()));

// --- 레이트리밋 (Python 자체 슬라이딩 윈도우 대응) ---
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("search", ctx => KeyedFixedWindow(ctx, permit: 30));
    o.AddPolicy("download", ctx => KeyedFixedWindow(ctx, permit: 10));
    // 관리자 로그인은 세션 쿠키가 없으므로 IP 기준. 비번+TOTP 브루트포스를 분당 10회로 제한한다.
    // (정상 로그인은 드물어 지장 없음. 리버스 프록시 뒤면 LAN 이 한 버킷을 공유하지만 그래도 유효.)
    o.AddPolicy("login", ctx => IpFixedWindow(ctx, permit: 10));
});

builder.Services.AddControllersWithViews();

// --- 응답 압축 (텍스트 자산만) ---
// app.js/CSS/JSON 은 비압축이라 전송량이 크다. 오디오·이미지는 이미 압축돼 있어 제외(재압축 낭비).
// Pi 앞단 리버스 프록시가 이미 gzip/brotli 하면 이 계층은 사실상 무해한 no-op 이 된다.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    o.MimeTypes = new[]
    {
        "text/html", "text/css", "text/plain",
        "application/javascript", "text/javascript", "application/json",
        "image/svg+xml",
    };
});

var app = builder.Build();

// 시작 시 DB 초기화 + 캐시 동기화 (Python lifespan 대응). 다운로드 워커는 HostedService 로 자동 기동.
await app.Services.GetRequiredService<DatabaseInitializer>().RunAsync();

app.UseResponseCompression();
app.UseMiddleware<StaticRevalidateMiddleware>();
app.UseStaticFiles();
app.UseMiddleware<JsonOnPostMiddleware>();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// --- 로컬 헬퍼 ---

// API 는 쿠키 인증 실패 시 로그인 페이지로 리다이렉트하지 않고 401/403 을 그대로 반환한다.
static void ConfigureApiCookie(CookieAuthenticationOptions o, string cookieName, bool isAdmin)
{
    o.Cookie.Name = cookieName;
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.ExpireTimeSpan = TimeSpan.FromHours(24);
    o.SlidingExpiration = false;
    o.Events = new CookieAuthenticationEvents
    {
        OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; },
        OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; },
        OnValidatePrincipal = ctx =>
        {
            var store = ctx.HttpContext.RequestServices.GetRequiredService<SessionStore>();
            var sid = ctx.Principal?.FindFirst(AuthSchemes.SidClaim)?.Value;
            var active = isAdmin ? store.IsAdminActive(sid) : store.IsUserActive(sid);
            if (!active) ctx.RejectPrincipal();
            return Task.CompletedTask;
        },
    };
}

static RateLimitPartition<string> KeyedFixedWindow(HttpContext ctx, int permit)
{
    var key = ctx.Request.Cookies["session"] ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = permit,
        Window = TimeSpan.FromMinutes(1),
    });
}

static RateLimitPartition<string> IpFixedWindow(HttpContext ctx, int permit)
{
    var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return RateLimitPartition.GetFixedWindowLimiter($"ip:{key}", _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = permit,
        Window = TimeSpan.FromMinutes(1),
    });
}
