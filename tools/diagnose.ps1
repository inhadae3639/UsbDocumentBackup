<#
.SYNOPSIS
    Collects everything needed to work out why a backup did not reach Google Drive.

.DESCRIPTION
    Reads the application's own state on this PC and prints a report.

    Deliberately never prints a credential: token files are reported by name and size only, and the
    OAuth client is reported by client id (which is not a secret) with the client secret omitted.
    The report is safe to paste into a chat.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File diagnose.ps1
    powershell -ExecutionPolicy Bypass -File diagnose.ps1 -OutFile "$env:USERPROFILE\Desktop\report.txt"
#>
[CmdletBinding()]
param(
    [string]$OutFile
)

$ErrorActionPreference = 'Continue'
$root = Join-Path $env:LOCALAPPDATA 'UsbDocumentBackup'
$lines = [System.Collections.Generic.List[string]]::new()
function Say([string]$text) { $lines.Add($text); Write-Host $text }

Say "UsbDocumentBackup 진단  ($(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))"
Say ("PC: {0}   사용자: {1}" -f $env:COMPUTERNAME, $env:USERNAME)
Say ("데이터 폴더: {0}" -f $root)
Say ('=' * 70)

if (-not (Test-Path $root)) {
    Say ""
    Say "!! 데이터 폴더가 없습니다. 이 PC에서 프로그램이 한 번도 실행되지 않았습니다."
    if ($OutFile) { $lines | Set-Content -Path $OutFile -Encoding utf8 }
    return
}

