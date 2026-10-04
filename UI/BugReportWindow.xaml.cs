using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Purge;
using Purge.Localization;
using Wpf.Ui.Controls;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace Purge.UI;

public partial class BugReportWindow : FluentWindow
{
    private readonly OperationLog _log;
    private string? _attachedScreenshotFileName;

    public BugReportWindow(OperationLog log)
    {
        InitializeComponent();
        _log = log;
    }

    private void AttachScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Dlg_ScreenshotTitle"),
            Filter = Loc.T("Dlg_ImageFilter"),
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(dialog.FileName);
            bitmap.EndInit();

            Clipboard.SetImage(bitmap);
            _attachedScreenshotFileName = Path.GetFileName(dialog.FileName);
            AttachedFileText.Text = $"「{_attachedScreenshotFileName}」をクリップボードにコピーしました";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"画像を読み込めませんでした。\n{ex.Message}", Loc.T("Common_Error"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReportButton_Click(object sender, RoutedEventArgs e)
    {
        var summary = SummaryText.Text.Trim();
        if (string.IsNullOrEmpty(summary))
        {
            MessageBox.Show(Loc.T("Msg_SummaryRequired"), Loc.T("Title_InputRequired"), MessageBoxButton.OK, MessageBoxImage.Information);
            SummaryText.Focus();
            return;
        }

        try
        {
            var isFeatureRequest = ReportTypeComboBox.SelectedIndex == 1;
            var prefix = isFeatureRequest ? Loc.T("Report_PrefixFeature") : Loc.T("Report_PrefixBug");
            var labels = isFeatureRequest ? "enhancement,user-request" : "bug,user-report";
            GitHubIssueReporter.OpenIssue($"{prefix} {summary}", BuildReport(), labels);

            if (_attachedScreenshotFileName != null)
            {
                MessageBox.Show(
                    Loc.T("Msg_PasteScreenshot"),
                    Loc.T("Title_PasteScreenshot"), MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ブラウザを開けませんでした。\n{ex.Message}", Loc.T("Common_Error"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string BuildReport()
    {
        var report = new StringBuilder();
        var isFeatureRequest = ReportTypeComboBox.SelectedIndex == 1;
        report.AppendLine(isFeatureRequest ? "## 要望の概要" : "## 不具合の概要");
        report.AppendLine(SummaryText.Text.Trim());
        report.AppendLine();
        report.AppendLine(isFeatureRequest ? "## 解決したい課題・利用場面" : "## 再現手順");
        report.AppendLine(string.IsNullOrWhiteSpace(StepsText.Text) ? Loc.T("Report_NotFilled") : StepsText.Text.Trim());
        report.AppendLine();
        report.AppendLine(isFeatureRequest ? "## 希望する動作・機能" : "## 期待する動作");
        report.AppendLine(string.IsNullOrWhiteSpace(ExpectedText.Text) ? Loc.T("Report_NotFilled") : ExpectedText.Text.Trim());
        report.AppendLine();
        report.AppendLine("## 実際の動作・エラーメッセージ");
        report.AppendLine(string.IsNullOrWhiteSpace(ActualText.Text) ? Loc.T("Report_NotFilled") : ActualText.Text.Trim());
        report.AppendLine();
        report.AppendLine("## 環境");
        report.AppendLine($"- OS: {Environment.OSVersion}");
        report.AppendLine($"- .NET: {Environment.Version}");

        if (IncludeLogCheckBox.IsChecked == true)
        {
            report.AppendLine();
            report.AppendLine("## 直近の操作ログ");
            report.AppendLine("```");
            report.AppendLine(string.Join(Environment.NewLine, _log.GetRecent(30)));
            report.AppendLine("```");
        }

        return report.ToString();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}