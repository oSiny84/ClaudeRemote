# Phase 3 작업 보고서 - Windows Claude 앱 연동

**작성일**: 2026-04-10
**담당**: Windows Developer Agent
**프로젝트**: ClaudeRemote Windows
**Phase**: 3 - Claude 앱 연동

---

## 1. 작업 요약

Phase 3의 핵심 목표인 **Claude 데스크톱 앱 UI 트리 분석 및 자동화 서비스 구체화**를 완료했습니다.
Electron/Chromium 기반 앱의 접근성 트리 특성을 파악하고, `TreeWalker.RawViewWalker` 기반의 요소 탐색 전략으로 전환하여 6개 통합 테스트를 모두 통과시켰습니다.

---

## 2. 변경/생성 파일 목록

| # | 파일 | 변경 유형 |
|---|------|-----------|
| 1 | `Services/ClaudeAutomationService.cs` | 전면 재작성 |
| 2 | `docs/ClaudeUI_Map.md` (신규) | UI 트리 분석 문서 |
| 3 | `ClaudeRemote.Tests/Phase3Test.cs` (신규) | 통합 테스트 |

---

## 3. Claude 데스크톱 앱 UI 분석 결과

### 3-1. 앱 기본 정보

| 항목 | 값 |
|------|------|
| 프로세스명 | `claude` (소문자) |
| 아키텍처 | Electron/Chromium 멀티프로세스 |
| 윈도우 클래스 | `Chrome_WidgetWin_1` |
| 메인 콘텐츠 | `Document` AID=`RootWebArea` Name=`Claude` |

### 3-2. 핵심 UI 요소

| 요소 | ControlType | 탐색 키 | 비고 |
|------|------------|---------|------|
| 모드 탭 | RadioButton | Name=`Chat`/`Cowork`/`Code` | 3개 모드 (Cowork 추가 확인) |
| 사이드바 | Group | Name=`Sidebar` | 세션/프로젝트 목록 포함 |
| 새 세션 | Hyperlink | Name=`New session` | 단축키 Ctrl+Shift+O |
| 메인 콘텐츠 | Group | AID=`main-content` | IsControlElement=**false** |
| 대화 영역 | Group | AID=`cli-button-container` | IsControlElement=**false** |
| 입력 폼 | Group | AID=`turn-form` | IsControlElement=**false** |
| 입력 필드 | Group | Name=`Enter your turn` | TextPattern, Focusable |
| 중단 버튼 | Button | Name=`Interrupt` | 스트리밍 중에만 표시 |

### 3-3. Chromium 접근성 트리의 핵심 발견

**문제**: 웹 콘텐츠 영역의 주요 요소(`main-content`, `cli-button-container`, `turn-form`)가 `IsControlElement=false`, `IsContentElement=false`로 설정되어 있어 **표준 `FindFirst/FindAll`로 찾을 수 없음**.

**원인**: Chromium이 렌더링한 웹 DOM 요소는 ControlView에 포함되지 않고 RawView에서만 접근 가능.

**해결**: `TreeWalker.RawViewWalker`를 사용한 깊이 우선 탐색으로 요소를 먼저 찾고, 찾은 요소의 `FindAll(Descendants, ...)` 호출은 정상 동작하므로 이를 활용하여 자식 텍스트 추출.

---

## 4. ClaudeAutomationService 상세 변경 내역

### 4-1. 요소 탐색 전략 변경

| 메서드 | 이전 | 수정 후 |
|--------|------|---------|
| 요소 찾기 | `FindFirst(Descendants, PropertyCondition)` | `FindByAutomationIdRaw()` (RawViewWalker DFS) |
| 캐싱 | 없음 | 캐시 + `IsCachedElementValid()` 검증 |
| 입력 필드 | `FindFirst(Edit 타입)` | Name=`Enter your turn` (Focusable Group) |
| 폴백 | 없음 | ControlView 먼저 시도 → RawView 폴백 |

### 4-2. 텍스트 입력 (SendInputAsync)

| 단계 | 방식 | 이유 |
|------|------|------|
| 1. 포커스 | `SetFocus()` | 입력 필드 활성화 |
| 2. 기존 텍스트 제거 | `Ctrl+A` (SendKeys) | contenteditable div이므로 ValuePattern 미지원 |
| 3. 텍스트 입력 | `Clipboard + Ctrl+V` | 한글(IME)/Unicode 안정 지원 |
| 4. 전송 | `Enter` (SendKeys) | - |

**설계 결정**: `ValuePattern`이 미지원되는 Electron contenteditable 요소이므로, 클립보드 기반 입력이 가장 안정적.

