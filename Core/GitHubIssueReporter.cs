using System;
using System.Diagnostics;

namespace Purge
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
        private const string RepositoryUrl = "https://github.com/Akatuki1121/Purge";

        /// <summary>
        /// GitHubのURL長制限(実用上、ブラウザ・サーバー双方で安全な範囲)を超えないよう、
        /// 完成したURL全体をこの文字数以下に収める。
        /// 日本語はURLエンコードで1文字が最大9文字(%XX×3バイト)に膨れ上がるため、
        /// bodyの生文字数ではなくエンコード後のURL長で判定しないと簡単に超過する
        /// (実際に発生した不具合: Issue #29)。
        /// </summary>
        private const int MaxUrlLength = 7600;

        public static void OpenIssueWithReport(string errorReport)
        {
            OpenIssue("[自動生成] エラー報告", errorReport, "bug,auto-report");
        }

        public static void OpenIssue(string title, string body, string labels = "bug")
        {
            var encodedTitle = Uri.EscapeDataString(title);
            var encodedLabels = Uri.EscapeDataString(labels);
            var baseUrl = $"{RepositoryUrl}/issues/new?title={encodedTitle}&body=";
            var suffix = $"&labels={encodedLabels}";

            const string truncationNotice =
                "\n\n(レポートが長いため一部省略されました。全文が必要な場合はアプリ内の「コピー」ボタンをお使いください。)";

            var encodedBody = Uri.EscapeDataString(body);
            int overflow = (baseUrl.Length + encodedBody.Length + suffix.Length) - MaxUrlLength;

            if (overflow > 0)
            {
                // エンコード後の超過分だけを元にbodyを切り詰めたいが、1文字あたりの
                // エンコード後サイズは文字種によって変わる(ASCII=そのまま、日本語=最大9文字)ため、
                // 正確な逆算はできない。安全側に倒し、超過分の1/3の文字数を目安に切り詰めてから
                // 再エンコードして確認する、を収まるまで繰り返す。
                var truncatedBody = body;
                while (true)
                {
                    int cut = System.Math.Max(1, overflow / 3 + 50);
                    if (cut >= truncatedBody.Length)
                    {
                        truncatedBody = string.Empty;
                        break;
                    }

                    truncatedBody = truncatedBody[..^cut];
                    var candidateEncoded = Uri.EscapeDataString(truncatedBody + truncationNotice);
                    overflow = (baseUrl.Length + candidateEncoded.Length + suffix.Length) - MaxUrlLength;

                    if (overflow <= 0)
                    {
                        encodedBody = candidateEncoded;
                        break;
                    }
                }
            }

            var url = baseUrl + encodedBody + suffix;

            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            };

            Process.Start(psi);
        }
    }
}
