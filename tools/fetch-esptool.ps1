# esptool standalone 실행 파일을 받아 tools\esptool\esptool.exe 로 둔다.
#
# 왜 필요한가: ESP-IDF 가 설치되지 않은 PC 에서도 [보드 > 펌웨어 플래시] 가 동작해야 한다.
# 앱은 tools\esptool\esptool.exe 를 (사용자 지정 경로 다음으로) 가장 먼저 찾는다.
#
# 이 바이너리는 저장소에 커밋하지 않는다(수십 MB). 릴리스 빌드(.github/workflows/build.yml)가
# 같은 방식으로 받아 배포 zip 에 넣고, 개발 PC 에서는 필요할 때 이 스크립트를 직접 실행한다.
#
# esptool 은 GPL-2.0+ 이다 — 배포 시 출처/소스 위치를 함께 알려야 한다(THIRD-PARTY-NOTICES.md).
[CmdletBinding()]
param(
  # 고정 버전(재현 가능한 빌드). 앱은 v4/v5 문법 차이를 스스로 판별하므로 둘 다 동작한다.
  [string]$Version = 'v5.3.1',
  [string]$Destination = (Join-Path $PSScriptRoot 'esptool'),
  [switch]$Force
)

$ErrorActionPreference = 'Stop'

$exePath = Join-Path $Destination 'esptool.exe'
if ((Test-Path $exePath) -and -not $Force) {
  Write-Host "이미 있습니다: $exePath  (다시 받으려면 -Force)"
  exit 0
}

$asset = "esptool-$Version-windows-amd64.zip"
$url = "https://github.com/espressif/esptool/releases/download/$Version/$asset"
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("esptool-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null

try {
  Write-Host "내려받기: $url"
  $zip = Join-Path $work $asset
  Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
  Write-Host ("  받음: {0:N1} MB" -f ((Get-Item $zip).Length / 1MB))

  Expand-Archive -Path $zip -DestinationPath (Join-Path $work 'x') -Force

  # zip 내부 구조는 버전에 따라 다를 수 있어(폴더 유무) 재귀로 찾는다.
  # 필요한 것은 esptool.exe 하나다 — espefuse/espsecure 등은 쓰지 않으므로 넣지 않는다.
  $found = Get-ChildItem -Path (Join-Path $work 'x') -Recurse -Filter 'esptool.exe' | Select-Object -First 1
  if (-not $found) { throw "zip 안에서 esptool.exe 를 찾지 못했습니다: $asset" }

  New-Item -ItemType Directory -Path $Destination -Force | Out-Null
  Copy-Item $found.FullName $exePath -Force
  Write-Host ("설치: {0}  ({1:N1} MB)" -f $exePath, ($found.Length / 1MB))

  # 실제로 실행되는지 확인(PyInstaller 번들이라 첫 실행이 느릴 수 있다).
  & $exePath version
}
finally {
  Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
