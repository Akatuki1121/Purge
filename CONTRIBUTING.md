# Purge へのコントリビュート

## 開発環境のセットアップ

1. リポジトリをクローンする
2. .NET 10 SDK をインストールする
3. pre-commit を導入する(秘密情報の流出防止、#109):

```bash
pip install pre-commit
pre-commit install
```

以後、`git commit` のたびに gitleaks による秘密情報の検査が走ります。
トークン・APIキー・証明書・秘密鍵などを含むコミットは、この時点で失敗します。

## 秘密情報をうっかりコミットした場合

- **未pushの履歴の場合**: `git commit --amend` または interactive rebase で、秘密情報を含むコミット自体を書き換える(削除コミットの追加では履歴に残り続けるため不可)
- **push済みの場合**: まず対象の資格情報を失効・ローテーションし、その後 `git filter-repo` で全履歴から除去する

## コミット前のローカル検証

```bash
dotnet build
dotnet test
```

## CIについて

- `CI`: ビルド・単体テスト、およびMSI検証(変更対象に応じて)
- `CodeQL` / `gitleaks`: 静的解析・秘密情報検出(push/PR)
- `Verify MSI Upgrade Matrix`: インストーラー・更新機能の変更があるPRでMSI更新互換性を検証
- `Verify Self-Uninstall` / `Verify Service/Task Restore`: それぞれの変更があるPRで実行
