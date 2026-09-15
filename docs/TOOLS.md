# 도구 등록·선택 로직

> 제출물 ① 의 핵심 문서. **어떤 도구가 어떻게 등록되고, 입력에 따라 무엇을 골라 쓰는가.**

---

## 1. 도구 등록 — `Program.cs`

도구는 .NET 의 DI 컨테이너에 싱글턴으로 등록된다.
→ [`src/CsiMusic.Web/Program.cs`](../src/CsiMusic.Web/Program.cs) **96~121줄**

```csharp
// --- AI 보조 (가사 검색의 제목 파싱 A / 가사 검증 B) ---
// 키가 없으면 AiClient 가 스스로 비활성이 되고 각 서비스가 전부 null/통과를 돌려주므로,
// 등록 자체는 무조건 해 둔다(조건부 등록은 LyricsService 생성자가 못 채워져 부팅이 죽는다).
builder.Services.AddSingleton<Ai.AiCache>();           // 결과 캐시 (SQLite ai_cache)
builder.Services.AddSingleton<Ai.AiTitleParser>();     // 도구 A
builder.Services.AddSingleton<DisplayNames>();         // 도구 A 재사용 (화면 표시용)
builder.Services.AddSingleton<Ai.AiLyricsVerifier>();  // 도구 B
builder.Services.AddSingleton<Ai.AiLyricsLayers>();    // 도구 C
builder.Services.AddSingleton<NamuLyricsSource>();     // 도구 C 의 1순위 소스
builder.Services.AddSingleton<LyricsLayersService>();  // 도구 C 의 조립자

builder.Services.AddHttpClient(Ai.AiClient.HttpClientName,     c => c.Timeout = 12초); // 판정용
builder.Services.AddHttpClient(Ai.AiClient.LongHttpClientName, c => c.Timeout = 60초); // 생성용
builder.Services.AddSingleton<Ai.AiClient>();
```

### 설계 결정 3가지

**① 조건부 등록을 하지 않는다.**
API 키가 없어도 등록은 한다. 조건부로 등록하면 `LyricsService` 의 생성자 파라미터를 못 채워서
**서버 부팅 자체가 죽는다.** 대신 `AiClient.Configured` 가 `false` 가 되고, 모든 도구가
"아무것도 안 함"으로 동작한다. **기능 토글이 아니라 타입 시스템으로 폴백을 보장한 것이다.**

**② HTTP 클라이언트를 두 개로 나눴다.**

| 클라이언트 | 타임아웃 | 용도 |
|---|---|---|
| `ai` | 12초 | 도구 A·B — 짧은 JSON 판정 |
| `ai-long` | 60초 | 도구 C — 15줄짜리 한국어 문장 생성 |

하나로 두면 C 가 **조용히 타임아웃으로 실패한다.** 특히 상위 모델을 지정했을 때 재현된다.
반대로 전부 60초로 두면 A·B 가 실패할 때 재생 화면이 그만큼 멈춘다.

**③ 전부 싱글턴 + `IHttpClientFactory`.**
싱글턴이 typed `HttpClient` 를 붙잡으면 내부 핸들러가 영원히 회전(rotate)하지 않아 DNS 변경을
못 따라간다. 그래서 **매 호출마다 팩터리에서 받는다** (반환된 `HttpClient` 는 핸들러를 소유하지
않으므로 `Dispose` 하지 않는 게 관례다).

---

## 2. 도구 선택 로직 — `FetchExternalAsync`

→ [`src/CsiMusic.Web/Services/LyricsService.cs`](../src/CsiMusic.Web/Services/LyricsService.cs) **116줄**

