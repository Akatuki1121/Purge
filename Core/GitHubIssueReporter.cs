using System;
using System.Diagnostics;

namespace UninstallTool
{
    /// <summary>
    /// GitHub Issue作成ページを、エラーレポート内容を事前入力した状態でブラウザに開く。
    ///
    /// アプリ側にGitHubの認証トークンを埋め込む方式(自動投稿)は、配布したアプリから誰でも
    /// トークンを取り出して開発者のGitHubアカウントを不正利用できてしまうため、
    /// 他人に配布するツールでは絶対に採用しない。代わりに、各ユーザー自身のGitHubアカウントで
    /// ログインした状態でIssueを作ってもらう「事前入力ページを開くだけ」の方式を使う。
    /// </summary>
    public static class GitHubIssueReporter
    {
        private const string RepositoryUrl = "https://github.com/Akatuki1121/UninstallTool";

        /// <summary>
        /// GitHubのURL長制限(実用上安全な範囲)を超えないよう、bodyをこの文字数で切り詰める。
        /// 超過した場合は「コピーして続きを貼り付けてください」という案内を末尾に付ける。
        /// </summary>
        private const int MaxBodyLength = 6000;

        public static void OpenIssueWithReport(string errorReport)
        {
            OpenIssue("[自動生成] エラー報告", errorReport, "bug,auto-report");
        }

        public static void OpenIssue(string title, string body, string labels = "bug")
        {
            var encodedTitle = Uri.EscapeDataString(title);

            bool truncated = false;
            if (body.Length > MaxBodyLength)
            {
                body = body[..MaxBodyLength];
                truncated = true;
            }

            if (truncated)
            {
                body += "\n\n(レポートが長いため一部省略されました。全文が必要な場合はアプリ内の「コピー」ボタンをお使いください。)";
            }

            var encodedBody = Uri.EscapeDataString(body);
            var url = $"{RepositoryUrl}/issues/new?title={encodedTitle}&body={encodedBody}&labels={Uri.EscapeDataString(labels)}";

            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            };

            Process.Start(psi);
        }
    }
}
