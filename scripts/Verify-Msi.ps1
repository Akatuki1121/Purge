# MSIインストーラーの実機検証(CI用)。
# ビルドしたMSIを実際にインストールし、期待通りに配置・登録され、アンインストールで
# 消し残しがないことを確認する。「ビルドは通ったが、入れてみたら動かない」を
# リリース前に検出するためのスクリプト。
#
# 使い方: pwsh -File scripts/Verify-Msi.ps1 -MsiPath <path> -ExpectedVersion 0.3.6
# 管理者権限で実行する(GitHub-hostedのwindows-latestは既定で管理者)。
param(
    [Parameter(Mandatory)][string]$MsiPath,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [string]$InstallDir = 'C:\Program Files\Purge',
    [int]$LaunchSeconds = 5
)

$ErrorActionPreference = 'Stop'
$script:failures = New-Object System.Collections.Generic.List[string]

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "  [OK]  $name" }
    else {
        Write-Host "  [NG]  $name $detail"
        $script:failures.Add("$name $detail".Trim())
    }
}

function Get-PurgeUninstallEntry {
    foreach ($hive in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                      'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
        Get-ChildItem $hive -ErrorAction SilentlyContinue | ForEach-Object {
            $p = Get-ItemProperty $_.PSPath
            if ($p.DisplayName -eq 'Purge') {
                [pscustomobject]@{ Code = $_.PSChildName; Version = $p.DisplayVersion }
            }
        }
    }
}

function Get-PurgeShortcuts {
    $dirs = @([Environment]::GetFolderPath('CommonDesktopDirectory'),
              [Environment]::GetFolderPath('CommonPrograms'))
    $dirs | Where-Object { $_ } | ForEach-Object {
        Get-ChildItem $_ -Filter 'Purge.lnk' -ErrorAction SilentlyContinue
    }
}

function Invoke-Msi([string]$arguments, [string]$logPath) {
    $p = Start-Process msiexec -ArgumentList "$arguments /qn /norestart /l*v `"$logPath`"" -Wait -PassThru
    return $p.ExitCode
}

Write-Host "=== 1. インストール前の状態 ==="
Check '既存のPurgeがない(クリーン環境)' (-not (Get-PurgeUninstallEntry)) '(既存インストールあり。CIランナー以外で実行していないか確認)'

Write-Host "=== 2. サイレントインストール ==="
$installLog = Join-Path $env:TEMP 'purge_install.log'
$code = Invoke-Msi "/i `"$MsiPath`"" $installLog
Check 'msiexec /i が成功(終了コード0)' ($code -eq 0) "終了コード=$code ログ=$installLog"

Write-Host "=== 3. インストール結果の検証 ==="
$exe = Join-Path $InstallDir 'Purge.UI.exe'
$entry = Get-PurgeUninstallEntry
Check 'Purge.UI.exe が配置された' (Test-Path $exe)
Check 'Purge.Core.dll が配置された' (Test-Path (Join-Path $InstallDir 'Purge.Core.dll'))
Check 'Uninstall登録(プログラムと機能)がある' ($null -ne $entry)
Check "登録バージョンが $ExpectedVersion" ($entry -and $entry.Version -eq $ExpectedVersion) "実際=$($entry.Version)"
Check 'スタートメニュー/デスクトップのショートカットが作られた' (@(Get-PurgeShortcuts).Count -ge 1)

if (Test-Path $exe) {
    Write-Host "=== 4. 起動確認(${LaunchSeconds}秒間生存するか) ==="
    $proc = Start-Process $exe -PassThru
    Start-Sleep -Seconds $LaunchSeconds
    $alive = -not $proc.HasExited
    Check "起動後 ${LaunchSeconds} 秒間クラッシュせず動作している" $alive "終了コード=$($proc.ExitCode)"
    if ($alive) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1 }
}

Write-Host "=== 5. アンインストール ==="
if ($entry) {
    $uninstallLog = Join-Path $env:TEMP 'purge_uninstall.log'
    $code = Invoke-Msi "/x $($entry.Code)" $uninstallLog
    Check 'msiexec /x が成功(終了コード0)' ($code -eq 0) "終了コード=$code ログ=$uninstallLog"
}

Write-Host "=== 6. 消し残しの検証 ==="
Check 'Uninstall登録が消えた' (-not (Get-PurgeUninstallEntry))
Check 'インストールフォルダが消えた' (-not (Test-Path $InstallDir)) "残存件数=$(if (Test-Path $InstallDir) { (Get-ChildItem $InstallDir -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count })"
Check 'ショートカットが消えた' (@(Get-PurgeShortcuts).Count -eq 0)
Check 'HKLM\Software\Purge が消えた' (-not (Test-Path 'HKLM:\SOFTWARE\Purge'))

Write-Host ''
if ($script:failures.Count -eq 0) {
    Write-Host '全ての検証に合格しました。'
    exit 0
}
Write-Host "検証に失敗しました($($script:failures.Count)件):"
$script:failures | ForEach-Object { Write-Host "  - $_" }
exit 1