```csharp
private async Task<(string? Synced, string? Plain, string Source)> FetchExternalAsync(
    string videoId, string title, string? uploader, int duration, MusicMeta? music,
    CancellationToken ct)
{
    // (A) 곡당 한 번만 묻는다. 소스마다 부르면 같은 답에 API 를 여러 번 쓴다.
    var hint = await aiTitles.ParseAsync(title, uploader, duration, music, ct);

    // (B) 채택 게이트 — 곡당 검증 API 예산 2회
    const int VerifyApiBudget = 2;
    var verifyApiUsed = 0;
    async Task<bool> Ok(string source, string? track, string? artist, double? dur, string lyrics)
    {
        var (accept, _, calledApi) = await aiVerifier.AcceptAsync(
            title, uploader, duration,
            new AiLyricsVerifier.Candidate(source, track, artist, dur, lyrics),
            apiBudgetExhausted: verifyApiUsed >= VerifyApiBudget, ct);
        if (calledApi) verifyApiUsed++;
        return accept;
    }

    // ── 1단: Unison 싱크 ─────────────────────────────────────────
    var unison = await FetchUnisonAsync(videoId, title, uploader, duration, music, hint, ct);
    var unisonTrusted = unison?.ExactVideoId == true;   // videoId 정확매칭은 검증 생략
    if (unison?.Synced is { } us && (unisonTrusted || await Ok("unison", unison.Track, null, null, us)))
        return (us, null, "unison");

    // ── 2단: LRCLIB 싱크 ─────────────────────────────────────────
    var lrclib = await FetchLrclibAsync(title, uploader, duration, music, hint, ct);
    var lrclibRejected = false;
    if (Empty(lrclib?.SyncedLyrics) is { } ls)
    {
        if (await Ok("lrclib", lrclib!.TrackName, lrclib.ArtistName, lrclib.Duration, ls))
            return (ls, Empty(lrclib.PlainLyrics), "lrclib");
        lrclibRejected = true;   // 같은 항목의 plain 도 통째로 건너뛴다
    }

    // ── 3단: Unison 플레인 / 4단: LRCLIB 플레인 ────────────────────
    // ── 5단: 나무위키 — 검증 통과 없이는 절대 채택하지 않는다 ─────────

    return (null, null, "none");
}
```

### 선택 로직의 설계 결정 5가지

#### ① 힌트를 공유한다 — 도구 A 는 곡당 딱 1회

AI 제목 파싱을 소스마다 부르면 **똑같은 답에 API 를 여러 번 쓴다.**
한 번 부른 결과(`hint`)를 모든 소스가 인자로 받아 쓴다.

#### ② 검증에 예산을 둔다 — 곡당 2회

사다리 단마다 검증하면 한 곡에 최대 5번 호출이 쌓이고, 타임아웃이 겹치면
**가사 패널이 수십 초 멈춘다.** 무료 티어도 그만큼 빨리 탄다.

검증은 '2차 소견'이라 **위쪽 두 단(가장 유력한 후보)에만 걸어도 오탐의 대부분을 잡는다.**
예산이 떨어지면 규칙 기반 판정을 그대로 믿는다 — 즉 **AI 를 끈 것과 같은 안전한 동작으로 수렴**한다.

> 캐시 히트는 지연도 비용도 0 이라 **예산을 쓰지 않는다.** 그래서 이미 검증해 둔 곡은
> 예산과 무관하게 계속 검증된다. 이 판단은 반드시 **캐시 조회 뒤**에 해야 한다.

#### ③ 신뢰할 수 있는 소스는 검증을 건너뛴다

Unison 의 videoId 정확매칭은 **그 영상에 직접 달린 가사**라 "다른 곡" 오탐이 성립하지 않는다.
굳이 물어보면 API 만 쓴다. 메타데이터 폴백 매칭(`exact=false`)일 때만 검증한다.

#### ④ 거부는 실패가 아니라 다음 기회다

검증이 후보를 거부하면 그 자리에서 끝나는 게 아니라 **사다리의 다음 단으로 내려간다.**
즉 **검증은 실패를 만드는 게 아니라 더 나은 후보를 찾을 기회를 만든다.**

곁들여, 같은 LRCLIB 항목의 `synced` 가 '다른 곡'으로 거부되면 그 항목의 `plain` 도 같은 곡의
것이 아니다. 다시 물어보면 텍스트가 달라 **캐시가 안 먹고 API 만 한 번 더 쓴다** →
`lrclibRejected` 플래그로 통째로 건너뛴다.

#### ⑤ 최후 수단은 규칙이 다르다 — 나무위키

나무위키는 스크랩이라 **자체 메타(곡명·아티스트·길이)가 없다.** 다른 소스는 그 메타로 1차 거름이
되지만 나무위키는 AI 검증(도구 B)이 **유일한 관문**이다.

| | 다른 소스 | 나무위키 |
|---|---|---|
| 검증 결과 "판정 없음" | **통과** | **거부** |
| 예산 소진 / AI 꺼짐 | 통과 | 채택하지 않음 |

> 검증되지 않은 스크랩을 가사로 띄우느니 **없는 편이 낫다.**

---

## 3. 순서는 왜 이런가

**"싱크 우선, 그 다음 정확도"**

