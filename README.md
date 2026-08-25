# UninstallTool

Geek Uninstallerを超えることを目指した、Windows向けアンインストーラーです。
通常のアンインストールに加え、レジストリ・サービス・タスクスケジューラ・PATH環境変数・
スタートアップ項目まで横断的にスキャンし、残存物を検出・削除できます。

## 主な機能

- インストール済みアプリの一覧表示・アンインストール実行
- USN Journal(MFT)を使った自前実装の高速ファイル検索
- 残存レジストリキー・サービス・タスクスケジューラ・PATH・スタートアップ項目の横断スキャン
- 孤児候補検出(既にアンインストール済みなのに残っているフォルダの検出、実験的機能)
- 操作ログの自動記録によるエラー報告支援(GitHub Issueへのワンクリック報告)
- ドライラン(実際には削除しない確認モード)対応

## 技術スタック

- .NET (C#) / WPF
- [WPF UI](https://github.com/lepoco/wpfui)(Fluent Design、MITライセンス)
- Win32 API(USN Journal、レジストリ操作)

## プロジェクト構成

- `Core/` — ロジック本体(アプリ一覧取得、アンインストール実行、MFT検索、残存物スキャン等)
- `UI/` — WPFアプリケーション本体
- ルート直下 — 開発時の動作確認用コンソールランナー

## セットアップ(開発者向け)

1. .NET SDKをインストール
2. `dotnet build` でビルド
3. `UI/bin/Debug/net*/UninstallTool.UI.exe` を管理者権限で実行

管理者権限が必須です(レジストリの一部書き込み・USN Journalアクセスのため)。

## ライセンス

このプロジェクトは [WPF UI](https://github.com/lepoco/wpfui) を利用しています。
詳細は [THIRD_PARTY_LICENSES.md](./THIRD_PARTY_LICENSES.md) を参照してください。
