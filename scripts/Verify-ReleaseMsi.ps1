# 配布用MSIのリリース前最終検証(#107)。
# 1) MSIの内容(ProductVersion・UpgradeCode・ProductCode・収録ファイル)を検査し、
# 2) 直前の公開済みMSIとのUpgradeCode/ProductCodeの関係を確認し、
# 3) scripts/Verify-Msi.ps1 によるインストール・起動・アンインストール検証を行う。
# この検証に失敗したMSIはGitHub Releaseへ公開してはならない。
#
# 使い方: pwsh -File scripts/Verify-ReleaseMsi.ps1 -MsiPath <MSI> -ExpectedVersion 1.4.0 [-BaselineMsiPath <直前の公開MSI>]
param(
    [Parameter(Mandatory)][string]$MsiPath,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    # 直前に公開済みのMSI。指定するとアップグレード要件(UpgradeCode一致・ProductCode相違)を検証する。
    [string]$BaselineMsiPath = '',
    [string]$WxsPath = 'installer/Product.wxs'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $MsiPath)) { Write-Host "MSIが見つかりません: $MsiPath"; exit 2 }
$MsiPath = (Resolve-Path -LiteralPath $MsiPath).ProviderPath
if ($BaselineMsiPath -and -not (Test-Path -LiteralPath $BaselineMsiPath)) {
    Write-Host "ベースラインMSIが見つかりません: $BaselineMsiPath"; exit 2
}
if (-not (Test-Path -LiteralPath $WxsPath)) { Write-Host "WiX定義が見つかりません: $WxsPath"; exit 2 }

$script:failures = New-Object System.Collections.Generic.List[string]

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "  [OK]  $name" }
    else {
        Write-Host "  [NG]  $name $detail"
        $script:failures.Add("$name $detail".Trim())
    }
}

# Windows Installer COM APIでMSIデータベースを開く
function Open-MsiDatabase([string]$path) {
    $wi = New-Object -ComObject WindowsInstaller.Installer
    return $wi.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $wi, @($path, 0))
}

# 1列のSELECT結果を文字列のリストで返す
function Get-MsiColumn($db, [string]$sql) {
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($sql))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
    $rows = [System.Collections.Generic.List[string]]::new()
    while ($true) {
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) { break }
        $rows.Add($record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1)))
    }
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
    return $rows
}

function Get-MsiProperty($db, [string]$name) {
    $rows = @(Get-MsiColumn $db "SELECT Value FROM Property WHERE Property='$name'")
    if ($rows.Count -ge 1) { return $rows[0] } else { return $null }
}

Write-Host '=== 1. MSIの内容検査 ==='
$db = Open-MsiDatabase $MsiPath

$productVersion = Get-MsiProperty $db 'ProductVersion'
Check "ProductVersionがタグのバージョン($ExpectedVersion)と一致" ($productVersion -eq $ExpectedVersion) "実際=$productVersion"

$productCode = Get-MsiProperty $db 'ProductCode'
Check 'ProductCodeが設定されている' (-not [string]::IsNullOrEmpty($productCode)) "実際=$productCode"

# UpgradeCodeはWiX定義と一致していること
$wxs = Get-Content -LiteralPath $WxsPath -Raw
if ($wxs -match 'UpgradeCode="([^"]+)"') {
    $expectedUpgradeCode = $Matches[1]
    $upgradeCode = Get-MsiProperty $db 'UpgradeCode'
    Check 'UpgradeCodeがWiX定義と一致' ($upgradeCode -and $expectedUpgradeCode -and ([guid]$upgradeCode -eq [guid]$expectedUpgradeCode)) "実際=$upgradeCode 期待=$expectedUpgradeCode"
} else {
    Check 'WiX定義からUpgradeCodeを取得できた' $false "$WxsPath"
}

# 収録ファイル(Fileテーブル)に必要なファイルがあること
$fileNames = Get-MsiColumn $db 'SELECT FileName FROM File'
Check 'Purge.exe が収録されている' (@($fileNames | Where-Object { $_ -match 'Purge\.exe' }).Count -ge 1)
Check 'Purge.Core.dll が収録されている' (@($fileNames | Where-Object { $_ -match 'Purge\.Core\.dll' }).Count -ge 1)
# WiXのライセンス文書(WixUILicenseRtf)は、BinaryテーブルではなくLicenseAgreementDlgの
# ScrollableText(Controlテーブル LicenseText)のText列にRTFとして埋め込まれる。公開済みv1.3.0のMSIで確認済み。
$licenseRtf = @(Get-MsiColumn $db "SELECT Text FROM Control WHERE Dialog_='LicenseAgreementDlg' AND Control='LicenseText'")
$licenseOk = ($licenseRtf.Count -ge 1) -and ($licenseRtf[0] -like '{\rtf*') -and ($licenseRtf[0].Length -gt 1000)
Check 'ライセンス文書がMSIに埋め込まれている' $licenseOk "LicenseTextのRTF長=$(if ($licenseRtf.Count -ge 1) { $licenseRtf[0].Length } else { '行なし' })"

if ($BaselineMsiPath) {
    Write-Host ''
    Write-Host '=== 2. 直前の公開済みMSIとの比較 ==='
    $baselineDb = Open-MsiDatabase ((Resolve-Path -LiteralPath $BaselineMsiPath).ProviderPath)
    $baselineUpgradeCode = Get-MsiProperty $baselineDb 'UpgradeCode'
    $baselineProductCode = Get-MsiProperty $baselineDb 'ProductCode'
    Check 'UpgradeCodeが直前の公開済みMSIと一致(アップグレード可能)' ($baselineUpgradeCode -eq $upgradeCode) "実際=$baselineUpgradeCode"
    Check 'ProductCodeが直前の公開済みMSIと異なる(メジャーアップグレードとして扱われる)' ($baselineProductCode -ne $productCode) "直前=$baselineProductCode"
}

Write-Host ''
Write-Host '=== 3. インストール・起動・アンインストール検証(scripts/Verify-Msi.ps1) ==='
& (Join-Path $PSScriptRoot 'Verify-Msi.ps1') -MsiPath $MsiPath -ExpectedVersion $ExpectedVersion
$verifyExit = $LASTEXITCODE
Check 'Verify-Msi.ps1 が全項目合格' ($verifyExit -eq 0) "終了コード=$verifyExit"

Write-Host ''
if ($script:failures.Count -eq 0) {
    Write-Host '配布用MSIの最終検証に全て合格しました。'
    exit 0
}
Write-Host "最終検証に失敗しました($($script:failures.Count)件)。このMSIはGitHub Releaseへ公開しないでください:"
$script:failures | ForEach-Object { Write-Host "  - $_" }
exit 1
