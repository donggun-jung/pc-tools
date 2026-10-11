$ErrorActionPreference = 'SilentlyContinue'
Set-Location -Path $PSScriptRoot

$log = Join-Path $PSScriptRoot 'MonitorSaver.log'
$hb  = Join-Path $PSScriptRoot 'heartbeat.txt'

Write-Host ''
$p = Get-Process MonitorSaver
if ($p) {
    Write-Host '  [ OK ]  실행 중 - 단축키가 동작합니다' -ForegroundColor Green
    $p | Select-Object Id, @{n='시작'; e={ $_.StartTime.ToString('MM-dd HH:mm:ss') }} | Format-Table -AutoSize | Out-String | ForEach-Object { $_.TrimEnd() } | Write-Host
    Write-Host '  (프로세스가 2개인 것이 정상입니다: 본체 + 감시용)' -ForegroundColor DarkGray
} else {
    Write-Host '  [  X ]  실행 중이 아닙니다 - 단축키를 눌러도 아무 일도 없습니다' -ForegroundColor Red
    Write-Host '          Start.cmd 로 다시 실행하세요' -ForegroundColor Yellow
}

if (Test-Path $hb) {
    $t = (Get-Item $hb).LastWriteTime
    $ago = [int]((Get-Date) - $t).TotalMinutes
    Write-Host ''
    Write-Host ("  마지막 생존 확인: {0}  ({1}분 전)" -f $t.ToString('yyyy-MM-dd HH:mm:ss'), $ago)
    if (-not $p) {
        Write-Host '  ^ 이 시각 직후에 죽은 것입니다.' -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host '--- 종료 기록 (왜 꺼졌는가) ---' -ForegroundColor Cyan
if (Test-Path $log) {
    $d = Select-String -Path $log -Pattern '종료됨|!! |session ending|process exit' -Encoding UTF8 | Select-Object -Last 8
    if ($d) { $d | ForEach-Object { Write-Host ('  ' + $_.Line) } }
    else    { Write-Host '  (아직 종료 기록 없음)' -ForegroundColor DarkGray }
} else {
    Write-Host '  (로그 파일 없음)' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host '--- 최근 로그 ---' -ForegroundColor Cyan
if (Test-Path $log) {
    Get-Content $log -Tail 12 -Encoding UTF8 | ForEach-Object { Write-Host ('  ' + $_) }
}
Write-Host ''