### 4-3. 모드 전환 (SwitchModeAsync)

탐색: `ControlType.RadioButton` + Name 매칭
시도 순서: `SelectionItemPattern` → `InvokePattern` → 좌표 클릭 (3단 폴백)

### 4-4. 세션 목록 (GetSessionsAsync)

- Sidebar 내 "Sessions" 헤더 이후의 Text 요소 수집
- 필터링: 날짜 헤더(Today/Yesterday), UI 라벨, 단축키 텍스트, 사용자 프로필 영역
- Settings 버튼의 BoundingRectangle 기반 자식 텍스트 제외

### 4-5. 모니터링 (MonitorLoop)

| 항목 | 이전 | 수정 후 |
|------|------|---------|
| 출력 비교 | 전체 문자열 비교 | SHA-256 해시 비교 |
| 스트리밍 감지 | 없음 | `Interrupt` 버튼 존재 여부로 감지 |
| 오류 복구 | 2초 대기 | 2초 대기 + 캐시 무효화 |

---

## 5. 통합 테스트 결과

### 실제 Claude 데스크톱 앱과의 라이브 테스트 (6/6 PASS)

| # | 테스트 | 검증 내용 | 결과 |
|---|--------|-----------|------|
| 1 | FindClaudeWindowAsync | 프로세스 탐지 + 윈도우 핸들 확보 + RootWebArea 위치 | PASS |
| 2 | GetOutputAsync | latest=2981자, full=8064자, summary=503자 추출 | PASS |
| 3 | GetSessionsAsync | 3개 세션 정확히 탐지 (프로필 텍스트 필터링 확인) | PASS |
| 4 | Mode detection | 현재 모드 "chat" 정상 반환 | PASS |
| 5 | Streaming detection | IsGenerating=true (테스트 실행 중 Claude가 응답 생성 중이었음) | PASS |
| 6 | Monitoring loop | 3초간 모니터링, 출력 변경 1회 감지 | PASS |

### 테스트 환경
- Claude 데스크톱 앱 실행 중 (코드 모드, D_Develop 프로젝트)
- 테스트 실행: `dotnet run --project ClaudeRemote.Tests -- phase3`

---

## 6. 미검증/미구현 항목

### SendInputAsync (입력 테스트)
- 자동화된 테스트에서 실제 입력은 실행하지 않음 (Claude 세션에 불필요한 메시지 생성 방지)
- 수동 테스트 필요: 한글 입력, 긴 텍스트, 특수문자
- **권장**: PM이 별도 테스트 세션에서 수동 검증

### SwitchModeAsync (모드 전환)
- RadioButton 감지는 확인됨, 실제 전환 동작은 수동 테스트 필요
- Cowork 모드가 새로 추가된 것 확인 (프로토콜에 없음 → 프로토콜 업데이트 필요 여부 PM 결정)

### SelectSessionAsync / AddSessionAsync
- 요소 탐색 로직은 검증됨, 실제 클릭/전환 동작은 수동 테스트 필요

### GetProjectsAsync
- Code 모드에서만 의미 있음 → Code 모드 전환 후 테스트 필요
- 현재 프로젝트 경로(`C:\D_Develop`) 감지 로직 포함

---

## 7. 기술적 주의사항

### Electron 앱 자동화의 한계

1. **AutomationId 동적 변경**: `radix-_r_xxx_` 형태의 ID는 앱 재시작/업데이트 시 변경될 수 있음. Name 기반 탐색을 우선 사용하는 이유.
2. **RawView 필수**: 웹 콘텐츠 영역의 핵심 요소가 ControlView에서 보이지 않아 RawViewWalker 사용. 성능 영향 있으나 캐싱으로 완화.
3. **클립보드 충돌**: SendInputAsync가 시스템 클립보드를 사용하므로 사용자 클립보드 내용이 덮어써짐. STA 스레드에서 별도 처리.
4. **좌표 기반 클릭**: SelectionItemPattern/InvokePattern 실패 시 BoundingRectangle 기반 클릭으로 폴백. 멀티모니터/DPI 스케일링 주의 필요.

---

## 8. 다음 Phase 준비사항

### Phase 4 (Android UI/UX) 관련
- Windows 서버 측은 Phase 3 완료 상태 → Android에서 연동 테스트 가능
- `Cowork` 모드 추가에 따른 프로토콜 변경 여부 PM 결정 필요

### Phase 5 (안정화) 관련
- Claude 앱 종료/재시작 감지: 프로세스 모니터링 타이머 구현 예정
- 대용량 출력 청크 분할: full 출력이 8KB+ 확인 → 10KB 초과 시 청크 분할 필요
- 하트비트: 아직 미구현
