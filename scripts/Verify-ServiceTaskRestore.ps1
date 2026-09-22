# サービス・タスク定義の自動復元(#19)の実環境検証(CI用)。
# テスト用のWindowsサービスとスケジュールタスクを実際に作成し、その定義を
# sc.exe / schtasks.exe で取得(RemovalBackupWriterと同じ形式)、削除した上で、
# RemovalRestoreService.Restore が実際にサービス/タスクを再登録できるかを検証する。
#
# 単体テストはテキスト解析(ExtractValue等)までで、「本当にWindowsに再登録されるか」は
# 見ていなかったため、それをリリース前に確認するためのもの。
#
# 使い方: pwsh -File scripts/Verify-ServiceTaskRestore.ps1 -CoreDllPath <path>
# 管理者権限で実行する(sc.exe create/delete に必要)。
param(
    [Parameter(Mandatory)][string]$CoreDllPath
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$script:failures = New-Object System.Collections.Generic.List[string]

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "  [OK]  $name" }
    else {
        Write-Host "  [NG]  $name $detail"
        $script:failures.Add("$name $detail".Trim())
    }
}

if (-not (Test-Path -LiteralPath $CoreDllPath)) {
    Write-Host "Purge.Core.dll が見つかりません: $CoreDllPath"
    exit 2
}
$CoreDllPath = (Resolve-Path -LiteralPath $CoreDllPath).ProviderPath

$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$serviceName = "PurgeVerifySvc_$suffix"
$taskName = "PurgeVerifyTask_$suffix"
$workDir = Join-Path $env:TEMP "purge_restore_verify_$suffix"
New-Item $workDir -ItemType Directory -Force | Out-Null

Write-Host "=== 1. テスト用サービス・タスクを作成 ==="
& sc.exe create $serviceName binPath= "C:\Windows\System32\cmd.exe" start= demand DisplayName= "Purge Verify Service" | Out-Null
Check 'テストサービスを作成できた' ($LASTEXITCODE -eq 0)
& schtasks.exe /Create /TN $taskName /TR "cmd.exe" /SC ONCE /ST 23:59 /F | Out-Null
Check 'テストタスクを作成できた' ($LASTEXITCODE -eq 0)

Write-Host "=== 2. 削除前の定義を取得(RemovalBackupWriterと同じ形式) ==="
$serviceDefinition = & sc.exe qc $serviceName | Out-String
$taskDefinition = & schtasks.exe /Query /TN $taskName /XML | Out-String
Check 'サービス定義を取得できた' ($serviceDefinition -match 'BINARY_PATH_NAME')
Check 'タスク定義を取得できた' ($taskDefinition -match '<Task')

Write-Host "=== 3. 削除 ==="
& sc.exe delete $serviceName | Out-Null
Check 'サービスを削除できた' ($LASTEXITCODE -eq 0)
& schtasks.exe /Delete /TN $taskName /F | Out-Null
Check 'タスクを削除できた' ($LASTEXITCODE -eq 0)
Start-Sleep -Seconds 1
$serviceGoneAfterDelete = -not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)
$taskGoneAfterDelete = -not (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)
Check '削除直後にサービスが存在しない(前提の確認)' $serviceGoneAfterDelete
Check '削除直後にタスクが存在しない(前提の確認)' $taskGoneAfterDelete

Write-Host "=== 4. RemovalRestoreService.Restore でマニフェストから復元 ==="
Add-Type -Path $CoreDllPath
$manifest = [pscustomobject]@{
    Entries = @(
        [pscustomobject]@{ Category = 'Service'; Location = $serviceName; DefinitionSnapshot = $serviceDefinition },
        [pscustomobject]@{ Category = 'ScheduledTask'; Location = $taskName; DefinitionSnapshot = $taskDefinition }
    )
}
$manifestPath = Join-Path $workDir 'manifest.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content $manifestPath -Encoding UTF8

$log = New-Object Purge.OperationLog -ArgumentList 500
$restoreService = New-Object Purge.RemovalRestoreService -ArgumentList $log
$restoredCount = $restoreService.Restore($manifestPath)
Check '2件とも復元処理が成功と報告された' ($restoredCount -eq 2) "実際=$restoredCount"

Write-Host "=== 5. 実際にWindowsへ再登録されたか検証 ==="
Start-Sleep -Seconds 1
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
Check 'サービスが実際に再登録された' ($null -ne $svc) "取得結果=$svc"
if ($svc) {
    $wmiSvc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    Check '再登録されたサービスのパスが元と一致する' ($wmiSvc.PathName -match 'cmd\.exe') "実際=$($wmiSvc.PathName)"
}
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
Check 'タスクが実際に再登録された' ($null -ne $task)
if ($task) {
    $action = $task.Actions | Select-Object -First 1
    Check '再登録されたタスクのコマンドが元と一致する' ($action.Execute -match 'cmd\.exe') "実際=$($action.Execute)"
}

Write-Host "=== 6. 後片付け ==="
& sc.exe delete $serviceName | Out-Null
& schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "  テスト用のサービス・タスクを削除しました"

Write-Host ''
if ($script:failures.Count -eq 0) {
    Write-Host '全ての検証に合格しました。'
    exit 0
}
Write-Host "検証に失敗しました($($script:failures.Count)件):"
$script:failures | ForEach-Object { Write-Host "  - $_" }
exit 1