| 단 | 소스 | 고른 이유 |
|---|---|---|
| 1 | Unison 싱크 | videoId 정확매칭이라 가장 믿을 만하고 **단어 단위 타이밍**까지 온다 |
| 2 | LRCLIB 싱크 | 제목/아티스트 퍼지 매칭이지만 **커버리지가 넓다** |
| 3 | Unison 플레인 | 싱크가 없을 때. LRCLIB 플레인보다 **매칭이 정확하다** |
| 4 | LRCLIB 플레인 | |
| 5 | 나무위키 | 보컬로이드·합성엔진 곡은 위 전부에 없는데 여기엔 실려 있는 경우가 흔하다 |

싱크(줄별 시각)가 있는 가사가 플레인보다 항상 낫기 때문에, **같은 소스라도 싱크를 먼저 시도하고
플레인은 뒤로 미룬다.** 그래서 Unison→LRCLIB→Unison→LRCLIB 로 번갈아 나온다.

---

## 4. 도구 상세

### 도구 A — AI 제목 파싱

| 항목 | 내용 |
|---|---|
| **입력** | 제목 · 채널 · 길이 · 유튜브 Content ID 음악메타 |
| **출력** | `{ track, artist, isCover, confidence, metaLooksWrong }` |
| **통합 방식** | **대체가 아니라 보강** — AI 결과를 후보 목록 *앞*에 끼워 넣고 규칙 기반 후보는 뒤에 그대로 남긴다 |
| **버림 기준** | `confidence < 0.5` |

**프롬프트의 핵심 설계** — 모델에게 "모른다"고 말할 길을 열어 준 것:

```
- This is a lookup key, not a guess: if you are not reasonably sure of a field,
  use null rather than inventing something. A null field is handled fine downstream.
- confidence is your honest probability (0..1) that BOTH non-null fields are correct.
  Use below 0.5 when you are unsure — that makes the result be discarded.
```

**부가 기능 `metaLooksWrong`**: 유튜브 Content ID 가 **엉뚱한 곡을 붙인 영상**을 잡아낸다.
확신할 때만 `true` 고, 그때도 메타를 버리지 않고 AI 답 **바로 뒤**에 남긴다 — AI 가 틀렸을 때의 길이다.

> 낮은 신뢰 결과도 **캐시한다.** 다시 물어도 같은 답이라 API 만 낭비된다.

### 도구 B — AI 가사 검증

| 항목 | 내용 |
|---|---|
| **입력** | 곡 정보 + 후보 가사의 **앞 24줄 / 1200자** |
| **출력** | `{ match, confidence, reason }` |
| **거부 기준** | `match == false` **그리고** `confidence >= 0.75` |

**판정이 비대칭이다.** 확신 있는 부정일 때만 거부하고, 애매하면 통과시킨다.

> 규칙 게이트를 이미 통과한 후보라 사전 확률이 높고, 잘못 거부하면 멀쩡한 가사가 사라진다.
> **오탐 하나를 막으려다 여러 곡의 가사를 잃는 건 손해다.** 그래서 이 도구는 '2차 소견'이지
> 1차 판정자가 아니다.

**비용 절감 두 가지**
- 전문이 아니라 **앞 24줄만** 보낸다 — 곡 식별은 후렴 전에 끝난다. 전문을 보내면 곡당 토큰이
  수천으로 뛰어 무료 티어를 금방 태운다.
- LRC 타임태그(`[mm:ss.xx]`, `<mm:ss.xx>`)를 떼서 같은 곡의 synced/plain 이 **같은 캐시 키로
  모이게** 한다 → 호출이 절반.

**프롬프트가 명시하는 "잡아야 할 것"과 "거부하면 안 되는 것"**

| 잡아야 할 실패 | 거부하면 안 되는 것 |
|---|---|
| 제목만 같은 **다른 곡** | 발췌가 잘린 것 (앞부분만 보내니까) |
| 같은 아티스트의 다른 곡 · 같은 앨범 인접 트랙 | 문장부호·줄바꿈·로마자 표기 차이 |
| 번안·재작성본 | 라이브·어쿠스틱·리믹스 버전 |
| 곡이 그럴 리 없는 언어 | 메타가 그냥 불완전한 것 |
| 가사가 아예 아닌 것 (설명·크레딧) | **그냥 그 곡을 모르는 것** |

> 마지막 줄이 중요하다: **"모른다"는 불일치의 증거가 아니다.**

### 도구 C — 발음·번역

| 항목 | 내용 |
|---|---|
| **입력** | 가사 줄 배열 (15줄씩 청크 + 앞뒤 2줄 문맥) |
| **출력** | 줄마다 `{ text, pronunciation, translation }` |
| **트리거** | 사람이 버튼을 눌렀을 때만 (`POST /api/lyrics/{vid}/layers`) |

