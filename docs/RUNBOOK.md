# pc-tools 운영 절차

다음 세션이 이 문서만 보고 도구를 추가·릴리스하고 페이지를 다시 배포할 수 있게 쓴다.

## 구성

| 무엇 | 어디 |
|---|---|
| 도구 소스 | `windows/<도구>/`, `mac/<도구>/` (도구마다 README.md) |
| 도구 목록 | `catalog.json` — 페이지가 `https://raw.githubusercontent.com/donggun-jung/pc-tools/main/catalog.json`을 직접 읽는다 |
| 다운로드 파일 | GitHub Releases (태그 `<도구 id>-v<버전>`) |
| 페이지 | `site/index.html` + `site/app.js`(목록 렌더) + `site/catalog.json`(GitHub을 못 읽을 때 쓰는 사본) → VPS `/opt/pc-tools/site/` |
| 서버 | 컨테이너 `pc-tools`(nginx:1.27-alpine, 네트워크 `investor-profiler_default`, 외부 포트 없음) · 중앙 Caddy `tools.jdg.dev` → `pc-tools:80` · Kuma `tools-https` |
| 서버 배포 기록·롤백 | `donggun-jung/vps-ops-recovery` `reports/20261012_tools_jdg_dev_deploy_plan.md` |
| 사규 등재 | `donggun-jung/company-operations` `governance/projects.yml`의 `pc-tools`, `governance/apps.yml`의 `tools` |

## 커밋 규칙

브랜치·PR로 main에 반영한다(ruleset, squash). 커밋에는 company-operations `governance/AGENT_IDENTITY.md`의 트레일러 6종(`Agent-Tool`·`Agent-Model`·`Agent-Session`·`Agent-Machine`·`Task-Ref`·`Co-authored-by`)을 빈 줄 없이 한 덩어리로 붙인다.

## Windows 도구 새 버전 (예: MonitorSaver)

1. 소스 수정 + 소스 안의 버전 상수(`Program.Version`) 올리기 + 도구 README 변경 내역.
2. PR → GitHub Actions `windows-release`가 Windows에서 빌드하고 `-quit` 실행 확인까지 한다. 초록이면 머지.
3. main에서 태그: `git tag monitorsaver-v<버전> && git push origin monitorsaver-v<버전>`
   → Actions가 다시 빌드해서 Release(`MonitorSaver-<버전>.zip` + `.sha256`)를 만든다. 태그 버전과 소스 버전이 다르면 실패한다.
4. Release가 생긴 것을 확인한 **뒤에** `catalog.json`의 `version`·`released`·`download`·`checksum`을 고치는 PR → 머지.
   페이지는 몇 분 안에 새 목록을 보여준다(GitHub raw 캐시 최대 5분). 서버 재배포는 필요 없다.
5. 실제 윈도우 PC에서 받은 zip으로 한 번 실행해 본다(에이전트는 Windows를 직접 돌릴 수 없다).

## 새 도구 추가

- Windows: `windows/<도구>/` + README. 빌드가 필요한 도구면 `.github/workflows/windows-release.yml`에 같은 모양의 job과 태그 패턴(`<도구 id>-v*`)을 추가한다.
- Mac: `mac/<도구>/` + README. 스크립트형 도구는 폴더를 zip으로 묶어 Release에 올리는 workflow를 같은 방식으로 추가한다.
- `catalog.json`에 항목 추가. 필드: `id`, `name`, `platform`(`windows`|`mac`), `version`, `released`, `summary`, `hotkey`(선택), `hotkey_action`(선택), `details`[], `requirements`, `download`, `checksum`, `source`, `steps`[].
  페이지는 `https://github.com/donggun-jung/pc-tools/`로 시작하는 링크만 보여준다.

## 페이지(site/) 자체를 바꿀 때

1. 디자인 원본: Claude Design 프로젝트 "tools.jdg.dev — 도구 다운로드 페이지" (`Page.dc.html`). 시각 변경은 원본부터 고친 뒤 `site/index.html`에 옮긴다.
2. `cp catalog.json site/catalog.json` (사본 동기화) → `node tests/site-app.test.js` 전부 PASS.
3. 보안 규칙: 페이지는 인라인 스크립트를 쓰지 않는다(`infra/nginx-default.conf`의 CSP가 `script-src 'self'`). 새 외부 출처(글꼴·API)가 생기면 CSP 목록도 같이 고치고, 고친 nginx 설정은 서버의 `/opt/pc-tools/nginx-default.conf`에도 반영해야 한다(컨테이너 재시작 필요 — 서버 절차는 vps-ops-recovery).
4. 원격 목록은 `textContent`로만 그리고, 링크는 URL 정규화 뒤 `https://github.com/donggun-jung/pc-tools/` 아래만 허용한다. 비었거나 깨진 목록은 이미 보이는 목록을 지우지 않는다.
5. PR → 머지 → `scripts/deploy.sh` (rsync + 확인. 컨테이너 재시작 불필요). 라이브 변경은 오너 승인 대상(`apps.yml` `deploy_approval: owner`).

## 확인

```
curl -s -o /dev/null -w "%{http_code}\n" https://tools.jdg.dev/
curl -s https://tools.jdg.dev/health
```
