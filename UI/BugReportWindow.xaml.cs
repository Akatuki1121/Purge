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
            AttachedFileText.Text = Loc.F("Bug_Attached", _attachedScreenshotFileName);
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
        report.AppendLine(isFeatureRequest ? Loc.T("Report_FeatureSummary") : Loc.T("Report_BugSummary"));
        report.AppendLine(SummaryText.Text.Trim());
        report.AppendLine();
        report.AppendLine(isFeatureRequest ? Loc.T("Report_FeatureContext") : Loc.T("Report_Steps"));
        report.AppendLine(string.IsNullOrWhiteSpace(StepsText.Text) ? Loc.T("Report_NotFilled") : StepsText.Text.Trim());
        report.AppendLine();
        report.AppendLine(isFeatureRequest ? Loc.T("Report_FeatureWanted") : Loc.T("Report_Expected"));
        report.AppendLine(string.IsNullOrWhiteSpace(ExpectedText.Text) ? Loc.T("Report_NotFilled") : ExpectedText.Text.Trim());
        report.AppendLine();
        report.AppendLine(Loc.T("Report_Actual"));
        report.AppendLine(string.IsNullOrWhiteSpace(ActualText.Text) ? Loc.T("Report_NotFilled") : ActualText.Text.Trim());
        report.AppendLine();
        report.AppendLine(Loc.T("Report_Environment"));
        report.AppendLine($"- OS: {Environment.OSVersion}");
        report.AppendLine($"- .NET: {Environment.Version}");

        if (IncludeLogCheckBox.IsChecked == true)
        {
            report.AppendLine();
            report.AppendLine(Loc.T("Report_RecentLog"));
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