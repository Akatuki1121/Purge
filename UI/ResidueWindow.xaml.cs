using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace UninstallTool.UI
{
    /// <summary>
    /// 残存物スキャン結果を一覧表示し、選択した項目のみ削除する画面。
    /// 選択はチェックボックス列ではなく、ListView標準の行選択(Ctrl/Shiftで複数選択可)を使う。
    /// </summary>
    public partial class ResidueWindow : FluentWindow
    {
        private readonly OperationLog _log;
        private readonly ResidueRemover _remover;
        private readonly List<SelectableResidueItem> _items;
        private readonly string _appName;

        public ResidueWindow(string appName, List<ResidueItem> residueItems, OperationLog log)
        {
            InitializeComponent();
            _appName = appName;
            _log = log;
            _remover = new ResidueRemover(_log);

            _items = residueItems.Select(i => new SelectableResidueItem(i)).ToList();
            ResidueListView.ItemsSource = _items;
            TitleText.Text = $"「{appName}」の残存物: {_items.Count}件";
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
                DryRunStatusText.Text = "✓ 安全モード(実際には削除されません)";
                DryRunStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E7B34"));
            }
            else
            {
                DryRunStatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FDECEA"));
                DryRunStatusText.Text = "⚠ 実行モード(本当に削除されます)";
                DryRunStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C62828"));
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = ResidueListView.SelectedItems.Cast<SelectableResidueItem>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("削除する項目を選択してください(行をクリックして選択できます)。", "未選択",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool dryRun = DryRunToggle.IsChecked == true;

            if (!dryRun)
            {
                var confirm = MessageBox.Show(
                    $"選択した{selected.Count}件を実際に削除します。よろしいですか？\n\nこの操作は取り消せません。",
                    "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
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
                MessageBox.Show($"ドライラン完了: {dryRunCount}件(実際の削除は行っていません)",
                    "結果", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"削除完了: 成功 {successCount}件 / 失敗 {failCount}件",
                    "結果", MessageBoxButton.OK, MessageBoxImage.Information);

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
                MessageBox.Show(
                    "スキャン結果のエクスポート(CSV保存)はPro版の機能です。\n(現在ライセンス販売は準備中です)",
                    "Pro機能", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "スキャン結果をCSVエクスポート",
                Filter = "CSVファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
                FileName = $"ResidueScan_{_appName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("種別,場所,詳細");
                foreach (var item in _items)
                {
                    sb.AppendLine($"{EscapeCsv(item.CategoryText)},{EscapeCsv(item.LocationText)},{EscapeCsv(item.DetailText)}");
                }

                System.IO.File.WriteAllText(dialog.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                MessageBox.Show($"スキャン結果({_items.Count}件)をCSV出力しました:\n{dialog.FileName}",
                    "エクスポート完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"エクスポート中にエラーが発生しました:\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }
}
