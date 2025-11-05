# ChatMouse 프로젝트 구조

## 디렉토리 구조

```
chatmouse/
├── Program.cs                  # 메인 애플리케이션 코드
├── ChatMouse.csproj           # 프로젝트 파일 (.NET 8.0)
├── ChatMouse.sln              # 솔루션 파일
├── NuGet.config               # NuGet 패키지 소스 설정
├── .gitignore                 # Git 제외 파일 목록
├── README.md                  # 프로젝트 설명 및 사용법
├── PROJECT_STRUCTURE.md       # 프로젝트 구조 문서 (이 파일)
├── build-standalone.ps1       # PowerShell 빌드 스크립트
├── build-standalone.sh        # Bash 빌드 스크립트
├── build-release.cmd          # Batch 빌드 스크립트
├── icon.ico (선택)            # 애플리케이션 아이콘
├── config.json (런타임 생성)  # 설정 파일
└── ChatMouse.log (런타임 생성) # 로그 파일
```

## 코드 구조 (Program.cs)

### 1. Models
- `ChatMessage`: 채팅 메시지 구조
- `ChatRequest`: LLM API 요청 구조
- `ChatResponse`: LLM API 응답 구조

### 2. UI Constants
- `Ui`: UI 스타일 상수 (색상, 폰트, 크기 등)

### 3. Logger
- `Logger`: 파일 로깅 시스템
  - 초기화, Info, Warn, Error 레벨 지원
  - 스레드 안전 로깅

### 4. Tooltip Form
- `PrettyTooltipForm`: 인터랙티브 툴팁 UI
  - 반투명 그라데이션 배경
  - 애니메이션 (Fade In/Out)
  - RichTextBox를 통한 텍스트 선택/복사 기능
  - 컨텍스트 메뉴 (Copy, Select All)
  - 더블클릭으로 전체 복사

### 5. Config
- `AppConfig`: 애플리케이션 설정 구조
  - LLM API 엔드포인트
  - 프록시 설정
  - 핫키 설정
  - 트레이 모드 설정

### 6. Program (Main Entry)
- `App`: 메인 애플리케이션 클래스
  - **Single Instance**: Mutex를 통한 중복 실행 방지
  - **IPC**: WM_COPYDATA를 통한 프로세스 간 통신
  - **Tray Mode**: 시스템 트레이 상주 모드
  - **Hotkey**: 전역 핫키 등록 및 처리

### 7. IPC
- `IpcWindow`: WM_COPYDATA 메시지 처리
- `NotifyExistingInstance`: 기존 인스턴스에 메시지 전송

### 8. Tray + Hotkey
- `TrayContext`: 시스템 트레이 아이콘 관리
- `HotkeyWindow`: 전역 핫키 처리 (Win32 API)

### 9. Trigger Logic
- `TriggerOnceAsync`: 메인 동작 로직
  - 텍스트 선택 감지
  - LLM API 호출
  - 툴팁 표시

### 10. HTTP / Config
- `CreateHttp`: HttpClient 생성 (프록시, SSL 설정)
- `LoadConfig`: 설정 파일 로드/생성

### 11. LLM
- `QueryLLMAsync`: LLM API 호출 (OpenAI 호환 형식)

### 12. Context Capture
- `GetContextTextPreferSelectionAsync`: 텍스트 추출 (우선순위)
  1. FlaUI (UI Automation)
  2. Win32 Edit/RichEdit 컨트롤
  3. Ctrl+C 프로브 (클립보드 감지)
  4. 클립보드 폴백

### 13. STA Helpers
- `RunOnStaAsync`: STA 스레드에서 비동기 실행
- `RunOnStaBlocking`: STA 스레드에서 동기 실행

## 의존성

### NuGet 패키지
- `FlaUI.UIA3` (v4.0.0): UI Automation 라이브러리

### .NET 기능
- Windows Forms
- System.Drawing
- HttpClient
- System.Text.Json
- P/Invoke (Win32 API)

## 빌드 출력

### Debug 빌드
- 경로: `bin/Debug/net8.0-windows/win-x64/`
- 파일: 여러 DLL 및 실행 파일

### Release 빌드 (Standalone)
- 경로: `publish/`
- 파일: `ChatMouse.exe` (단일 실행 파일, 약 73MB)
- 특징:
  - Self-contained (.NET 런타임 포함)
  - 네이티브 라이브러리 내장
  - 압축 활성화
  - ReadyToRun 최적화

## 아키텍처 패턴

### SOLID 원칙 적용
- **Single Responsibility**: 각 클래스는 단일 책임 (Logger, Config, UI, IPC 등)
- **Open/Closed**: 확장 가능한 구조 (Config, LLM API)
- **Dependency Inversion**: HttpClient 주입 방식

### MVC 분리
- **Model**: ChatMessage, ChatRequest, ChatResponse, AppConfig
- **View**: PrettyTooltipForm, TrayIcon
- **Controller**: TrayContext, HotkeyWindow, App (Main Logic)

### 비동기 프로그래밍
- async/await 패턴 사용
- CancellationToken을 통한 취소 처리
- Task 기반 비동기 실행

## 보안 고려사항

- SSL 인증서 검증 (설정으로 비활성화 가능)
- API 키 보안 (config.json은 .gitignore에 포함)
- Single Instance로 리소스 보호

## 성능 최적화

- ReadyToRun 컴파일
- 단일 파일 압축
- 비동기 I/O
- 타임아웃 설정 (HttpClient, Context Capture)

