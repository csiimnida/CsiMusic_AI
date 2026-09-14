using CsiMusic.Web.Auth;
using CsiMusic.Web.Common;
using CsiMusic.Web.Data;
using CsiMusic.Web.Services;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CsiMusic.Web.Controllers;

/// <summary>
/// 가사 라우트 — Python routes/lyrics.py 대응. player-config + LRCLIB 조회/캐시 + 오프셋 저장.
/// (자체 싱크/관리자 수동편집·NetEase 폴백은 후속.)
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public sealed class LyricsController(
    ISqliteConnectionFactory factory, RuntimeSettingsService settings, LyricsService lyrics,
    LyricsLayersService layers) : ControllerBase
{
    /// <summary>플레이어 표시 설정(runtime_settings — 관리자 편집 즉시 반영).</summary>
    [HttpGet("player-config")]
    public IActionResult PlayerConfig() => Ok(new
    {
        interlude = new
        {
            gap = settings.Get("interlude_gap"),
            lead = settings.Get("interlude_lead"),
            min = settings.Get("interlude_min"),
            dots = settings.Get("interlude_dots"),
        },
        // 상세보기 가사 창 크기(한 번에 보이는 줄 수).
        lyricWindow = settings.Get("detail_lyric_window"),
        // 아이폰에서 곡별 소리 크기 보정을 켤지 — 잠금화면 재생 회귀 위험이 있어 기본 꺼짐.
        iosLoudness = settings.Get("ios_loudness") >= 1,
    });

    [HttpGet("lyrics/{vid}")]
    public async Task<IActionResult> Get(string vid)
    {
        if (!VideoId.IsValid(vid)) return BadRequest(new { detail = "invalid videoId" });
        using var conn = factory.Create();
        var track = await conn.QueryFirstOrDefaultAsync(
            "SELECT title, uploader, duration, music_track, music_artist, music_album FROM tracks WHERE video_id = @vid",
            new { vid });
        if (track is null) return NotFound(new { detail = "track not found" });

        // 유튜브가 준 구조화 메타가 있으면 가사 검색의 1순위 입력으로 넘긴다(없으면 제목 파싱으로 폴백).
        var music = new MusicMeta((string?)track.music_track, (string?)track.music_artist, (string?)track.music_album);

        // 캐시에 있으면 그대로, 없거나 재조회 대상이면 외부 소스(Unison→LRCLIB→…) 조회 후 캐시.
        var r = await lyrics.GetOrFetchAsync(
            vid, (string)track.title, (string?)track.uploader, (int)(long)track.duration,
            music.IsEmpty ? null : music, HttpContext.RequestAborted);
        // 발음·번역을 나무위키에서 가져왔으면 어느 문서인지 — 화면이 출처를 밝히는 데 쓴다(§9.7).
        // GetOrFetchAsync 가 가사 행을 새로 넣을 수 있으므로 그 뒤에 읽는다.
        var layersSource = await conn.ExecuteScalarAsync<string?>(
            "SELECT layers_source FROM lyrics WHERE video_id = @vid", new { vid });
        return Ok(new
        {
            videoId = r.VideoId,

            synced = r.Synced,
            plain = r.Plain,
            source = r.Source,
            offsetMs = r.OffsetMs,
            locked = r.Locked,
            layers = r.Layers,
            layersSource,
            status = r.Status,
        });
    }

    [HttpPost("lyrics/{vid}/offset")]
    public async Task<IActionResult> SetOffset(string vid, [FromBody] Contracts.LyricsOffsetRequest body)
    {
        if (!VideoId.IsValid(vid)) return BadRequest(new { detail = "invalid videoId" });
        using var conn = factory.Create();

        var locked = await conn.ExecuteScalarAsync<long?>(
            "SELECT locked FROM lyrics WHERE video_id = @vid", new { vid });
        if (locked is not null and not 0) return StatusCode(403, new { detail = "sync locked" });

        var offset = Math.Clamp(body.OffsetMs, -30000, 30000);
        // 가사 행이 있으면 오프셋만 갱신, 없으면 'none' 행을 만들어 오프셋 보관.
        await conn.ExecuteAsync(
            "INSERT INTO lyrics (video_id, source, offset_ms, fetched_at) VALUES (@vid, 'none', @offset, @now) " +
            "ON CONFLICT(video_id) DO UPDATE SET offset_ms = excluded.offset_ms",
            new { vid, offset, now = AppTime.NowTs() });
        return Ok(new { ok = true, offsetMs = offset });
    }

    /// <summary>
    /// 일본어 가사에 한글 발음·번역을 만들어 붙인다(계획 §2.6).
    ///
    /// <b>사람이 버튼을 눌렀을 때만 도는 경로다.</b> 가사 조회(<c>Get</c>)는 이걸 부르지 않는다 —
    /// 곡을 틀 때마다 AI 호출이 저절로 생기면 실제로 보지도 않을 곡에 돈을 쓰게 된다.
    ///
    /// 거절 사유(일본어 아님·이미 있음·잠김)는 오류가 아니라 정상적인 답이므로 <b>200 에 ok=false</b>
    /// 로 싣는다. 오류 코드를 쓰지 않는 이유가 있다 — app.js 의 api() 는 409 를 전부 '프로필 필요'로
    /// 가로채고(app.js:97), apiJson() 은 실패 응답의 본문을 버려서 사유를 화면에 띄울 수가 없다.
    /// </summary>
    [HttpPost("lyrics/{vid}/layers")]
    public async Task<IActionResult> BuildLayers(string vid, [FromQuery] bool force = false)
    {
        if (!VideoId.IsValid(vid)) return BadRequest(new { detail = "invalid videoId" });
        var r = await layers.BuildAsync(vid, force, HttpContext.RequestAborted);
        if (!r.Ok) return Ok(new { ok = false, detail = r.Reason, lines = r.Lines });
        return Ok(new
        {
            ok = true,
            layers = r.Entries,
            layersSource = r.LayersSource,
            lines = r.Lines,
            filled = r.Filled,
            apiCalls = r.ApiCalls,
            failedChunks = r.FailedChunks,
        });
    }
}
