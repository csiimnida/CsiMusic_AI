using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsiMusic.Web.Common;
using CsiMusic.Web.Data;
using Dapper;

namespace CsiMusic.Web.Services.Ai;

/// <summary>
/// AI 판단 결과 캐시 — ai_cache 테이블.
///
/// 세 작업(제목 파싱·가사 검증·발음/번역) 모두 <b>같은 입력이면 같은 답</b>이어야 하므로 캐시가
/// 자연스럽다. 실제 효과가 큰 곳은 배치다: '일괄 찾기'를 force 로 두 번 돌려도 두 번째는 API 를
/// 한 번도 부르지 않는다. 부정 결과(모델이 "모르겠다"고 한 것)도 같이 캐시해야 의미가 있다.
///
/// 키는 (작업명 + 프롬프트 버전 + 입력)의 SHA-256 이다. 프롬프트를 고치면 버전을 올려 옛 답을
/// 자동으로 무효화한다 — 프롬프트가 바뀌었는데 옛 답이 계속 나오면 튜닝이 불가능하다.
/// </summary>
public sealed class AiCache(ISqliteConnectionFactory factory, ILogger<AiCache> log)
{
    /// <summary>캐시 수명(초). 90일 — 외부 가사 DB 가 갱신될 수 있으니 영구 보관은 하지 않는다.</summary>
    private const long Ttl = 90 * 86400;

    public static string Key(string task, int version, params string?[] parts)
    {
        var raw = $"{task} v{version} {string.Join(" ", parts.Select(p => p ?? ""))}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    /// <summary>캐시 조회. 없거나 만료·파싱 실패면 null.</summary>
    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        try
        {
            using var conn = factory.Create();
            var row = await conn.QueryFirstOrDefaultAsync(
                "SELECT value, created_at FROM ai_cache WHERE key = @key", new { key });
            if (row is null) return null;
            if (AppTime.NowTs() - (long)row.created_at > Ttl) return null;
            return JsonSerializer.Deserialize<T>((string)row.value);
        }
        catch (Exception e) when (e is JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            log.LogDebug("AI 캐시 조회 실패: {Msg}", e.Message);
            return null;
        }
    }

    /// <summary>캐시 저장. 실패해도 무해(다음에 다시 물어보면 된다)라 예외를 삼킨다.</summary>
    public async Task SetAsync<T>(string key, T value)
    {
        try
        {
            using var conn = factory.Create();
            await conn.ExecuteAsync(
                "INSERT INTO ai_cache (key, value, created_at) VALUES (@key, @value, @now) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value, created_at = excluded.created_at",
                new { key, value = JsonSerializer.Serialize(value), now = AppTime.NowTs() });
        }
        catch (Exception e) when (e is JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            log.LogDebug("AI 캐시 저장 실패: {Msg}", e.Message);
        }
    }

    /// <summary>만료 항목 정리. 관리자 화면에서 수동 실행.</summary>
    public async Task<int> PurgeAsync(bool all = false)
    {
        using var conn = factory.Create();
        return all
            ? await conn.ExecuteAsync("DELETE FROM ai_cache")
            : await conn.ExecuteAsync("DELETE FROM ai_cache WHERE created_at < @cutoff",
                new { cutoff = AppTime.NowTs() - Ttl });
    }
}