**A·B 와 성격이 다르다.** 저 둘은 짧은 *판정*이라 실패하면 규칙 기반으로 폴백하면 되지만,
이건 실제로 *글을 짓는* 작업이라 **폴백이 없다.** 만들거나 안 만들거나 둘 중 하나다.
그래서 "대충 맞는 값"을 저장하느니 **버리는 쪽**을 택한다.

**발음과 번역을 따로 부른다.** 한 번에 시키면 모델이 **양쪽 다 대충 한다.**
호출이 2배가 되지만 사람이 버튼을 눌렀을 때만 도는 기능이라 감당 가능하다.

**1순위는 사람이다.** 나무위키에서 사람이 쓴 발음·번역을 **먼저 베껴 오고**, 못 찾은 자리만
AI 가 생성한다. 베껴 오는 건 생성보다 안전하다 — 정답이 입력 안에 있으니 지어낼 이유가 없고,
결과가 원문과 맞는지 기계로 대조할 수 있다.

**음차 규칙 — 목적이 규칙을 결정한다**

이 줄의 쓰임은 **따라 부르기**다. 그래서 표준 외래어 표기법을 따르지 않는다.

| 규칙 | 이유 |
|---|---|
| 조사는 소리대로 (は→와, へ→에, を→오) | 부를 때 나는 소리 |
| 작은 っ · ん 은 받침으로 | 〃 |
| **무성음은 무성음 그대로** (か→카, 가 아님) | 노래 가이드지 표기법이 아니다 |
| **장음은 늘려 쓴다** | **한 글자가 한 박**이라 줄이면 박이 사라진다. 뜻은 아래 번역 줄이 책임진다 |
| 간주(♪ ～ ー)는 빈 문자열 | **절대 가사를 지어내지 않는다** |

> 장음 규칙은 [`AiLyricsLayers.cs`](../src/CsiMusic.Web/Services/Ai/AiLyricsLayers.cs) 의
> `LongVowelRule` **한 줄만 고치고 `PromptVersion` 을 올리면** 반대 방침으로 바꿀 수 있다.
> (관례대로 줄여 쓰려면 그 상수의 주석에 적힌 대체 문구로 교체)

### 나무위키 문서 찾기 — 제목으로 고르지 않는다

나무위키는 문서 제목이 한국어라 **제목 유사도로는 맞는 문서인지 알 수 없다.**
동명이곡·번안곡·리듬게임 수록 문서가 검색 결과에 섞여 나온다.

그래서 후보 문서를 **실제로 열어 우리 가사 줄이 그 안에 몇 % 있는지 세어** 고른다.
**50% 이상**이면 그 곡의 문서로 인정한다. 검색 결과 6개까지 열어 본다.

---

## 5. AI 를 쓰지 않은 곳 — 그것도 설계다

| 판단 | 방법 | AI 를 안 쓴 이유 |
|---|---|---|
| 가사가 **일본어인가** | 유니코드 범위 카운트 | 확실히 알 수 있는 걸 물으면 **돈·시간이 들고 답이 흔들린다** |
| 가사 **언어가 곡과 맞는가** | 문자 체계 비교 | 〃 |
| 제목에서 장식 떼기 | 정규식 15개 | 대부분의 제목은 규칙으로 충분하다 |
| LRC 타임태그 파싱 | 정규식 | 형식이 고정되어 있다 |

> **"AI 를 어디에 안 쓸지 정하는 게 절반이었다."**

---

## 6. 실행 시 로그 — 도구 선택 과정이 그대로 찍힌다

가사가 없는 곡을 재생하면 서버 로그에 선택 과정이 순서대로 남는다.

| 로그 문구 | 의미 |
|---|---|
| `AI 제목 파싱: … → … / … (cover=, conf=)` | 도구 A 가 원곡을 특정했다 |
| `AI 가사 거부 [소스] … — 사유 (conf=)` | 도구 B 가 거부 → 다음 단으로 내려간다 |
| `나무위키 가사 채택: 문서명 — N줄` | 5단까지 내려가 채택 |
| `나무위키 가사 거부: 문서명 — 사유` | 검증 실패 → 가사 없음 |
| `AI 가사 {층} 결과를 버린다 — 사유` | 도구 C 의 기계 검증 불합격 |
| `AI 호출을 300초 동안 중단한다(연속 실패 N회)` | 회로 차단기 작동 |
| `lyrics {vid}: synced (source=…)` | 최종 결과 |
