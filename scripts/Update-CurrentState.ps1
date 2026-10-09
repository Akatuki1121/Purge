# docs\CURRENT_STATE.md の自動更新部分(<!-- AUTO:BEGIN --> ～ <!-- AUTO:END -->)を、
# 現在のブランチ・最新リリース・未マージPR・オープンIssue・直近コミットで書き換える。
# git hook(.githooks\)から、git操作のたびにバックグラウンドで呼ばれる。手動実行も可。
#
# 方針:
# - git操作の邪魔をしない。失敗しても何もせず終了コード0で終わる。
# - 手書き部分(マーカーの外)には触れない。マーカーが無ければ何もしない。
# - gh が使えない(オフライン等)ときは、PR/Issueの節だけ前回の内容を残す。
# - 同時に複数起動されても壊れないよう、Mutexで直列化し、一時ファイル経由で置き換える。

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8
$env:GH_PROMPT_DISABLED = '1'

$BeginMarker = '<!-- AUTO:BEGIN -->'
$EndMarker = '<!-- AUTO:END -->'
$RemoteBegin = '<!-- AUTO:REMOTE:BEGIN -->'
$RemoteEnd = '<!-- AUTO:REMOTE:END -->'
$MutexName = 'Global\PurgeCurrentStateUpdate'
$MutexWaitMs = 30000
$RecentCommitCount = 8

function Get-Section([string]$Text, [string]$Begin, [string]$End) {
    $b = $Text.IndexOf($Begin)
    $e = $Text.IndexOf($End)
    if ($b -lt 0 -or $e -lt 0 -or $e -lt $b) { return $null }
    return @{ Start = $b + $Begin.Length; End = $e }
}

function Get-CiState($rollup) {
    if (-not $rollup -or $rollup.Count -eq 0) { return '-' }
    $states = $rollup | ForEach-Object { if ($_.conclusion) { $_.conclusion } else { $_.status } }
    if ($states -contains 'FAILURE') { return '失敗' }
    if ($states | Where-Object { $_ -in 'IN_PROGRESS', 'QUEUED', 'PENDING', 'WAITING', '' }) { return '実行中' }
    return '成功'
}

function Get-RemoteSectionCore {
    $prs = gh pr list --state open --json number,title,headRefName,statusCheckRollup 2>$null | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { return $null }
    $issues = gh issue list --state open --limit 50 --json number,title,labels 2>$null | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { return $null }

    $sb = New-Object Text.StringBuilder
    [void]$sb.AppendLine('### 未マージのPR')
    [void]$sb.AppendLine('')
    if (-not $prs -or @($prs).Count -eq 0) {
        [void]$sb.AppendLine('なし')
    }
    else {
        [void]$sb.AppendLine('| # | タイトル | ブランチ | CI |')
        [void]$sb.AppendLine('|---|---|---|---|')
        foreach ($p in $prs) {
            [void]$sb.AppendLine("| $($p.number) | $($p.title) | $($p.headRefName) | $(Get-CiState $p.statusCheckRollup) |")
        }
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('### オープンIssue')
    [void]$sb.AppendLine('')
    if (-not $issues -or @($issues).Count -eq 0) {
        [void]$sb.AppendLine('なし')
    }
    else {
        [void]$sb.AppendLine('| # | タイトル | ラベル |')
        [void]$sb.AppendLine('|---|---|---|')
        foreach ($i in $issues) {
            $labels = ($i.labels | ForEach-Object { $_.name }) -join ', '
            [void]$sb.AppendLine("| $($i.number) | $($i.title) | $labels |")
        }
    }
    return $sb.ToString().TrimEnd()
}

function Get-RemoteSection {
    try { return Get-RemoteSectionCore } catch { return $null }
}

$mutex = New-Object Threading.Mutex($false, $MutexName)
$acquired = $false
try {
    $acquired = $mutex.WaitOne($MutexWaitMs)
    if (-not $acquired) { exit 0 }

    $root = (git rev-parse --show-toplevel 2>$null)
    if (-not $root) { exit 0 }
    $statePath = Join-Path $root 'docs\CURRENT_STATE.md'
    if (-not (Test-Path $statePath)) { exit 0 }

    $bytes = [IO.File]::ReadAllBytes($statePath)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [IO.File]::ReadAllText($statePath)
    $crlf = $text.Contains("`r`n")
    $text = $text.Replace("`r`n", "`n")

    $auto = Get-Section $text $BeginMarker $EndMarker
    if ($null -eq $auto) { exit 0 }

    $oldBlock = $text.Substring($auto.Start, $auto.End - $auto.Start)
    $oldRemote = Get-Section $oldBlock $RemoteBegin $RemoteEnd
    $remote = Get-RemoteSection
    if ($null -eq $remote) {
        # 取得できなかったときは前回の内容を残す(無ければ注記だけ)。
        if ($null -ne $oldRemote) {
            $remote = $oldBlock.Substring($oldRemote.Start, $oldRemote.End - $oldRemote.Start).Trim()
        }
        else {
            $remote = '(PR・Issueは取得できませんでした。gh が使えない状態です)'
        }
    }

    $branch = (git branch --show-current 2>$null)
    if (-not $branch) { $branch = '(detached HEAD)' }
    $tag = (git describe --tags --abbrev=0 2>$null)
    if (-not $tag) { $tag = '(タグなし)' }
    $dirty = @(git status --porcelain 2>$null | Where-Object { $_ -notmatch '^\?\? docs/' }).Count
    $commits = git log --oneline -n $RecentCommitCount 2>$null | ForEach-Object { "- $_" }
    $now = Get-Date -Format 'yyyy-MM-dd HH:mm'

    $block = New-Object Text.StringBuilder
    [void]$block.AppendLine('')
    [void]$block.AppendLine("最終更新: $now(git操作のたびに scripts\Update-CurrentState.ps1 が書き換える。この枠の中は手で編集しない)")
    [void]$block.AppendLine('')
    [void]$block.AppendLine("- 現在のブランチ: ``$branch``")
    [void]$block.AppendLine("- 最新のリリースタグ: ``$tag``")
    [void]$block.AppendLine("- 未コミットの変更: $dirty 件(docs\ を除く)")
    [void]$block.AppendLine('')
    [void]$block.AppendLine($RemoteBegin)
    [void]$block.AppendLine($remote)
    [void]$block.AppendLine($RemoteEnd)
    [void]$block.AppendLine('')
    [void]$block.AppendLine('### 直近のコミット(このブランチ)')
    [void]$block.AppendLine('')
    foreach ($c in $commits) { [void]$block.AppendLine($c) }

    $newText = $text.Substring(0, $auto.Start) + $block.ToString().Replace("`r`n", "`n") + $text.Substring($auto.End)
    if ($crlf) { $newText = $newText.Replace("`n", "`r`n") }

    $tmp = "$statePath.tmp"
    [IO.File]::WriteAllText($tmp, $newText, (New-Object Text.UTF8Encoding($hasBom)))
    Move-Item -LiteralPath $tmp -Destination $statePath -Force
}
catch {
    # git操作を妨げない。原因調査用に1行だけ残す。
    try {
        $logPath = Join-Path $env:TEMP 'purge-current-state-hook.log'
        "$(Get-Date -Format s) $($_.Exception.Message)" | Out-File -Append -Encoding utf8 $logPath
    }
    catch { }
}
finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
exit 0
