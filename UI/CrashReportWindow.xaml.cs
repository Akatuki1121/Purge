using System.Windows;
using Purge;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace Purge.UI
{
    /// <summary>
    /// 未処理例外/致命的でないエラーの両方から使う共通のエラー報告ダイアログ。
    /// OperationLog.BuildErrorReport() が生成した「直前の操作の流れ + 例外詳細」の文脈付きレポートを
    /// そのまま表示し、ワンクリックでコピーできるようにする。
    ///
    /// これは元々「ツール自身が今何をしていたかを理解した状態でエラーを説明する」機能として
    /// エンジン(OperationLog.BuildErrorReport)は先に実装済みだったが、専用の表示・コピー導線が
    /// 抜けていたため、この画面で初めてユーザーが直接使える形になる。
    /// </summary>
    public partial class CrashReportWindow : FluentWindow
    {
        public CrashReportWindow(string report)
        {
            InitializeComponent();
            ReportText.Text = report;
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ReportText.Text)) return;

            Clipboard.SetText(ReportText.Text);
            CopyButton.Content = "コピーしました";

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = System.TimeSpan.FromSeconds(1.5),
            };
            timer.Tick += (_, _) =>
            {
                CopyButton.Content = "コピー";
                timer.Stop();
            };
            timer.Start();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// エラーレポートを事前入力した状態でGitHub Issue作成ページを既定ブラウザで開く。
        /// GitHubへのログインは各ユーザー自身のアカウントで行われる(トークンをアプリに埋め込まない設計)。
        /// </summary>
        private void ReportOnGitHubButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                GitHubIssueReporter.OpenIssueWithReport(ReportText.Text);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(
                    $"ブラウザを開けませんでした。お手数ですが「コピー」ボタンで内容をコピーし、\n{ex.Message}\n\n手動でGitHubのIssueページに貼り付けてください。",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