# ---------- 실행 상태 ----------
Say ""
Say "[실행 상태]"
$proc = Get-Process -Name UsbDocumentBackup -ErrorAction SilentlyContinue
if ($proc) {
    foreach ($p in $proc) { Say ("  실행 중  PID={0}  시작={1}" -f $p.Id, $p.StartTime) }
    Say ("  실행 파일: {0}" -f $proc[0].Path)
} else {
    Say "  실행 중이 아님  <-- 트레이에 아이콘이 없으면 프로그램이 꺼진 것입니다."
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$auto = (Get-ItemProperty -Path $runKey -Name UsbDocumentBackup -ErrorAction SilentlyContinue).UsbDocumentBackup
Say ("  자동 실행 등록: {0}" -f $(if ($auto) { $auto } else { '없음' }))

# ---------- Google 연결 ----------
Say ""
Say "[Google 연결]"
$cred = Join-Path $root 'credentials'
$clientFile = Join-Path $cred 'client_secret.json'

if (Test-Path $clientFile) {
    try {
        $j = Get-Content $clientFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $inner = if ($j.installed) { $j.installed } elseif ($j.web) { $j.web } else { $null }
        # client_id 는 비밀이 아니다. client_secret 은 절대 출력하지 않는다.
        Say ("  클라이언트 설정 파일: 있음   client_id={0}" -f $inner.client_id)
    } catch {
        Say "  클라이언트 설정 파일: 있음 (읽기 실패)"
    }
} else {
    Say "  클라이언트 설정 파일: 없음 (실행 파일에 내장된 것을 사용 중일 수 있음)"
}

$tokens = @(Get-ChildItem $cred -Filter *.bin -ErrorAction SilentlyContinue)
if ($tokens.Count -gt 0) {
    foreach ($t in $tokens) { Say ("  저장된 토큰: {0} ({1} bytes, {2})" -f $t.Name, $t.Length, $t.LastWriteTime) }
    Say "  -> 이 PC에서 한 번은 연결에 성공했습니다."
} else {
    Say "  저장된 토큰: 없음"
    Say "  -> 이 PC에서 Google 연결을 한 적이 없습니다. 업로드는 전부 대기 상태로 남습니다."
    Say "     토큰은 PC마다 따로 만들어집니다. 다른 PC에서 연결했어도 이 PC에는 적용되지 않습니다."
}

# ---------- 설정 ----------
Say ""
Say "[설정]"
$settingsFile = Join-Path $root 'settings.json'
if (Test-Path $settingsFile) {
    try {
        $s = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
        Say ("  연결된 계정: {0}" -f $(if ($s.GoogleAccountKey) { $s.GoogleAccountKey } else { '없음' }))
        Say ("  지정 계정:   {0}" -f $(if ($s.ExpectedGoogleAccount) { $s.ExpectedGoogleAccount } else { '없음' }))
        Say ("  Drive 폴더:  {0}  (id={1})" -f $s.DriveFolderName, $(if ($s.DriveFolderId) { $s.DriveFolderId } else { '아직 없음' }))
        Say ("  일시정지:    {0}" -f $s.Paused)
        Say ("  보관 기간:   {0}일" -f $s.TemporaryRetentionDays)
    } catch { Say "  settings.json 읽기 실패" }
} else {
    Say "  settings.json 없음 (설정을 저장한 적 없음 = Google 연결도 안 했을 가능성)"
}

# ---------- 백업/업로드 상태 ----------
Say ""
Say "[백업과 업로드]"
$db = Join-Path $root 'state.db'
if (-not (Test-Path $db)) {
    Say "  state.db 없음"
} else {
    $sqlite = Get-Command sqlite3 -ErrorAction SilentlyContinue
    if ($sqlite) {
        $copy = Join-Path $env:TEMP 'udb-diag.db'
        Copy-Item $db $copy -Force
        function Q($sql) { & $sqlite.Source $copy $sql }
        Say "  등급/상태별 백업:"
        (Q "SELECT tier||' '||state||'  '||COUNT(*)||'건' FROM backups GROUP BY tier,state;") | ForEach-Object { Say "    $_" }
        Say "  업로드 큐:"
        $u = (Q "SELECT state||'  '||COUNT(*)||'건' FROM uploads GROUP BY state;")
        if ($u) { $u | ForEach-Object { Say "    $_" } } else { Say "    (비어 있음)" }
        Say "  업로드 실패 사유 (최근 5건):"
        $e = (Q "SELECT substr(COALESCE(last_error,'-'),1,90) FROM uploads WHERE last_error IS NOT NULL ORDER BY rowid DESC LIMIT 5;")
        if ($e) { $e | ForEach-Object { Say "    $_" } } else { Say "    (없음)" }
        Say ("  '열림'으로 기록된 문서: {0}건" -f (Q "SELECT COUNT(*) FROM opened_documents;"))
        Remove-Item $copy -ErrorAction SilentlyContinue
    } else {
        Say "  (sqlite3 명령이 없어 DB 요약을 건너뜁니다. 상태 창의 숫자를 대신 알려주세요.)"
    }
}

# ---------- 보관함 ----------
Say ""
Say "[보관함]"
foreach ($tier in @('retained', 'temp')) {
    $p = Join-Path $root "archive\$tier"
    if (Test-Path $p) {
        $f = @(Get-ChildItem $p -Recurse -File -ErrorAction SilentlyContinue)
        $mb = if ($f.Count) { ($f | Measure-Object Length -Sum).Sum / 1MB } else { 0 }
        Say ("  {0}: {1}개, {2:N1} MB" -f $tier, $f.Count, $mb)
    } else {
        Say ("  {0}: 폴더 없음" -f $tier)
    }
}

# ---------- 로그 ----------
Say ""
Say "[로그]"
$log = Join-Path $root 'logs\app.log'
if (Test-Path $log) {
    $info = Get-Item $log
    Say ("  파일: {0} bytes, 마지막 기록 {1}" -f $info.Length, $info.LastWriteTime)
    Say ""
    Say "  --- WARN / ERROR 전체 (최근 20건) ---"
    $problems = @(Select-String -Path $log -Pattern '\[WARN\]|\[ERROR\]' -Encoding UTF8 |
                  Select-Object -Last 20 -ExpandProperty Line)
    if ($problems) { $problems | ForEach-Object { Say "    $_" } } else { Say "    (없음)" }
    Say ""
    Say "  --- 마지막 15줄 ---"
    Get-Content $log -Tail 15 -Encoding UTF8 | ForEach-Object { Say "    $_" }
} else {
    Say "  로그 파일 없음"
}

Say ""
Say ('=' * 70)

if ($OutFile) {
    $lines | Set-Content -Path $OutFile -Encoding utf8
    Write-Host ""
    Write-Host "저장했습니다: $OutFile"
}
