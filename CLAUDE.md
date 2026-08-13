# UartTerminal — 작업 규칙

배경과 설계 근거는 [README.md](README.md) 에 있다. 이 파일은 **매 수정마다 지켜야 하는 규칙**만 짧게 적는다.
규칙이 코드 주석에만 흩어져 있으면 수정할 때마다 조금씩 어긋난다 — 그래서 여기 모아 두고,
지킬 수 있는 것은 `tests/UartTerminal.Tests/ConventionTests.cs` 가 기계적으로 검사한다.

## UI (XAML)

- **FontSize 숫자 금지.** `Themes/Controls.xaml` 의 토큰만 쓴다 —
  `Font.Glyph`(10) · `Font.Caption`(11.5) · `Font.Body`(12.5) · `Font.Emph`(13) · `Font.Title`(21).
  새 크기가 필요하면 화면이 아니라 토큰을 늘릴지 먼저 판단한다.
- **색 리터럴 금지.** `#RRGGBB` 도, `White` 같은 이름 색도 안 된다. `Themes/Palette.Dark.xaml` 의
  키를 쓰고, 없으면 팔레트에 추가한다. 강조 배경 위 글자는 `OnAccent`.
- **팔레트 브러시는 `{DynamicResource}`**, 폰트·스타일·지오메트리 키는 `{StaticResource}`.
- **새 스타일은 화면이 아니라 `Themes/Controls.xaml` 에.** 라벨·머리글·안내문은 이미 있는
  시맨틱 스타일을 쓴다: `FieldLabel` · `TableHeaderText` · `HintText` · `CaptionText`.
- **테마는 다크 하나뿐이다.** 라이트 팔레트를 되살리지 않는다(제거 이유는 README §2.4).
- 표(ListBox + 머리글 Grid)는 열 폭이 **두 곳에 복제**돼 있다. 한쪽만 고치면 열이 어긋난다 —
  폭을 바꿀 때는 머리글 Grid 와 DataTemplate 을 함께 고친다.

## 문자열

- **사용자에게 보이는 문자열은 `Loc.cs` 표에만.** 코드·XAML 에 한국어를 직접 쓰지 않는다
  (번역하면 깨지는 데이터라면 그 줄에 `// loc:data`).
- **Core 는 문장을 만들지 않는다.** `LocMessage`(키 + 인자)만 돌려주고 문장은 UI 가 조립한다.
- 언어별 문자열을 `static readonly` 로 캐시하지 않는다 — 시작 시 언어로 굳는다. 프로퍼티로 매번 조회.

## 이름

- **회사 관련 바이너리/제품 이름을 코드·주석·예제·커밋 메시지·태그에 쓰지 않는다.**
  예제가 필요하면 중립적인 이름(`sensor`, `pulse simul`)을 쓴다.

## 검증

- 고쳤으면 `dotnet test` 를 돌린다. UI 변경은 테스트로 안 잡히므로 **실제로 앱을 띄워 확인**하고,
  확인하지 못했으면 그 사실을 보고에 적는다.
- 앱이 실행 중이면 Debug 빌드가 DLL 잠금으로 실패한다 — 빌드 실패를 성공으로 착각하지 말 것.
- 커밋 메시지는 한국어로, **무엇을 왜** 바꿨는지 적는다(증상 → 원인 → 조치).
