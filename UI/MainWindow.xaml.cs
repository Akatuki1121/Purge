using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.ComponentModel;
using System.Threading;
using Purge;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace Purge.UI;

/// <summary>
/// アプリ一覧表示・アンインストール実行の最小画面。
/// ロジックは Purge.Core (AppInventory, AppUninstaller, OperationLog, ResidueScanner) にすべて委譲する。
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
    private readonly OrphanExclusionStore _orphanExclusions;
    private ICollectionView? _appView;
    private int _totalAppCount;
    private CancellationTokenSource? _scanCts;

    public MainWindow()
    {
        InitializeComponent();
        _inventory = new AppInventory(_log);
        _uninstaller = new AppUninstaller(_log);
        _scanner = new ResidueScanner(_log);
        _orphanExclusions = new OrphanExclusionStore();
        _orphanDetector = new OrphanDetector(_log, _orphanExclusions);

        if (!ElevationChecker.IsRunningAsAdministrator())
        {
            ElevationWarningBorder.Visibility = Visibility.Visible;
            _log.Warning("Startup", "管理者権限で実行されていません。MFT検索系の機能は失敗します。");
        }

        if (LicenseState.IsProUnlocked)
        {
            BatchUninstallButton.Content = "選択した複数アプリを一括アンインストール";
            ProUpgradeButton.Visibility = Visibility.Collapsed;
        }

        AppListView.SelectionChanged += AppListView_SelectionChanged;

        // バックグラウンドの更新チェックがコンストラクタより先に完了していた場合(通常はまず
        // 無いが将来的な実行順序変更に備え)と、これから完了する場合の両方に対応する。
        if (App.AvailableUpdateTag != null)
        {
            ShowUpdateBanner(App.AvailableUpdateTag, App.AvailableUpdateMsiUrl);
        }
        App.UpdateAvailable += OnUpdateAvailable;
        Closed += (_, _) => App.UpdateAvailable -= OnUpdateAvailable;

        // 列幅をウィンドウ幅に追従させる(固定幅だと縮小時に列が見切れ、拡大時に右側が空く)。
        // 先頭のアイコン列はnull=固定幅、以降はアプリ名・バージョン・発行元・場所の伸縮比率。
        GridViewColumnSizer.AttachAutoSize(AppListView, new double?[] { null, 34, 13, 24, 29 });

        // 横ホイール(チルトホイール)とShift+ホイールでの横スクロールを有効化。
        HorizontalScrollSupport.AttachToWindow(this);
        HorizontalScrollSupport.AttachShiftWheel(AppListView);

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
        EmptyStateText.Text = "アプリ一覧を読み込み中...";
        EmptyStatePanel.Visibility = Visibility.Visible;

        var apps = await Task.Run(() => _inventory.GetInstalledApps());
        var items = apps.Select(a => new AppListItem(a)).ToList();
        _totalAppCount = items.Count;
        _appView = CollectionViewSource.GetDefaultView(items);
        _appView.Filter = FilterApp;
        AppListView.ItemsSource = _appView;
        UpdateAppCount();
        UpdateEmptyState();
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

    private bool FilterApp(object item)
    {
        if (item is not AppListItem app)
        {
            return false;
        }

        var query = SearchTextBox?.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        return new[] { app.DisplayName, app.DisplayVersion, app.Publisher, app.InstallLocation }
            .Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _appView?.Refresh();
        UpdateAppCount();
    }

    private void UpdateAppCount()
    {
        if (_appView == null)
        {
            return;
        }

        var visibleCount = _appView.Cast<object>().Count();
        CountText.Text = visibleCount == _totalAppCount
            ? $"{_totalAppCount}件"
            : $"{visibleCount} / {_totalAppCount}件";
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (_appView == null)
        {
            return;
        }

        var visibleCount = _appView.Cast<object>().Count();
        EmptyStatePanel.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateText.Text = _totalAppCount == 0
            ? "インストール済みアプリは見つかりませんでした"
            : "検索条件に一致するアプリはありません";
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
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        CancelScanButton.Visibility = Visibility.Visible;
        ScanStatusText.Text = "スキャン準備中";

        try
        {
            var progress = new Progress<ScanProgress>(value =>
            {
                ScanStatusText.Text = $"{value.Category}: {value.CurrentItem}";
            });
            var results = await Task.Run(() =>
                _scanner.ScanAll(appName, includeMftSearch: false,
                    cancellationToken: _scanCts.Token, progress: progress), _scanCts.Token);

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
        catch (OperationCanceledException)
        {
            _log.Info("ResidueScan", "残存物スキャンをキャンセルしました", appName);
            RefreshLogView();
        }
        catch (System.Exception ex)
        {
            new CrashReportWindow(_log.BuildErrorReport(ex)) { Owner = this }.ShowDialog();
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            CancelScanButton.Visibility = Visibility.Collapsed;
            ScanStatusText.Text = "スキャン: 待機中";
        }
    }

    private void CancelScanButton_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        ScanStatusText.Text = "キャンセル中...";
    }

    /// <summary>
    /// 対応アプリ不明フォルダのスキャン: 特定アプリの選択は不要(システム全体を横断走査するため)。
    /// メニュー「ツール」からのみ呼び出される、選択操作から独立した機能。
    /// MFT検索+exe/dllメタデータチェックを含む重い処理のため、UIスレッドをブロックしないよう別スレッドで実行する。
    /// </summary>
    private async void ScanOrphanButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Button)sender;
        var originalContent = button.Content;

        try
        {
            // スキャンはMFT検索を含み数十秒かかることがあるため、進行中であることを
            // 明示してボタンの二重押しを防ぐ(押せたかどうか分からない、という指摘への対応)。
            button.IsEnabled = false;
            button.Content = "検索中...";
            ScanStatusText.Text = "不明フォルダを検索中...";
            Mouse.OverrideCursor = Cursors.Wait;

            var apps = _inventory.GetInstalledApps();

            var orphans = await Task.Run(() =>
                _orphanDetector.DetectOrphans("C", apps, includeExecutableMetadataCheck: true));

            RefreshLogView();

            if (orphans.Count == 0)
            {
                ScanStatusText.Text = "検索完了(該当なし)";
                MessageBox.Show("不明フォルダは見つかりませんでした。", "検索結果",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ScanStatusText.Text = $"検索完了({orphans.Count}件)";

            var orphanWindow = new OrphanWindow(orphans, _log, _orphanExclusions)
            {
                Owner = this,
            };
            orphanWindow.ShowDialog();
            RefreshLogView();
        }
        catch (System.Exception ex)
        {
            ScanStatusText.Text = "検索に失敗しました";
            new CrashReportWindow(_log.BuildErrorReport(ex)) { Owner = this }.ShowDialog();
        }
        finally
        {
            Mouse.OverrideCursor = null;
            button.IsEnabled = true;
            button.Content = originalContent;
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
            var purchaseConfirm = MessageBox.Show(
                "複数アプリの一括アンインストールはPro版の機能です。\n\n購入ページを開きますか？",
                "Pro機能", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (purchaseConfirm == MessageBoxResult.Yes)
            {
                try
                {
                    LicenseState.OpenPurchasePage();
                }
                catch (System.Exception ex)
                {
                    _log.Warning("Purchase", "購入ページを開けなかった", ex.Message);
                }
            }
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
                ? $"確認完了: {dryRunCount}件(安全モードのため実際の削除は行っていません)"
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
        // Issue #28対応でヘッダーをHeaderTemplate(DataTemplate)化したため、内部のButtonに
        // x:Nameでは直接アクセスできなくなった。senderから取得する。
        var button = (System.Windows.Controls.Button)sender;

        if (string.IsNullOrEmpty(LogText.Text))
        {
            return;
        }

        Clipboard.SetText(LogText.Text);
        button.Content = "コピーしました";

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = System.TimeSpan.FromSeconds(1.5),
        };
        timer.Tick += (_, _) =>
        {
            button.Content = "コピー";
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
        LogExpander.IsExpanded = ShowLogMenuItem.IsChecked;
    }

    private void LogExpander_Changed(object sender, RoutedEventArgs e)
    {
        ShowLogMenuItem.IsChecked = LogExpander.IsExpanded;
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Purge\n\n残存ファイル・レジストリ・サービス・タスクスケジューラまで横断的にスキャンできる\nアンインストーラーです。",
            "バージョン情報", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ReportBugMenuItem_Click(object sender, RoutedEventArgs e)
    {
        new BugReportWindow(_log)
        {
            Owner = this,
        }.ShowDialog();
    }

    private void RestoreBackupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "削除前バックアップを選択",
            Filter = "JSONマニフェスト (*.json)|*.json",
            InitialDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Purge", "RemovalBackups"),
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var confirm = MessageBox.Show(
            "選択したバックアップからファイル・フォルダ・レジストリを復元します。\n" +
            "既存のファイルやレジストリ値は上書きされる場合があります。\n\n実行しますか？",
            "バックアップから復元", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var restored = new RemovalRestoreService(_log).Restore(dialog.FileName);
            MessageBox.Show($"復元処理が完了しました。復元件数: {restored}件", "復元完了",
                MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshLogView();
        }
        catch (System.Exception ex)
        {
            new CrashReportWindow(_log.BuildErrorReport(ex)) { Owner = this }.ShowDialog();
        }
    }

    private void LicenseMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenLicenseWindowAndRefresh();
    }

    /// <summary>
    /// メイン画面右上に常設した「Proにアップグレード」ボタン。
    /// 以前は「その他」メニューの奥にしかPro導線がなく気づかれにくかったため、
    /// 常に見える位置に配置した(Pro解放済みの場合はコンストラクタでVisibility.Collapsedにする)。
    /// </summary>
    private void ProUpgradeButton_Click(object sender, RoutedEventArgs e)
    {
        OpenLicenseWindowAndRefresh();
    }

    private void OpenLicenseWindowAndRefresh()
    {
        new LicenseWindow
        {
            Owner = this,
        }.ShowDialog();

        // ライセンス状態が変わった可能性があるため、関連するUIの表示を更新する
        BatchUninstallButton.Content = LicenseState.IsProUnlocked
            ? "選択した複数アプリを一括アンインストール"
            : "選択した複数アプリを一括アンインストール (Pro)";
        ProUpgradeButton.Visibility = LicenseState.IsProUnlocked
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// App.UpdateAvailableイベントのハンドラ。App.xaml.cs側でUIスレッドから呼ばれることが
    /// 保証されているため、ここではDispatcher対応は不要。
    /// </summary>
    private void OnUpdateAvailable(string tag, string? msiUrl)
    {
        ShowUpdateBanner(tag, msiUrl);
    }

    /// <summary>
    /// 新バージョン通知バナーを表示する。本アプリは管理者権限起動が前提のため、
    /// 更新のたびに必ずUAC同意が要り完全な自動更新はできない。そのため「気づいたら
    /// ユーザー自身がダウンロード・実行する」半自動方式とし、起動やその後の操作を
    /// 一切妨げない控えめな通知に留める。
    /// </summary>
    private void ShowUpdateBanner(string tag, string? msiUrl)
    {
        UpdateAvailableText.Text = $"🔔 新しいバージョン({tag})があります。";
        _pendingUpdateTag = tag;
        _pendingUpdateMsiUrl = msiUrl;

        // assetsからMSIが見つからなかった場合(命名規則変更等)は、ダウンロードではなく
        // Releasesページを開くボタンに切り替える(URLが完全に無いよりは救済になる)。
        UpdateDownloadButton.Content = msiUrl != null ? "ダウンロード" : "Releasesページを開く";

        UpdateAvailableBorder.Visibility = Visibility.Visible;
    }

    private string? _pendingUpdateTag;
    private string? _pendingUpdateMsiUrl;

    /// <summary>
    /// 「ダウンロード」ボタン押下時の処理。
    /// MSIのURLが取得できている場合はアプリ内でダウンロードし、完了後にインストーラーを
    /// 起動するかユーザーに確認する。取得できていない場合はRelasesページをブラウザで開く。
    /// </summary>
    private async void UpdateDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdateMsiUrl == null)
        {
            LicenseState.OpenUrl("https://github.com/Akatuki1121/Purge/releases/latest");
            return;
        }

        var originalContent = UpdateDownloadButton.Content;
        UpdateDownloadButton.IsEnabled = false;
        UpdateDismissButton.IsEnabled = false;

        try
        {
            var progress = new Progress<double?>(p =>
            {
                UpdateDownloadButton.Content = p.HasValue
                    ? $"ダウンロード中... {p.Value:P0}"
                    : "ダウンロード中...";
            });

            var msiPath = await UpdateDownloader.DownloadAsync(_pendingUpdateMsiUrl, progress);

            var result = MessageBox.Show(
                $"バージョン {_pendingUpdateTag} のダウンロードが完了しました。\n\n" +
                "今すぐインストーラーを起動しますか?\n" +
                "(インストール中はPurgeを終了する必要があります。管理者権限の確認が表示されます)",
                "ダウンロード完了", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
            {
                UpdateDownloader.LaunchInstaller(msiPath);
                Close();
            }
            else
            {
                // 「後で」を選んだ場合、次回もダウンロードから促せるようバナーは維持しつつ
                // ボタンだけ元に戻す(再ダウンロードは避けたいが、パスを覚えておく必要はない。
                // 次回ダウンロード実行時はUpdateDownloader側で同名ファイルの再利用に任せる)。
                UpdateDownloadButton.Content = "インストーラーを起動";
                UpdateDownloadButton.IsEnabled = true;
                UpdateDismissButton.IsEnabled = true;
                _downloadedMsiPath = msiPath;
                UpdateDownloadButton.Click -= UpdateDownloadButton_Click;
                UpdateDownloadButton.Click += UpdateLaunchButton_Click;
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"アップデートのダウンロードに失敗しました。\n\n{ex.Message}\n\n" +
                "手動でRelasesページからダウンロードしてください。",
                "ダウンロード失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateDownloadButton.Content = originalContent;
        }
        finally
        {
            UpdateDownloadButton.IsEnabled = true;
            UpdateDismissButton.IsEnabled = true;
        }
    }

    private string? _downloadedMsiPath;

    /// <summary>
    /// ダウンロード済みMSIの起動だけを行うハンドラ。「後で」を選んだ後にボタンを押した場合用。
    /// </summary>
    private void UpdateLaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadedMsiPath == null) return;
        UpdateDownloader.LaunchInstaller(_downloadedMsiPath);
        Close();
    }

    private void UpdateDismissButton_Click(object sender, RoutedEventArgs e)
    {
        // 「後で」は今回のセッション中だけ非表示にする(次回起動時はまた表示される)。
        // 恒久的な非表示設定は現状持たない。
        UpdateAvailableBorder.Visibility = Visibility.Collapsed;
    }
}
