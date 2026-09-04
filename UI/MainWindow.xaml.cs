using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using UninstallTool;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace UninstallTool.UI;

/// <summary>
/// アプリ一覧表示・アンインストール実行の最小画面。
/// ロジックは UninstallTool.Core (AppInventory, AppUninstaller, OperationLog, ResidueScanner) にすべて委譲する。
///
/// 画面遷移方針: メインの流れは「アプリを選ぶ→アンインストール→(自動提案で)残存物スキャン」の1本道にし、
/// 選択と無関係な孤児候補スキャンはメニュー「ツール」からのみ呼び出す。
/// 一括アンインストール(Pro)ボタンは複数選択時のみ表示し、初心者向けの通常フローを邪魔しないようにする。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly OperationLog _log = App.SharedLog;
    private readonly AppInventory _inventory;
    private readonly AppUninstaller _uninstaller;
    private readonly ResidueScanner _scanner;
    private readonly OrphanDetector _orphanDetector;

    public MainWindow()
    {
        InitializeComponent();
        _inventory = new AppInventory(_log);
        _uninstaller = new AppUninstaller(_log);
        _scanner = new ResidueScanner(_log);
        _orphanDetector = new OrphanDetector(_log);

        if (!ElevationChecker.IsRunningAsAdministrator())
        {
            ElevationWarningBorder.Visibility = Visibility.Visible;
            _log.Warning("Startup", "管理者権限で実行されていません。MFT検索系の機能は失敗します。");
        }

        if (LicenseState.IsProUnlocked)
        {
            BatchUninstallButton.Content = "選択した複数アプリを一括アンインストール";
        }

        AppListView.SelectionChanged += AppListView_SelectionChanged;

        LoadApps();
    }

    /// <summary>
    /// ドライラン切り替え時、トグルの状態だけでなく色付きバッジでも
    /// 「安全モードか実際に削除するモードか」をひと目で分かるようにする。
    ///
    /// 注意: XAMLでToggleSwitchにIsChecked="True"を指定していると、InitializeComponent()の
    /// 実行中(=このウィンドウのコンストラクタの途中)にCheckedイベントが発火する。
    /// その時点ではDryRunStatusBadge等のフィールドがまだnullのままのため、ここで参照すると
    /// NullReferenceExceptionで即クラッシュする。そのためnullガードを入れて安全に抜ける。
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

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        LoadApps();
    }

    /// <summary>
    /// アプリ一覧の読み込みを2段階に分ける。
    /// 1段目: レジストリ読み取り(112件規模で数百ms〜要することがある)をバックグラウンドスレッドで行い、
    /// UIスレッドを一切ブロックしないようにする。
    /// 2段目: アイコン抽出・発行元補完(いずれも1件あたり数十ms、100件超だと合計で数秒規模)も
    /// バックグラウンドで行い、完了したものから順にUIへ反映する。
    /// これによりウィンドウ表示(UAC承認直後)からアプリ一覧が見えるまでの体感待ち時間を最小化する。
    /// </summary>
    private async void LoadApps()
    {
        CountText.Text = "読み込み中...";

        var apps = await Task.Run(() => _inventory.GetInstalledApps());
        var items = apps.Select(a => new AppListItem(a)).ToList();
        AppListView.ItemsSource = items;
        CountText.Text = $"{items.Count}件検出";
        RefreshLogView();

        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                item.ResolveIcon();
                // 発行元がレジストリ未登録の場合、exe/dllのCompanyNameで補完する(発行元が空欄になる問題への対策)
                item.ResolvePublisher();
            }
        });
    }

    /// <summary>
    /// 複数選択されたときだけ一括アンインストールボタンを表示する。
    /// 単一選択/未選択の通常フローでは隠しておき、初心者を混乱させない。
    /// </summary>
    private void AppListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        BatchUninstallButton.Visibility = AppListView.SelectedItems.Count >= 2
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppListView.SelectedItem is not AppListItem selectedItem)
        {
            MessageBox.Show("アンインストールするアプリを一覧から選択してください。", "未選択",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var selectedApp = selectedItem.App;

        bool dryRun = DryRunToggle.IsChecked == true;

        if (!dryRun)
        {
            var confirm = MessageBox.Show(
                $"「{selectedApp.DisplayName}」を実際にアンインストールします。よろしいですか？",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                RefreshLogView();
                return;
            }
        }

        var result = _uninstaller.ExecuteUninstall(selectedApp, dryRun);
        RefreshLogView();

        MessageBox.Show($"結果: {result}", "アンインストール", MessageBoxButton.OK, MessageBoxImage.Information);

        if (!dryRun)
        {
            LoadApps();
        }

        // アンインストール完了後、確認を挟まずそのまま残存物スキャンへ移行する
        // (以前は「スキャンしますか？」の確認ダイアログを挟んでいたが、
        // アンインストール後に残存物を確認するのは既定の流れなので、都度尋ねる必要はないと判断)
        await RunResidueScanAsync(selectedApp.DisplayName);
    }

    /// <summary>
    /// 残存物スキャンの共通処理。単体アンインストール後の自動提案からも、
    /// 一括アンインストール後からも呼べるよう独立したメソッドにしている。
    /// </summary>
    private async Task RunResidueScanAsync(string appName, bool silentIfEmpty = false)
    {
        try
        {
            var results = await Task.Run(() =>
                _scanner.ScanAll(appName, includeMftSearch: false));

            RefreshLogView();

            if (results.Count == 0)
            {
                if (!silentIfEmpty)
                {
                    MessageBox.Show("残存物は見つかりませんでした。", "スキャン結果",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            var residueWindow = new ResidueWindow(appName, results, _log)
            {
                Owner = this,
            };
            residueWindow.ShowDialog();
            RefreshLogView();
        }
        catch (System.Exception ex)
        {
            new CrashReportWindow(_log.BuildErrorReport(ex)) { Owner = this }.ShowDialog();
        }
    }

    /// <summary>
    /// 孤児候補スキャン: 特定アプリの選択は不要(システム全体を横断走査するため)。
    /// メニュー「ツール」からのみ呼び出される、選択操作から独立した機能。
    /// MFT検索+exe/dllメタデータチェックを含む重い処理のため、UIスレッドをブロックしないよう別スレッドで実行する。
    /// </summary>
    private async void ScanOrphanButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var apps = _inventory.GetInstalledApps();

            var orphans = await Task.Run(() =>
                _orphanDetector.DetectOrphans("C", apps, includeExecutableMetadataCheck: true));

            RefreshLogView();

            if (orphans.Count == 0)
            {
                MessageBox.Show("孤児候補は見つかりませんでした。", "スキャン結果",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var orphanWindow = new OrphanWindow(orphans, _log)
            {
                Owner = this,
            };
            orphanWindow.ShowDialog();
            RefreshLogView();
        }
        catch (System.Exception ex)
        {
            new CrashReportWindow(_log.BuildErrorReport(ex)) { Owner = this }.ShowDialog();
        }
    }

    /// <summary>
    /// 複数アプリの一括アンインストール(Pro機能)。複数選択時のみボタンが表示される。
    /// LicenseState.IsProUnlockedがfalseの間は案内のみ表示して実行しない。
    /// (開発者ローカルフラグにより自分自身は常に利用可能 — LicenseState参照)
    /// </summary>
    private async void BatchUninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (!LicenseState.IsProUnlocked)
        {
            MessageBox.Show(
                "複数アプリの一括アンインストールはPro版の機能です。\n(現在ライセンス販売は準備中です)",
                "Pro機能", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedApps = AppListView.SelectedItems.Cast<AppListItem>().Select(i => i.App).ToList();
        if (selectedApps.Count == 0)
        {
            return;
        }

        bool dryRun = DryRunToggle.IsChecked == true;

        if (!dryRun)
        {
            var names = string.Join("\n", selectedApps.Select(a => $"・{a.DisplayName}"));
            var confirm = MessageBox.Show(
                $"以下の{selectedApps.Count}件を一括でアンインストールします。\n\n{names}\n\nよろしいですか？",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        BatchUninstallButton.IsEnabled = false;
        var originalContent = BatchUninstallButton.Content;
        BatchUninstallButton.Content = "一括アンインストール中...";

        try
        {
            int successCount = 0, failCount = 0, dryRunCount = 0;
            var successApps = new List<InstalledApp>();

            foreach (var app in selectedApps)
            {
                var result = await Task.Run(() => _uninstaller.ExecuteUninstall(app, dryRun));
                switch (result)
                {
                    case UninstallResult.Success:
                        successCount++;
                        successApps.Add(app);
                        break;
                    case UninstallResult.DryRun:
                        dryRunCount++;
                        break;
                    default:
                        failCount++;
                        break;
                }
                RefreshLogView();
            }

            var summary = dryRun
                ? $"ドライラン完了: {dryRunCount}件"
                : $"完了: 成功 {successCount}件 / 失敗 {failCount}件";
            MessageBox.Show(summary, "一括アンインストール結果", MessageBoxButton.OK, MessageBoxImage.Information);

            if (!dryRun)
            {
                LoadApps();

                // アンインストール成功したアプリの残存物を順次スキャン(見つかったもののみ表示)
                foreach (var app in successApps)
                {
                    await RunResidueScanAsync(app.DisplayName, silentIfEmpty: true);
                }
            }
        }
        finally
        {
            BatchUninstallButton.IsEnabled = true;
            BatchUninstallButton.Content = originalContent;
        }
    }

    /// <summary>
    /// ツール自身の完全削除。確認を2段階(通常確認+入力確認)にすることで誤操作を防ぐ。
    /// 呼び出し後はプロセスをすぐに終了させる(batが自分のPIDの終了を待っているため)。
    /// </summary>
    private void SelfUninstallButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm1 = MessageBox.Show(
            "このツール自身をアンインストールします。\n実行ファイルと関連ファイルがすべて削除されます。\n\nよろしいですか？",
            "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm1 != MessageBoxResult.Yes) return;

        var confirm2 = MessageBox.Show(
            "本当に実行しますか？この操作は取り消せません。",
            "最終確認", MessageBoxButton.YesNo, MessageBoxImage.Stop);
        if (confirm2 != MessageBoxResult.Yes) return;

        SelfUninstaller.BeginSelfUninstall(_log);
        Application.Current.Shutdown();
    }

    private void RefreshLogView()
    {
        var lines = _log.GetRecent(200);
        LogText.Text = string.Join("\n", lines);
    }

    /// <summary>
    /// 操作ログ全文をクリップボードにコピーする。範囲選択ではなくワンクリックで確実にコピーできるようにする。
    /// </summary>
    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(LogText.Text))
        {
            return;
        }

        Clipboard.SetText(LogText.Text);
        CopyLogButton.Content = "コピーしました";

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = System.TimeSpan.FromSeconds(1.5),
        };
        timer.Tick += (_, _) =>
        {
            CopyLogButton.Content = "コピー";
            timer.Stop();
        };
        timer.Start();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void ShowLogMenuItem_Click(object sender, RoutedEventArgs e)
    {
        LogCard.Visibility = ShowLogMenuItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "UninstallTool\n\n残存ファイル・レジストリ・サービス・タスクスケジューラまで横断的にスキャンできる\nアンインストーラーです。",
            "バージョン情報", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ReportBugMenuItem_Click(object sender, RoutedEventArgs e)
    {
        new BugReportWindow(_log)
        {
            Owner = this,
        }.ShowDialog();
    }
}
