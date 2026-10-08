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
    Register-ObjectEvent $w Created -SourceIdentifier "purge_lnk_created_$i" -MessageData $t.Name | Out-Null
    $watchers += $w
}
$code = Invoke-Msi "/i `"$NewMsi`"" (Join-Path $env:TEMP 'purge_upgrade_new.log')
Start-Sleep -Seconds 2
$allEvents = Get-Event | Where-Object { $_.SourceIdentifier -like 'purge_lnk_*' } | Sort-Object TimeGenerated
# 診断用: 削除・作成イベントの発生時刻。
foreach ($e in $allEvents) { Write-Host ("  [diag] {0} {1:HH:mm:ss.fff} {2}" -f ($e.SourceIdentifier -replace '^purge_lnk_(\w+)_\d+$', '$1'), $e.TimeGenerated, $e.MessageData) }
# MSIはショートカットを上書きするとき、内部で一瞬だけ削除イベントが出る(NTFSは作成日時も引き継ぐ)。
# 実際に問題なのは「旧版を先に削除→新版が作る」間にショートカットが存在しない時間なので、
# 削除イベントから次の作成イベントまでの最大時間(ms)を測って判定する。
$gaps = @{}
foreach ($t in $targets) {
    $name = $t.Name
    $dels = @($allEvents | Where-Object { $_.SourceIdentifier -like 'purge_lnk_deleted_*' -and $_.MessageData -eq $name })
    $cres = @($allEvents | Where-Object { $_.SourceIdentifier -like 'purge_lnk_created_*' -and $_.MessageData -eq $name })
    $max = 0
    foreach ($d in $dels) {
        $next = $cres | Where-Object { $_.TimeGenerated -ge $d.TimeGenerated } | Select-Object -First 1
        $gap = if ($next) { ($next.TimeGenerated - $d.TimeGenerated).TotalMilliseconds } else { [double]::PositiveInfinity }
        if ($gap -gt $max) { $max = $gap }
    }
    $gaps[$name] = $max
    Write-Host ("  [diag] {0}: 存在しなかった最大時間={1}ms" -f $name, $max)
}
$newLog = Join-Path $env:TEMP 'purge_upgrade_new.log'
Write-Host '--- [diag] msiexec log ---'
Get-Content -LiteralPath $newLog -ErrorAction SilentlyContinue |
    Select-String -Pattern 'Shortcut|RemoveExistingProducts|InstallFinalize|InstallInitialize|Component: (Start|Desktop)' |
    Select-Object -First 80 | ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) }
Write-Host '--- [diag] end ---'

if ($code -ne 0) { $failures.Add("新版へのアップグレードが失敗(終了コード=$code)") }
foreach ($t in $targets) {
    $f = Join-Path $t.Dir 'Purge.lnk'
    if (-not (Test-Path $f)) { $failures.Add("$($t.Name)のショートカットがアップグレード後に存在しない"); continue }
    $after = (Get-Item $f).CreationTimeUtc.Ticks
    if ($after -ne $before[$t.Name]) { $failures.Add("$($t.Name)のショートカットが作り直された(作成日時が変化)") }
    if ($gaps[$t.Name] -gt 300) { $failures.Add("$($t.Name)のショートカットがアップグレード中に一時的に存在しなくなった(最大$($gaps[$t.Name])ms)") }
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
