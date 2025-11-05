# ChatMouse 빠른 시작 가이드

## 1. 빌드하기

### Windows (PowerShell 권장)

```powershell
# 1. 빌드 스크립트 실행
.\build-standalone.ps1

# 2. 또는 간단한 Batch 파일
.\build-release.cmd
```

### Linux/Mac (WSL)

```bash
chmod +x build-standalone.sh
./build-standalone.sh
```

### 수동 빌드

```bash
dotnet publish ChatMouse.csproj -c Release -r win-x64 \
    -p:PublishSingleFile=true \
    -p:SelfContained=true \
    -p:IncludeNativeLibrariesForSelfContained=true \
    -p:IncludeAllContentForSelfExtract=true \
    -o publish
```

## 2. 실행하기

```powershell
# publish 디렉토리의 실행 파일 실행
.\publish\ChatMouse.exe
```

첫 실행 시 `config.json` 파일이 자동으로 생성됩니다.

## 3. 설정하기

`config.json` 파일을 편집하여 LLM API 엔드포인트를 설정합니다:

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

### 주요 설정 항목

| 항목 | 설명 | 예시 |
|------|------|------|
| `base_url` | LLM API 엔드포인트 (OpenAI 호환) | `http://localhost:8000/v1` |
| `api_key` | API 키 (필요시) | `sk-...` 또는 `EMPTY` |
| `model` | 모델 이름 | `gpt-3.5-turbo`, `llama-2` |
| `prompt` | 시스템 프롬프트 | `다음 텍스트를 요약해줘:` |
| `hotkey` | 전역 핫키 | `Ctrl+Shift+Space` |
| `tray_mode` | 트레이 상주 모드 | `true` / `false` |

## 4. 사용하기

### 기본 사용법

1. **애플리케이션 실행**
   - `ChatMouse.exe` 실행
   - 시스템 트레이에 아이콘 표시

2. **텍스트 선택**
   - 원하는 텍스트를 마우스로 드래그하여 선택

3. **핫키 입력**
   - 기본: `Ctrl+Shift+Space`
   - 설정에서 변경 가능

4. **결과 확인**
   - 마우스 커서 근처에 툴팁 표시
   - LLM 응답 내용 확인

5. **텍스트 복사**
   - 툴팁 더블클릭: 전체 복사
   - 텍스트 선택 후 Ctrl+C: 일부 복사
   - 우클릭 메뉴: Copy, Select All

### 트레이 메뉴

- **Show (핫키)**: 즉시 실행
- **Exit**: 프로그램 종료

## 5. 트러블슈팅

### 빌드 오류

**오류**: `dotnet` 명령을 찾을 수 없음
- **해결**: .NET 8.0 SDK 설치
  - 다운로드: https://dotnet.microsoft.com/download/dotnet/8.0

**오류**: `icon.ico` 파일을 찾을 수 없음
- **해결**: 정상입니다. 프로젝트 파일에서 아이콘이 주석처리되어 있습니다.
- 아이콘을 사용하려면: `icon.ico` 파일을 프로젝트 루트에 추가

### 실행 오류

**오류**: 이미 실행 중입니다
- **해결**: 이미 프로세스가 실행 중입니다. 작업 관리자에서 종료 후 재실행

**오류**: 핫키 등록 실패
- **해결**: 다른 프로그램이 동일한 핫키를 사용 중일 수 있습니다.
  - `config.json`에서 다른 핫키로 변경
  - 트레이 메뉴로 수동 실행

**오류**: LLM API 연결 실패
- **해결**: 
  - `config.json`의 `base_url`이 올바른지 확인
  - LLM 서버가 실행 중인지 확인
  - 방화벽/프록시 설정 확인

### 텍스트 추출 안됨

**문제**: 선택된 텍스트를 인식하지 못함
- **해결**:
  1. FlaUI 호환 애플리케이션인지 확인
  2. `allow_clipboard_probe`를 `true`로 설정
  3. 텍스트 선택 후 수동으로 Ctrl+C 눌러보기

## 6. 고급 설정

### 프록시 사용

```json
{
  "http_proxy": "http://proxy.example.com:8080"
}
```

### SSL 인증서 검증 비활성화 (개발용)

```json
{
  "disable_ssl_verify": true
}
```

**⚠️ 주의**: 프로덕션 환경에서는 사용하지 마세요.

### 핫키 조합 예시

```json
// Ctrl + Shift + Space (기본)
"hotkey": "Ctrl+Shift+Space"

// Alt + Q
"hotkey": "Alt+Q"

// Ctrl + Alt + C
"hotkey": "Ctrl+Alt+C"

// Win + S
"hotkey": "Win+S"
```

## 7. 로그 확인

애플리케이션 디렉토리의 `ChatMouse.log` 파일을 확인하세요:

```
2025-11-05 09:51:55.123 [INFO] ===== App Start =====
2025-11-05 09:51:55.456 [INFO] Config loaded. tray_mode=True
2025-11-05 09:51:56.789 [INFO] Hotkey 'Ctrl+Shift+Space' registered
```

## 8. 배포하기

### 단일 파일 배포

`publish/ChatMouse.exe` 파일만 배포하면 됩니다.
- 크기: 약 73MB
- .NET 런타임 포함
- 별도 설치 불필요

### 추가 파일 (선택)

- `config.json`: 사전 설정 배포 시
- `icon.ico`: 커스텀 트레이 아이콘 사용 시

## 9. 지원

- 로그 파일: `ChatMouse.log`
- 프로젝트 구조: `PROJECT_STRUCTURE.md`
- 상세 문서: `README.md`

## 10. 라이센스

프로젝트 라이센스를 명시해주세요.

