# MSIアップグレード時にデスクトップ/スタートメニューのショートカットが削除・再作成されないことの実機検証(CI用、#92)。
# 旧版MSIを入れてから新版MSIで上書きアップグレードし、
#  - アップグレード中にショートカットの削除イベントが起きない
#  - ショートカットの作成日時が変わらない(=作り直されていない)
# ことを確認する。管理者権限で実行する(GitHub-hostedのwindows-latestは既定で管理者)。
param(
    [Parameter(Mandatory)][string]$OldMsi,
    [Parameter(Mandatory)][string]$NewMsi
)

$ErrorActionPreference = 'Stop'
$OldMsi = (Resolve-Path -LiteralPath $OldMsi).ProviderPath
$NewMsi = (Resolve-Path -LiteralPath $NewMsi).ProviderPath

$desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
$programs = [Environment]::GetFolderPath('CommonPrograms')
$targets = @(
    [pscustomobject]@{ Name = 'デスクトップ'; Dir = $desktop },
    [pscustomobject]@{ Name = 'スタートメニュー'; Dir = $programs }
)
$failures = New-Object System.Collections.Generic.List[string]

function Invoke-Msi([string]$arguments, [string]$log) {
    $p = Start-Process msiexec -ArgumentList "$arguments /qn /norestart /l*v `"$log`"" -Wait -PassThru
    return $p.ExitCode
}

Write-Host '=== 1. 旧版をインストール ==='
$code = Invoke-Msi "/i `"$OldMsi`"" (Join-Path $env:TEMP 'purge_upgrade_old.log')
if ($code -ne 0) { Write-Host "旧版のインストールに失敗(終了コード=$code)"; exit 1 }

$before = @{}
foreach ($t in $targets) {
    $f = Join-Path $t.Dir 'Purge.lnk'
    if (-not (Test-Path $f)) { Write-Host "[NG] $($t.Name)のショートカットが旧版で作られていない"; exit 1 }
    $before[$t.Name] = (Get-Item $f).CreationTimeUtc.Ticks
    Write-Host "  $($t.Name): 作成日時(UTC ticks)=$($before[$t.Name])"
}

Write-Host '=== 2. 削除イベントを監視しながら新版へアップグレード ==='
$deleted = New-Object System.Collections.ArrayList
$watchers = @()
$i = 0
foreach ($t in $targets) {
    $w = New-Object System.IO.FileSystemWatcher $t.Dir, 'Purge.lnk'
    $w.EnableRaisingEvents = $true
    $i++
    # -Actionを付けないとイベントはGet-Eventのキューに溜まる(後でまとめて読む)
    Register-ObjectEvent $w Deleted -SourceIdentifier "purge_lnk_deleted_$i" -MessageData $t.Name | Out-Null
    $watchers += $w
}
$code = Invoke-Msi "/i `"$NewMsi`"" (Join-Path $env:TEMP 'purge_upgrade_new.log')
Start-Sleep -Seconds 2
$events = Get-Event | Where-Object { $_.SourceIdentifier -like 'purge_lnk_deleted_*' }
foreach ($e in $events) { [void]$deleted.Add($e.MessageData) }

if ($code -ne 0) { $failures.Add("新版へのアップグレードが失敗(終了コード=$code)") }
foreach ($t in $targets) {
    $f = Join-Path $t.Dir 'Purge.lnk'
    if (-not (Test-Path $f)) { $failures.Add("$($t.Name)のショートカットがアップグレード後に存在しない"); continue }
    $after = (Get-Item $f).CreationTimeUtc.Ticks
    if ($after -ne $before[$t.Name]) { $failures.Add("$($t.Name)のショートカットが作り直された(作成日時が変化)") }
    if ($deleted -contains $t.Name) { $failures.Add("$($t.Name)のショートカットがアップグレード中に一度削除された") }
}

Write-Host '=== 3. 後始末 ==='
Get-Event | Remove-Event -ErrorAction SilentlyContinue
Get-EventSubscriber | Unregister-Event -ErrorAction SilentlyContinue
$watchers | ForEach-Object { $_.Dispose() }
Invoke-Msi "/x `"$NewMsi`"" (Join-Path $env:TEMP 'purge_upgrade_cleanup.log') | Out-Null

if ($failures.Count -gt 0) {
    Write-Host '=== 結果: NG ==='
    $failures | ForEach-Object { Write-Host "  [NG] $_" }
    exit 1
}
Write-Host '=== 結果: OK(ショートカットは削除も再作成もされなかった) ==='
