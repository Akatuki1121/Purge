using System.Collections.ObjectModel;
using System.Diagnostics;
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
    /// 対応アプリ不明フォルダのスキャン結果を一覧表示し、選択した項目のみ削除する画面。
    /// 選択はチェックボックス列ではなく、ListView標準の行選択(Ctrl/Shiftで複数選択可)を使う。
    /// 削除は既存のResidueRemover(MftFileカテゴリ)に委譲し、削除ロジックを一本化する。
    /// </summary>
    public partial class OrphanWindow : FluentWindow
    {
        private readonly OperationLog _log;
        private readonly ResidueRemover _remover;
        private readonly ObservableCollection<SelectableOrphanCandidate> _items;

        public OrphanWindow(System.Collections.Generic.List<OrphanCandidate> candidates, OperationLog log)
        {
            InitializeComponent();
            _log = log;
            _remover = new ResidueRemover(_log);

            _items = new ObservableCollection<SelectableOrphanCandidate>(
                candidates.Select(c => new SelectableOrphanCandidate(c)));
            OrphanListView.ItemsSource = _items;
        }

        /// <summary>
        /// ドライラン切り替え時、色付きバッジで安全モードか実行モードかを明示する。
        /// XAMLのIsChecked="True"によりInitializeComponent()実行中にCheckedが発火するため、
        /// バッジ未初期化のnullガードが必須(MainWindowで実際に起きたクラッシュと同種の問題)。
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

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = OrphanListView.SelectedItems.Cast<SelectableOrphanCandidate>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("開く項目を選択してください(行をクリックして選択できます)。", "未選択",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var item in selected)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{item.FullPath}\"",
                        UseShellExecute = true,
                    });
                }
                catch (System.Exception ex)
                {
                    _log.Warning("OrphanReview", "エクスプローラーで開けなかった", $"{item.FullPath}: {ex.Message}");
                }
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = OrphanListView.SelectedItems.Cast<SelectableOrphanCandidate>().ToList();
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
                    $"選択した{selected.Count}件を実際に削除します。\n\n" +
                    "これらは「対応アプリ不明フォルダ」であり、誤検出の可能性があります。\n" +
                    "本当に不要と確認したものだけ選択していることを確認してください。\n\n" +
                    "よろしいですか？この操作は取り消せません。",
                    "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
            }

            int successCount = 0, failCount = 0, dryRunCount = 0;
            var succeededItems = new System.Collections.Generic.List<SelectableOrphanCandidate>();

            foreach (var item in selected)
            {
                var residueItem = new ResidueItem
                {
                    Category = ResidueCategory.MftFile,
                    Location = item.FullPath,
                    Detail = "対応アプリ不明フォルダとして検出",
                };

                var result = _remover.Remove(residueItem, dryRun);
                switch (result)
                {
                    case RemovalResult.Success:
                        successCount++;
                        succeededItems.Add(item);
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
                Title = "対応アプリ不明フォルダ一覧をCSVエクスポート",
                Filter = "CSVファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
                FileName = $"OrphanFolders_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("フォルダ名,場所の説明,ファイル数,サイズ,最終更新日時,フルパス");
                foreach (var item in _items)
                {
                    sb.AppendLine($"{EscapeCsv(item.FolderName)},{EscapeCsv(item.LocationDescription)},{EscapeCsv(item.FileCountDisplay)},{EscapeCsv(item.SizeDisplay)},{EscapeCsv(item.LastModifiedDisplay)},{EscapeCsv(item.FullPath)}");
                }

                System.IO.File.WriteAllText(dialog.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                MessageBox.Show($"候補フォルダ一覧({_items.Count}件)をCSV出力しました:\n{dialog.FileName}",
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
