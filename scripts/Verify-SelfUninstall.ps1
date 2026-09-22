# 自己アンインストール(SelfUninstaller)の実環境検証(CI用)。
# インストール済みのPurgeに対して、GUIを介さず BeginSelfUninstall を直接呼び、
# 「batがプロセス終了を待つ → msiexec /x で自分自身を削除する」一連の流れが
# 実際に完了して消し残しがないことを確認する。
#
# 単体テスト(BuildBatchContentの文字列検証)では確認できない、
# 「本当に消えるか」をリリース前に確認するためのもの。#16 の自動化版。
#
# 使い方: pwsh -File scripts/Verify-SelfUninstall.ps1 -MsiPath <path> -ExpectedVersion <ver>
# 管理者権限で実行する。クリーンな環境(Purge未インストール)を前提とする。
param(
    [Parameter(Mandatory)][string]$MsiPath,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [string]$InstallDir = 'C:\Program Files\Purge',
    [int]$WaitSeconds = 90
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

if (-not (Test-Path -LiteralPath $MsiPath)) {
    Write-Host "MSIが見つかりません: $MsiPath"
    exit 2
}
$MsiPath = (Resolve-Path -LiteralPath $MsiPath).ProviderPath

Write-Host "=== 1. 前提: クリーン環境 ==="
if (Get-PurgeUninstallEntry) {
    Write-Host '  [NG]  既にPurgeがインストールされています。CIランナー以外で実行していないか確認してください。'
    exit 2
}
Write-Host '  [OK]  既存のPurgeなし'

Write-Host "=== 2. インストール ==="
$installLog = Join-Path $env:TEMP 'purge_selfuninstall_install.log'
$p = Start-Process msiexec -ArgumentList "/i `"$MsiPath`" /qn /norestart /l*v `"$installLog`"" -Wait -PassThru
Check 'msiexec /i が成功' ($p.ExitCode -eq 0) "終了コード=$($p.ExitCode)"
$entry = Get-PurgeUninstallEntry
Check "インストールされた(登録バージョン $ExpectedVersion)" ($entry -and $entry.Version -eq $ExpectedVersion) "実際=$($entry.Version)"
if ($p.ExitCode -ne 0 -or -not $entry) {
    # 入っていない状態で後続を進めると「消えている=成功」と誤って合格するため、ここで打ち切る。
    if (Test-Path $installLog) { Write-Host '--- msiexecログ(末尾) ---'; Get-Content $installLog -Tail 30 }
    Write-Host 'インストールに失敗したため以降の検証を中止します。'
    exit 1
}

$before = @(Get-PurgeShortcuts)
Check 'ショートカットが存在する(削除確認の前提)' ($before.Count -ge 1)

Write-Host "=== 3. 自己アンインストールを実行(BeginSelfUninstallを直接呼ぶ) ==="
# 呼び出し元プロセスは「Purge本体の代わり」になる。BeginSelfUninstallはそのPIDの終了を
# batが待つ設計なので、ハーネスは呼び出し後すぐに終了する。
$harness = Join-Path $env:TEMP 'purge_selfuninstall_harness.ps1'
@"
`$ErrorActionPreference = 'Stop'
Add-Type -Path '$InstallDir\Purge.Core.dll'
`$log = New-Object Purge.OperationLog -ArgumentList 500
[Purge.SelfUninstaller]::BeginSelfUninstall(`$log, '$InstallDir\Purge.UI.exe')
`$log.GetRecent(30) | ForEach-Object { Write-Output ('[log] ' + `$_.ToString()) }
"@ | Set-Content $harness -Encoding UTF8

$h = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $harness -Wait -PassThru -RedirectStandardOutput (Join-Path $env:TEMP 'purge_harness_out.txt') -RedirectStandardError (Join-Path $env:TEMP 'purge_harness_err.txt')
Check 'BeginSelfUninstall が例外なく完了' ($h.ExitCode -eq 0) "終了コード=$($h.ExitCode)"
if ($h.ExitCode -ne 0) {
    Get-Content (Join-Path $env:TEMP 'purge_harness_err.txt') -ErrorAction SilentlyContinue
}
Get-Content (Join-Path $env:TEMP 'purge_harness_out.txt') -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }

Write-Host "=== 4. 削除の完了を待機(最大 ${WaitSeconds} 秒) ==="
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline -and ((Get-PurgeUninstallEntry) -or (Test-Path $InstallDir))) {
    Start-Sleep -Seconds 2
}
$elapsed = [math]::Round(($WaitSeconds - ($deadline - (Get-Date)).TotalSeconds), 0)
Write-Host "  待機時間: 約${elapsed}秒"

Write-Host "=== 5. 消し残しの検証 ==="
Check 'Uninstall登録(プログラムと機能)が消えた' (-not (Get-PurgeUninstallEntry))
Check 'インストールフォルダが消えた' (-not (Test-Path $InstallDir)) "残存件数=$(if (Test-Path $InstallDir) { (Get-ChildItem $InstallDir -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count })"
Check 'ショートカットが消えた' (@(Get-PurgeShortcuts).Count -eq 0)
Check 'HKLM\Software\Purge が消えた' (-not (Test-Path 'HKLM:\SOFTWARE\Purge'))
Check 'bat一時ファイルが消えた(自己削除できている)' (-not (Get-ChildItem $env:TEMP -Filter 'uninstall_self_*.bat' -ErrorAction SilentlyContinue))
Check 'Purgeのプロセスが残っていない' (-not (Get-Process -Name 'Purge*' -ErrorAction SilentlyContinue))

Write-Host ''
if ($script:failures.Count -eq 0) {
    Write-Host '全ての検証に合格しました。'
    exit 0
}
Write-Host "検証に失敗しました($($script:failures.Count)件):"
$script:failures | ForEach-Object { Write-Host "  - $_" }
exit 1
