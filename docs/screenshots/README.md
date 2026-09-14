# 실행 화면 캡처

제출물 ① 의 "실행 화면 캡처" 가 들어가는 자리.

## 넣을 것 (권장 8장)

| 파일명 | 무엇을 | 어디서 |
|---|---|---|
| `01-lyrics-layers.png` | **가사 재생 화면** — 일본어 원문 + 한글 발음 + 한국어 번역 3줄 | 플레이어 |
| `02-build-layers.png` | 발음·번역 만들기 결과 (`filled`, `apiCalls`) | 가사 상세 |
| `03-agent-status.png` | **에이전트 상태** — 모델·오늘 호출수·상한·회로 | `GET /api/admin/ai` |
| `04-tool-selection-log.png` | **도구 선택 과정 로그** ← 가장 중요 | `docker compose logs -f` |
| `05-batch-progress.png` | 가사 일괄 찾기 진행률 | 관리자 → 가사 탭 |
| `06-git-log.png` | 커밋 이력 | `git log --oneline` |
| `07-deploy.png` | 라즈베리파이 배포 상태 | `docker compose ps` + `curl /healthz` |
| `08-fallback.png` | **API 키를 망가뜨려도 가사가 나오는 화면** | — |

## 04 번이 가장 중요하다

`docker compose logs -f` 를 띄워 놓고 가사가 없는 곡을 재생하면, 에이전트가 도구를 골라 가는
과정이 순서대로 찍힌다. **이 화면 한 장이 "도구 등록·선택 로직"의 실행 증거다.**

```bash
docker compose logs -f | grep -E "AI 제목 파싱|AI 가사 거부|나무위키|lyrics "
```

## 08 번은 완성도의 증거다

`.env` 의 `GOOGLE_AI_API_KEY` 를 비우거나 잘못된 값으로 바꾸고 재생해 본다.
**AI 가 죽어도 규칙 기반으로 가사가 정상적으로 나온다.**

> 캡처 설명: "API 키를 고의로 망가뜨려도 서비스는 살아 있다 — AI 는 필수 경로가 아니다"

---

**캡처마다 한 줄 설명을 붙일 것.** 설명 없는 스크린샷은 채점자가 무엇을 보는지 모른다.
