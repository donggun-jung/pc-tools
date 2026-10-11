# pc-tools

여러 컴퓨터(Mac · Windows)에서 쓰는 작은 도구 모음. 받는 곳: **https://tools.jdg.dev**

| 플랫폼 | 도구 | 하는 일 |
|---|---|---|
| Windows | [MonitorSaver](windows/MonitorSaver/) | 단축키 한 번에 세션 잠금 + 모니터 끄기. 컴퓨터는 계속 돌아감 |
| Mac | — | 아직 없음 ([mac/](mac/)) |

## 구조

| 경로 | 내용 |
|---|---|
| `windows/<도구>/` | Windows 도구. 소스와 스크립트, 도구별 README |
| `mac/<도구>/` | Mac 도구 |
| `catalog.json` | 도구 목록. tools.jdg.dev 페이지가 이 파일을 읽어서 보여준다 |
| `site/` | tools.jdg.dev 페이지 (정적 HTML 한 장) |
| `infra/` | 페이지 서버(nginx) 설정 |
| `scripts/deploy.sh` | `site/`를 서버로 올리고 확인 |
| `.github/workflows/` | Windows 도구 빌드·릴리스 (GitHub Actions) |
| `docs/RUNBOOK.md` | 도구 추가·릴리스·페이지 배포 절차 |

## 받기

- 페이지: https://tools.jdg.dev
- 직접: [Releases](https://github.com/donggun-jung/pc-tools/releases)

Windows에서 인터넷으로 받은 zip은 **압축을 풀기 전에** 우클릭 → 속성 → "차단 해제"를 체크하면 첫 실행 경고가 뜨지 않는다.

## 도구 추가·새 버전

[docs/RUNBOOK.md](docs/RUNBOOK.md) 참고. 요약: 도구 폴더 추가 → PR → 태그(`<도구>-v<버전>`)로 릴리스 → `catalog.json` 갱신. 페이지는 `catalog.json`을 GitHub에서 바로 읽으므로 서버 재배포가 필요 없다.
