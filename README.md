# ChatMouse

.NET 8.0 기반 Windows Forms 애플리케이션으로, UI 요소에서 선택된 텍스트를 자동으로 추출하여 LLM에 전송하는 도구입니다.

## 주요 기능

- **전역 핫키**: 설정된 핫키(기본값: Ctrl+Shift+Space)로 어디서나 호출 가능
- **텍스트 자동 추출**: FlaUI를 사용한 UI Automation으로 선택된 텍스트 추출
- **시스템 트레이 상주**: 백그라운드에서 실행되며 필요할 때만 활성화
- **Single Instance**: 중복 실행 방지 및 IPC 통신
- **인터랙티브 툴팁**: 복사 가능한 반투명 툴팁으로 결과 표시

## 시스템 요구사항

- Windows 10/11
- .NET 8.0 Runtime

## 빌드 방법

### 일반 빌드

```bash
# 복원 및 빌드
dotnet restore
dotnet build

# Release 빌드
dotnet build -c Release

# 실행
dotnet run
```

### Standalone 단일 파일 빌드

```powershell
# PowerShell (권장)
.\build-standalone.ps1

# 또는 직접 dotnet 명령어 사용
dotnet publish ChatMouse.csproj -c Release -r win-x64 `
    -p:PublishSingleFile=true `
    -p:SelfContained=true `
    -p:IncludeNativeLibrariesForSelfContained=true `
    -p:IncludeAllContentForSelfExtract=true `
    -o publish
```

단일 실행 파일은 `publish/ChatMouse.exe` (약 73MB)에 생성되며, .NET 런타임을 포함하여 별도의 설치 없이 실행 가능합니다.

## 설정

첫 실행 시 `config.json` 파일이 자동으로 생성됩니다.

```json
{
  "base_url": "http://127.0.0.1:8000/v1",
  "api_key": "EMPTY",
  "model": "my-vllm-model",
  "prompt": "다음 텍스트를 요약해줘:",
  "allow_clipboard_probe": true,
  "http_proxy": null,
  "disable_ssl_verify": false,
  "tray_mode": true,
  "hotkey": "Ctrl+Shift+Space"
}
```

### 설정 항목

- `base_url`: LLM API 엔드포인트 (OpenAI 호환 형식)
- `api_key`: API 키 (필요시)
- `model`: 사용할 모델 이름
- `prompt`: 시스템 프롬프트
- `allow_clipboard_probe`: 클립보드 접근 허용 여부
- `http_proxy`: 프록시 서버 주소 (선택사항)
- `disable_ssl_verify`: SSL 인증서 검증 비활성화 (개발용)
- `tray_mode`: 트레이 모드로 실행 (false시 1회 실행 후 종료)
- `hotkey`: 전역 핫키 설정

## 사용 방법

1. 애플리케이션 실행 (트레이 아이콘 표시)
2. 텍스트를 선택
3. 설정된 핫키 입력 (기본: Ctrl+Shift+Space)
4. 툴팁에 LLM 응답 표시
5. 툴팁 더블클릭으로 전체 복사

## 아키텍처

- **Models**: 데이터 전송 객체 (ChatMessage, ChatRequest, ChatResponse)
- **UI Components**: PrettyTooltipForm (커스텀 툴팁 UI)
- **Services**: 
  - Logger (파일 로깅)
  - IPC 통신 (WM_COPYDATA)
  - HTTP 통신 (LLM API)
- **Controllers**: 
  - TrayContext (트레이 아이콘 관리)
  - HotkeyWindow (전역 핫키 처리)

## 라이센스

프로젝트 라이센스를 명시해주세요.


