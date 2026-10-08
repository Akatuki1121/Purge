# MSIアップグレード互換性の実機検証(CI用、#110)。
# 公開済みバージョンのMSI(FromMsi)をインストールした状態で、新しいMSI(ToMsi)へ
# アップグレードし、ファイル置換・ショートカット・設定(レジストリ)が期待どおり
# 維持されることを確認する。「ビルドは通ったが、更新すると壊れる」をPR段階で検出する。
#
# 使い方:
#   pwsh -File scripts/Verify-MsiUpgrade.ps1 -FromMsi <旧MSI> -FromVersion 1.3.0 -ToMsi <新MSI> -ToVersion 9.9.9
#   同一バージョン再実行の検証では FromMsi と ToMsi に同じMSIを渡す。
# 管理者権限で実行する(GitHub-hostedのwindows-latestは既定で管理者)。
param(
    [Parameter(Mandatory)][string]$FromMsi,
    [Parameter(Mandatory)][string]$ToMsi,
    [Parameter(Mandatory)][string]$FromVersion,
    [Parameter(Mandatory)][string]$ToVersion,
    [string]$InstallDir = 'C:\Program Files\Purge'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $FromMsi)) { Write-Host "旧MSIが見つかりません: $FromMsi"; exit 2 }
if (-not (Test-Path -LiteralPath $ToMsi)) { Write-Host "新MSIが見つかりません: $ToMsi"; exit 2 }
$FromMsi = (Resolve-Path -LiteralPath $FromMsi).ProviderPath
$ToMsi = (Resolve-Path -LiteralPath $ToMsi).ProviderPath

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

function Show-LogTail([string]$logPath) {
    if (Test-Path $logPath) {
        Write-Host '--- msiexecログ(末尾) ---'
        Get-Content $logPath -Tail 30 -ErrorAction SilentlyContinue
    }
}

$exe = Join-Path $InstallDir 'Purge.exe'

# --- 1. ベースライン(旧バージョン)をインストール ---
Write-Host "=== 1. ベースライン $($FromVersion) をインストール ==="
$installLog = Join-Path $env:TEMP 'purge_upgrade_from.log'
$code = Invoke-Msi "/i `"$FromMsi`"" $installLog
Check '旧MSIのインストールが成功(終了コード0)' ($code -eq 0) "終了コード=$code ログ=$installLog"
if ($code -ne 0) {
    Show-LogTail $installLog
    Write-Host 'ベースラインのインストールに失敗したため以降の検証を中止します。'
    exit 1
}
$entry = Get-PurgeUninstallEntry
Check "Uninstall登録バージョンが $FromVersion" ($entry -and $entry.Version -eq $FromVersion) "実際=$($entry.Version)"
Check 'Purge.exe が配置された' (Test-Path $exe)

# --- 2. アップグレード前の状態を記録 ---
Write-Host '=== 2. アップグレード前の状態を記録 ==='
$shortcutsBefore = @(Get-PurgeShortcuts)
Check 'ショートカットが存在する' ($shortcutsBefore.Count -ge 1) "件数=$($shortcutsBefore.Count)"

# 設定(レジストリ)の維持検証用マーカー。
# HKLM\SOFTWARE\Purge は製品の設定キー。マーカー値を置き、アップグレード後も残るかを見る。
$settingsKey = 'HKLM:\SOFTWARE\Purge'
if (-not (Test-Path $settingsKey)) { New-Item $settingsKey -Force | Out-Null }
Set-ItemProperty $settingsKey -Name 'CiUpgradeMarker' -Value "keep-me-$FromVersion" -Type String
Write-Host "  マーカーを設定: HKLM\SOFTWARE\Purge\CiUpgradeMarker = keep-me-$FromVersion"

# --- 3. 新バージョンへアップグレード ---
Write-Host "=== 3. $($ToVersion) へアップグレード ==="
$upgradeLog = Join-Path $env:TEMP 'purge_upgrade_to.log'
$code = Invoke-Msi "/i `"$ToMsi`"" $upgradeLog
Check 'アップグレードが成功(終了コード0)' ($code -eq 0) "終了コード=$code ログ=$upgradeLog"
if ($code -ne 0) {
    Show-LogTail $upgradeLog
    Write-Host 'アップグレードに失敗したため以降の検証を中止します。'
    exit 1
}

# --- 4. アップグレード結果の検証 ---
Write-Host '=== 4. アップグレード結果の検証 ==='
$entry = Get-PurgeUninstallEntry
Check "Uninstall登録バージョンが $ToVersion" ($entry -and $entry.Version -eq $ToVersion) "実際=$($entry.Version)"

if (Test-Path $exe) {
    $exeVersion = (Get-Item $exe).VersionInfo.ProductVersion
    Check "Purge.exe が $ToVersion に置き換えられた" ($exeVersion -eq $ToVersion) "実際=$exeVersion"
} else {
    Check 'Purge.exe が配置された' $false
}
Check 'Purge.Core.dll が配置された' (Test-Path (Join-Path $InstallDir 'Purge.Core.dll'))

$shortcutsAfter = @(Get-PurgeShortcuts)
Check 'ショートカットが維持されている(消失なし)' ($shortcutsAfter.Count -ge $shortcutsBefore.Count) "前=$($shortcutsBefore.Count)件 後=$($shortcutsAfter.Count)件"
Check 'ショートカットが重複していない' ($shortcutsAfter.Count -le $shortcutsBefore.Count) "前=$($shortcutsBefore.Count)件 後=$($shortcutsAfter.Count)件"

$marker = (Get-ItemProperty $settingsKey -ErrorAction SilentlyContinue).CiUpgradeMarker
Check '設定(HKLM\SOFTWARE\Purge)が維持された' ($marker -eq "keep-me-$FromVersion") "実際=$marker"

# --- 5. クリーンアップ(アンインストール) ---
Write-Host '=== 5. アンインストールして後片付け ==='
if ($entry) {
    $uninstallLog = Join-Path $env:TEMP 'purge_upgrade_uninstall.log'
    $code = Invoke-Msi "/x $($entry.Code)" $uninstallLog
    Check 'アンインストールが成功(終了コード0)' ($code -eq 0) "終了コード=$code ログ=$uninstallLog"
}
Check 'Uninstall登録が消えた' (-not (Get-PurgeUninstallEntry))
Check 'インストールフォルダが消えた' (-not (Test-Path $InstallDir))
Check 'ショートカットが消えた' (@(Get-PurgeShortcuts).Count -eq 0)

Write-Host ''
if ($script:failures.Count -eq 0) {
    Write-Host "全ての検証に合格しました($FromVersion → $ToVersion)。"
    exit 0
}
Write-Host "検証に失敗しました($($script:failures.Count)件):"
$script:failures | ForEach-Object { Write-Host "  - $_" }
exit 1
