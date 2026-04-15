# Claude Code AskUserQuestion UI 트리 구조 (실측)

**측정일**: 2026-04-13
**환경**: Claude Code Desktop (Electron/Chromium), Windows 10

---

## 1. 전체 트리 경로

```
[Group] AID="main-content"
  └─ [Group] Class="relative h-full"
      └─ [Group] Class="flex flex-col h-full flex-1 bg-bg-000..."
          ├─ [Group] Class="sticky top-0 z-20"          ← 세션 헤더
          ├─ [Group] AID="cli-button-container"          ← 대화 스크롤 영역
          └─ [Group] Class="absolute bottom-0 left-0 right-0 pointer-events-none z-header"
              └─ [Group] Class="flex justify-center relative"
                  └─ [Group] Class="pointer-events-auto relative z-10 flex flex-col gap-3 mt-0"
                      └─ ★ [Group] Name="질문+선택지 전체"   ← AskUserQuestion 컨테이너
                          ├─ [Text] Name="질문 텍스트"
                          ├─ [Button] Name="선택지1"
                          ├─ [Button] Name="선택지2"
                          ├─ [Button] Name="선택지3"
                          ├─ [Button] Name="Type something else... N"
                          └─ [Button] Name="Skip"
```

## 2. AskUserQuestion 컨테이너 (부모 Group)

| 속성 | 값 |
|---|---|
| ControlType | Group |
| Name | 질문 + 모든 선택지가 연결된 전체 텍스트 (예: `"어떤 동물이 좋으세요? 소 음메~ 1 쥐 찍찍~ 2 닭 꼬끼오~ 3 Type something else... 4 Skip"`) |
| AutomationId | (없음) |
| ClassName | `"rounded-xl outline-none border border-border-300 border-0.5 p-4 my-4 bg-bg-000"` |
| BoundingRect | 예: [1141,523,736x364] |

### 식별 특징
- **Name이 비어있지 않음** (질문+선택지 전체가 Name에 포함)
- **Name에 "Skip"이 항상 포함됨** (마지막에 Skip 버튼이 있으므로)
- **ClassName에 `"rounded-xl"` 포함**
- **직계 자식으로 Button이 3개 이상** (선택지 + "Type something else..." + "Skip")

## 3. 선택 버튼 (Choice Buttons)

| 속성 | 값 |
|---|---|
| ControlType | **Button** |
| Name | `"소 음메~ 1"`, `"쥐 찍찍~ 2"`, `"닭 꼬끼오~ 3"` |
| ClassName | `"flex w-full items-center justify-between p-3 text-left transition-colors ..."` |
| BoundingRect | Width=700, Height=64 |
| 자식 | Text 요소들 (선택지 이름, 설명, 번호) |

## 4. "Type something else..." 항목

| 속성 | 값 |
|---|---|
| ControlType | **Button** |
| Name | `"Type something else... 4"` |
| Width | 700, Height=49 |

## 5. "Skip" 버튼

| 속성 | 값 |
|---|---|
| ControlType | **Button** |
| Name | `"Skip"` |
| ClassName | `"relative overflow-hidden rounded-lg border border-border-300 bg-bg-100 px-4 py-2 text-sm text-text-200 hover:bg-bg-200"` |
| BoundingRect | Width=63, Height=38 (다른 선택 버튼보다 훨씬 작음) |

## 6. "Used a tool", "AskUserQuestion" 텍스트

| 항목 | ControlType | 위치 |
|---|---|---|
| `"Used a tool"` | **Button** (Width=704) | 대화 영역 내, `cli-button-container` 하위 (활동 토글) |
| `"AskUserQuestion"` | **Text** | 대화 영역 내, 도구 상태 라벨 |
| `"Awaiting input..."` | **Text** | 대화 영역 내 |

## 7. turn-form 상태

AskUserQuestion 활성 시:
- **turn-form 요소 자체가 존재하지 않음** (FindByAutomationIdRaw 결과: null)
- 선택 컨테이너가 turn-form 자리를 대체

## 8. 감지 전략 (권장)

```
1. turn-form이 null이면 AskUserQuestion 가능성 있음
2. main-content 하위에서 ClassName에 "rounded-xl"을 포함하고 
   Name에 "Skip"이 포함된 Group 요소를 찾음
3. 해당 Group의 직계 자식 Button들을 수집
4. "Skip"과 "Type something else..."를 제외한 나머지가 선택 버튼
```
