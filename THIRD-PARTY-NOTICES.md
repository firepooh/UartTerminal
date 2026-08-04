# 서드파티 고지 (Third-Party Notices)

UartTerminal 배포본에는 아래 서드파티 구성요소가 **별도 실행 파일**로 포함될 수 있습니다.
UartTerminal 자체 코드와는 프로세스 경계로 분리되어 있으며, 각 구성요소의 라이선스는 그대로 적용됩니다.

## esptool (Espressif Systems)

- **포함 위치**: `tools\esptool\esptool.exe`
- **용도**: [보드] > 펌웨어 플래시 기능에서 ESP32 계열 SoC 에 펌웨어를 기록할 때 별도 프로세스로 실행됩니다.
  ESP-IDF 가 설치된 PC 에서는 설치본의 esptool 을 대신 사용하므로 이 파일이 없어도 됩니다.
- **라이선스**: GNU General Public License v2.0 이상 (GPL-2.0-or-later)
- **소스 코드**: <https://github.com/espressif/esptool>
- **배포된 바이너리 원본**: <https://github.com/espressif/esptool/releases>
  (릴리스 자산 `esptool-<버전>-windows-amd64.zip` 에서 `esptool.exe` 만 추출해 넣습니다)

GPL-2.0 전문: <https://www.gnu.org/licenses/old-licenses/gpl-2.0.html>

esptool 을 포함하지 않은 배포본을 원하면 `tools\esptool\` 폴더를 삭제하면 됩니다.
그 경우 플래시 기능은 ESP-IDF 설치본이나 사용자가 지정한 esptool 경로(`state.json` 의 `esptoolPath`)를 사용합니다.

## ModelContextProtocol (.NET SDK)

- **용도**: 내장 MCP 서버(AI 연동)
- **라이선스**: MIT
- **소스 코드**: <https://github.com/modelcontextprotocol/csharp-sdk>
