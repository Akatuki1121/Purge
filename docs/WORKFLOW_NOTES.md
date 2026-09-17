# Purge開発 作業手順ノウハウ

Claude(Desktop Commander経由でこのPCを操作するエージェント)がPurgeのIssue対応・PRマージ・リリースを行う際の手順とノウハウ。試行錯誤の結果まとまったものなので、以後はこれをベースに作業する。

## 基本フロー(Issue対応〜マージ)

1. `gh issue list --repo Akatuki1121/Purge --state open` でIssue一覧を確認
2. **本文だけでなくコメントも必ず確認する**(`gh issue view <n> --json title,body,comments`)。
   本文が空でコメントに要件が書かれているIssueがあった。本文しか見ずに対応してクローズし、
   後から指摘されたことがある(#23の教訓)。
3. 該当ブランチを作成 (`fix/issue-<n>-<slug>` の命名)
4. 実装
5. `dotnet build` → `dotnet test --no-build`(後述)で確認
6. コミット・push・`gh pr create`
7. CI通過を待って `gh pr merge <n> --squash --delete-branch`
8. ローカルを `git checkout main && git pull` で最新化、使い終わったブランチを削除


## ビルド・テストが詰まる問題と対策

このセッションで繰り返し発生した問題: `dotnet build` / `dotnet test` を実行すると、
プロセスは生きているのに `read_process_output` が何十秒待っても無音のまま、という
ハングが何度も起きた。原因はほぼ毎回同じで、**前回実行分のMSBuildノード(dotnet.exeの
常駐プロセス)が残ったままになっていて、新しいビルド/テストがそのノードの空き待ちで
詰まっている**というもの。

### 対策

- 怪しいと思ったら早めに確認する:
  ```
  Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' } | Select-Object ProcessId, CommandLine
  ```
  複数のdotnet.exeが残っていて、かつ今実行中のはずのコマンドの出力が長時間無い場合は
  ほぼ確実にこれが原因。
- 詰まっていたら全部殺してやり直す:
  ```
  Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force
  ```
  (`dotnet build-server shutdown` は効かないことがあったので、Stop-Processで直接殺す方が確実)
- `read_process_output` は短い timeout を何度も刻むより、**最初から120秒程度の長めの
  timeout_ms で1回待つ**方が結果的に速い。細切れに呼び直すと「あと数秒で終わるところを
  毎回タイムアウトで打ち切って仕切り直す」を繰り返してしまう。
- 60秒待っても無音なら、そこで見切りをつけて `force_terminate` → プロセス確認 →
  クリーンな状態で再実行、に切り替える。同じ待ちを3〜4回繰り返さない。


## dotnet test は build と二重作業になりやすい

`dotnet build` の直後に素の `dotnet test` を実行すると、test コマンドが内部でもう一度
復元・最新性チェックをやり直すため、実行時間が build 単体よりむしろ長くなることがある
(実測: build 7.7秒 → test 15.3秒、うち実テスト実行はわずか0.5秒程度)。

**対策**: ビルド直後にテストする流れでは `dotnet build` → `dotnet test --no-build` の
組み合わせを使う。直前のビルド成果物をそのまま使うため二重チェックをスキップでき、
体感でほぼ半分の時間になる(実測15.3秒→7.8秒)。

ビルドを挟まず単独でテストだけ実行したい場合は通常の `dotnet test` のままでよい。

## PowerShellコンソールの文字化けについて

`Select-String` や `Get-Content` 等の出力で日本語が頻繁に文字化けする
(例: 「ビルドに成功しました」が「ビルドに成功しました、E」のように表示される)。
これはコンソールのコードページ設定の問題で実害はない([Desktop Commander経由で
実行している]PowerShellの表示上の問題であり、ファイルの中身やGit上の内容は正しい
UTF-8のまま)。`exit code` や `成功`/`エラー` の文字列マッチだけで判断すれば、
文字化けした部分は無視してよい。


## Issueのコメントは必ず見る

`gh issue view <n>` はデフォルトでコメントを表示しないことがあるため、
`--json title,body,comments` や `--comments` を明示的に付けて確認する。
本文が空でコメントに実要件が書かれているIssueがあった(#23)。本文だけを見て
「対応完了」と判断してクローズし、後から見落としを指摘されたことがある。
**Issue対応前とクローズ前の両方で、コメント欄まで含めて再確認する。**

## WiXインストーラーのハマりどころ

- `GridViewColumn` は `FrameworkElement` を継承しておらず `Tag` プロパティを
  XAMLでは持てない。列ごとのメタデータ(比率等)を持たせたい場合はコードビハインド側で
  列インデックス順に配列で渡す方式にする。
- WiX SDKスタイルのプロジェクトで `<Cultures>ja-JP;en-US</Cultures>` のように
  複数カルチャを指定すると、**1つのMSIが自動で言語切り替えするのではなく、
  言語ごとに別々のMSIファイルが生成される**(`bin\x64\Release\ja-JP\*.msi` /
  `en-US\*.msi`)。単一MSIでOS言語に自動追従させたい場合は `light.exe` の
  低レベルな多言語トランスフォーム機能が必要で、SDKスタイルでは素直にサポートされない。
- `Advertise="yes"` のショートカットは、そのコンポーネントのKeyPathが「File」で
  あることが必須。レジストリ値をKeyPathにした条件付きコンポーネント
  (オプションのショートカット等)では `Advertise="no"` + `Target="[#FileId]"` の
  明示指定にする必要がある。
- 上記の構成(常時必須のexeへの非advertisedショートカット + レジストリKeyPath)は
  ICE43 / ICE57 / ICE69 の検証に引っかかるが、ターゲットのファイルが常にインストール
  される設計であれば実害のない既知の誤検知なので、`<SuppressIces>` で無視してよい。
- `WixLocalization` の `Codepage` 属性はMSIのサマリー情報用で、**ANSIコードページのみ
  有効**(日本語は932、UTF-8の65001は不可)。
- Windows Installerエンジン自体が持つ組み込み文言(「Please wait while Windows
  configures ...」等)は、WixUIの `Cultures` 設定とは別に `Package` 要素の
  `Language` 属性で言語が決まる。`Cultures=ja-JP` を設定しても、`Language` を
  明示しないとこの部分だけ英語のままになる。


## リリースタイミングの指標

- **致命的バグ(機能が完全に動かない系)は即リリース**。例: 決済URLの誤記(#21)、
  アプリ内バグ報告機能が動かない(#29)。ユーザーの実害・機会損失に直結するため
  他の作業完了を待たない。
- **ユーザー影響の大きいUI改善**は、ある程度まとまったタイミングでリリースに含める。
- **実機で未検証の変更**(特にインストーラー周り)が残っている間はリリースしない。
  一度も実機確認していない状態で配布するとリスクが高い。
- 目安: 致命的バグは即リリースの単独パッチ、それ以外は週1回など定期リリースに
  まとめる、という2段階の運用が扱いやすい。

## GitHub Issue本文のURL長制限について

アプリ内の「不具合・要望」からGitHub Issueの事前入力URLを作る機能で、
本文の生文字数だけで切り詰め判定していたためにURL長超過エラーが発生した(#29)。
**日本語はURLエンコードで1文字が最大9文字(%XX×3バイト)に膨れ上がる**ため、
本文の生文字数ではなく「エンコード後の完成URL全体の長さ」で判定する必要がある。
`Core/GitHubIssueReporter.cs` の実装を参照。
