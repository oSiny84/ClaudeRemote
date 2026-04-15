# Phase 6 Hotfix 4 작업 보고서 - 전송 Snackbar 제거

**작성일**: 2026-04-13  
**담당**: Android Developer Agent  
**프로젝트**: ClaudeRemote Android  
**유형**: UX 개선

---

## 변경 내용

`MainViewModel.kt`에서 명령 전송 시 표시되던 Snackbar 메시지 제거:

| 함수 | 제거된 호출 |
|------|------------|
| `sendInput()` | `_snackbarEvent.tryEmit("Sent!")` |
| `sendQuickInput()` | `_snackbarEvent.tryEmit("Sent: $text")` |

**유지**: `_statusMessage` 업데이트 (하단 상태 텍스트), 에러 발생 시 Snackbar (서버 응답 실패 등)
