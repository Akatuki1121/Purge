# Antigravity 拡張機能 設定変更レポート

## 1. 概要
VSCode の Antigravity 拡張機能において、通常のコマンド実行を自動化しつつ、破壊的・危険な操作はブロックし、変更適用前のレビューを必ず挟むよう設定を変更しました。

- **実施日時**: 2026-09-04
- **対象設定ファイル**: ~/.gemini/config/config.json
- **バックアップファイル**: ~/.gemini/config/config.json.bak

---

## 2. 変更内容一覧

| 設定項目 | 設定プロパティ | 変更前 | 変更後 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| **ターミナル自動実行** | utoExecutionPolicy | 未設定 (0: OFF / 毎回確認) | **3 (EAGER / Always Proceed)** | 通常のビルド・テスト・ファイル検索等のコマンドをAgentが自動実行します。 |
| **アーティファクトレビュー** | rtifactReviewMode | 未設定 (0: 自動判断等) | **1 (ALWAYS / Request Review)** | 実装計画やコード変更差分（diff）を適用する前に、必ずユーザー確認・承認を求めます。 |
| **コマンド拒否リスト** | deniedCommandsList | 未設定 / 空 | **29件の危険コマンドを登録** | 以下の危険コマンドが指定された場合は自動実行されず、必ず人間の確認・承認が入ります。 |

---

## 3. 拒否リスト（Deny list）に登録した危険コマンド

以下のコマンド（および前方一致パターン）は、Agentによる自動実行から完全に除外されています。

### ① ファイル・ディレクトリの強制・再帰削除
- m -rf
- mdir /s
- d /s
- del /f /s /q
- Remove-Item -Recurse -Force
- Remove-Item * -Recurse -Force

### ② Git の破壊的変更・上書き
- git reset --hard
- git push --force
- git push -f
- git clean -fd
- git clean -fdx
- git checkout -f
- git restore .

### ③ ディスク・ファイルシステムの操作
- Format-Volume
- Clear-Disk
- Initialize-Disk

### ④ システム停止・再起動
- shutdown
- Stop-Computer
- Restart-Computer

### ⑤ レジストリ・権限昇格・スクリプト実行ポリシー
- eg delete
- unas
- Set-ExecutionPolicy Bypass
- Set-ExecutionPolicy Unrestricted

### ⑥ 外部スクリプトの直接実行
- curl | bash
- curl | sh
- Invoke-Expression (Invoke-WebRequest
- iex (iwr

### ⑦ データベースの破壊的操作
- DROP DATABASE
- drop database

---

## 4. VSCode UI上での確認・調整手順

VSCode の GUI から現在の設定を確認したり、追加でコマンドを登録・解除したりすることも可能です。

1. **コマンドパレットを開く**:
   - Ctrl + Shift + P を押します。
2. **設定画面を開く**:
   - Antigravity: Open Antigravity Settings と入力して実行します（またはサイドバーの Antigravity アイコンから開きます）。
3. **設定の確認**:
   - **Terminal Command Auto Execution**: Always Proceed に設定されています。
   - **Artifact Review Policy**: Request Review（または Always Block）に設定されています。
   - **Deny list / Blacklist**: 登録されたコマンド一覧が表示されます。プロジェクトに合わせて追加・編集が可能です。
