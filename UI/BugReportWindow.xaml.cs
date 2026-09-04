using System;
using System.Text;
using System.Windows;
using UninstallTool;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace UninstallTool.UI;

public partial class BugReportWindow : FluentWindow
{
    private readonly OperationLog _log;

    public BugReportWindow(OperationLog log)
    {
        InitializeComponent();
        _log = log;
    }

    private void ReportButton_Click(object sender, RoutedEventArgs e)
    {
        var summary = SummaryText.Text.Trim();
        if (string.IsNullOrEmpty(summary))
        {
            MessageBox.Show("概要を入力してください。", "入力不足", MessageBoxButton.OK, MessageBoxImage.Information);
            SummaryText.Focus();
            return;
        }

        try
        {
            var isFeatureRequest = ReportTypeComboBox.SelectedIndex == 1;
            var prefix = isFeatureRequest ? "[要望]" : "[不具合]";
            var labels = isFeatureRequest ? "enhancement,user-request" : "bug,user-report";
            GitHubIssueReporter.OpenIssue($"{prefix} {summary}", BuildReport(), labels);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ブラウザを開けませんでした。\n{ex.Message}", "エラー",
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
        report.AppendLine(string.IsNullOrWhiteSpace(StepsText.Text) ? "(未記入)" : StepsText.Text.Trim());
        report.AppendLine();
        report.AppendLine(isFeatureRequest ? "## 希望する動作・機能" : "## 期待する動作");
        report.AppendLine(string.IsNullOrWhiteSpace(ExpectedText.Text) ? "(未記入)" : ExpectedText.Text.Trim());
        report.AppendLine();
        report.AppendLine("## 実際の動作・エラーメッセージ");
        report.AppendLine(string.IsNullOrWhiteSpace(ActualText.Text) ? "(未記入)" : ActualText.Text.Trim());
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