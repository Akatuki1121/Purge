using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Purge.Localization;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace Purge.UI
{
    /// <summary>
    /// 残存物スキャン結果を一覧表示し、選択した項目のみ削除する画面。
    /// 選択はチェックボックス列ではなく、ListView標準の行選択(Ctrl/Shiftで複数選択可)を使う。
    /// </summary>
    public partial class ResidueWindow : FluentWindow
    {
        private readonly OperationLog _log;
        private readonly ResidueRemover _remover;
        private readonly RemovalBackupWriter _backupWriter;
        private readonly List<SelectableResidueItem> _items;
        private readonly string _appName;

        public ResidueWindow(string appName, List<ResidueItem> residueItems, OperationLog log)
        {
            InitializeComponent();
            _appName = appName;
            _log = log;
            _remover = new ResidueRemover(_log);
            _backupWriter = new RemovalBackupWriter(_log);

            _items = residueItems.Select(i => new SelectableResidueItem(i)).ToList();
            ResidueListView.ItemsSource = _items;

            // 列幅をウィンドウ幅に追従させ、横ホイールでの横スクロールも有効化する。
            // 列順: 種別/確度/場所/検出根拠。
            GridViewColumnSizer.AttachAutoSize(ResidueListView,
                new double?[] { 13, 7, 48, 32 });
            HorizontalScrollSupport.AttachToWindow(this);
            HorizontalScrollSupport.AttachShiftWheel(ResidueListView);
            TitleText.Text = Loc.F("Residue_Title", appName, _items.Count);
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            ResidueListView.SelectAll();
        }

        private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            ResidueListView.UnselectAll();
        }

        /// <summary>
        /// ドライラン切り替え時、色付きバッジで安全モードか実行モードかを明示する。
        /// XAMLでToggleSwitchにIsChecked="True"を指定しているとInitializeComponent()実行中に
        /// Checkedイベントが発火し、その時点ではDryRunStatusBadge等がまだnullなためnullガード必須
        /// (MainWindowで実際に発生した起動時クラッシュと同種の問題)。
        /// </summary>
        private void DryRunToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (DryRunStatusBadge == null || DryRunStatusText == null)
            {
                return;
            }

            bool dryRun = DryRunToggle.IsChecked == true;

            if (dryRun)
            {
                DryRunStatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E6F4EA"));
                DryRunStatusText.Text = Loc.T("Badge_Safe");
                DryRunStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E7B34"));
            }
            else
            {
                DryRunStatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FDECEA"));
                DryRunStatusText.Text = Loc.T("Badge_Live");
                DryRunStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C62828"));
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = ResidueListView.SelectedItems.Cast<SelectableResidueItem>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(Loc.T("Msg_SelectToDelete"), Loc.T("Common_NotSelected"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool dryRun = DryRunToggle.IsChecked == true;

            if (!dryRun)
            {
                var confirm = MessageBox.Show(
                    $"選択した{selected.Count}件を実際に削除します。よろしいですか？\n\nこの操作は取り消せません。",
                    Loc.T("Common_Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                string manifestPath;
                try
                {
                    manifestPath = _backupWriter.Write(selected.Select(item => item.Item).ToList());
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show(
                        $"削除前バックアップを作成できなかったため、削除を中止しました。\n{ex.Message}",
                        Loc.T("Common_BackupFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _log.Info("ResidueBackup", "削除前バックアップを作成", manifestPath);
            }

            int successCount = 0, failCount = 0, dryRunCount = 0;
            var succeededItems = new List<SelectableResidueItem>();

            foreach (var selectableItem in selected)
            {
                var result = _remover.Remove(selectableItem.Item, dryRun);
                switch (result)
                {
                    case RemovalResult.Success:
                        successCount++;
                        succeededItems.Add(selectableItem);
                        break;
                    case RemovalResult.DryRun:
                        dryRunCount++;
                        break;
                    default:
                        failCount++;
                        break;
                }
            }

            if (dryRun)
            {
                MessageBox.Show(Loc.F("Msg_CheckDone", dryRunCount),
                    Loc.T("Common_Result"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(Loc.F("Msg_DeleteDone", successCount, failCount),
                    Loc.T("Common_Result"), MessageBoxButton.OK, MessageBoxImage.Information);

                foreach (var succeeded in succeededItems)
                {
                    _items.Remove(succeeded);
                }
                ResidueListView.Items.Refresh();
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (!LicenseState.IsProUnlocked)
            {
                var purchaseConfirm = MessageBox.Show(
                    Loc.T("Msg_ExportProPrompt"),
                    Loc.T("Common_ProFeature"), MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (purchaseConfirm == MessageBoxResult.Yes)
                {
                    try
                    {
                        LicenseState.OpenPurchasePage();
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("Purchase", "購入ページを開けなかった", ex.Message);
                    }
                }
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.T("Dlg_ExportResidueTitle"),
                Filter = Loc.T("Dlg_CsvFilter"),
                FileName = $"ResidueScan_{_appName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(Loc.T("Csv_ResidueHeader"));
                foreach (var item in _items)
                {
                    sb.AppendLine($"{EscapeCsv(item.CategoryText)},{EscapeCsv(item.LocationText)},{EscapeCsv(item.DetailText)}");
                }

                System.IO.File.WriteAllText(dialog.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                MessageBox.Show($"スキャン結果({_items.Count}件)をCSV出力しました:\n{dialog.FileName}",
                    Loc.T("Common_ExportDone"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"エクスポート中にエラーが発生しました:\n{ex.Message}",
                    Loc.T("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }
}
