using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Purge;

namespace Purge.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// アプリ全体で1つの操作ログを共有する。個々のウィンドウ(MainWindow等)は
    /// それぞれ独自のOperationLogインスタンスを持つ設計だったが、未処理例外の捕捉は
    /// アプリケーションレベルで行う必要があるため、ここに集約する。
    /// MainWindowのコンストラクタでこの共有ログを使うよう差し替える。
    /// </summary>
    public static readonly OperationLog SharedLog = new();

    /// <summary>
    /// バックグラウンドの更新チェックで新バージョンが見つかった場合、そのタグ名(例: "v0.2.0")が入る。
    /// MainWindow側でこれを見て控えめな通知(バナー等)を出す想定。null なら更新なし、または未確認。
    /// </summary>
    public static string? AvailableUpdateTag { get; private set; }

    /// <summary>
    /// 新バージョンのMSIインストーラーのダウンロードURL(GitHub Releasesのasset)。
    /// AvailableUpdateTagとセットで設定される。
    /// </summary>
    public static string? AvailableUpdateMsiUrl { get; private set; }

    /// <summary>
    /// バックグラウンドの更新チェックで新バージョンが見つかった時点で発火する。
    /// MainWindowのコンストラクタは先にShow()されているため、チェック完了を待たず
    /// 起動する。MainWindow側はこのイベントを購読し、見つかり次第バナーを表示する。
    /// UIスレッドで呼び出すことを保証する(購読側でDispatcher対応を意識しなくてよいように)。
    /// </summary>
    public static event Action<string, string?>? UpdateAvailable;

    private const string GitHubReleasesApiUrl = "https://api.github.com/repos/Akatuki1121/Purge/releases/latest";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UIスレッドで発生した未処理例外を捕捉し、専用のクラッシュレポート画面を表示する。
        // e.Handled = true にすることでアプリの即時終了を防ぎ、ユーザーがログをコピーする猶予を与える。
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // UIスレッド以外(Task.Runのバックグラウンド処理等)で発生し、どこにもcatchされなかった例外。
        // こちらはプロセスを止められないため、記録してから通常通りクラッシュさせる。
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        // 起動をブロックしないよう、更新チェックは完全にバックグラウンドで行う。
        // このアプリは管理者権限が前提のため、ダウンロード・自動適用は行わない
        // (UACダイアログを勝手に出すことはできないため)。新バージョンの有無だけを
        // 確認し、あればMainWindow側で控えめに知らせ、実際の更新はユーザーが
        // Releasesページからダウンロード・手動実行する形にする。
        _ = CheckForUpdatesInBackgroundAsync();
    }

    /// <summary>
    /// GitHub Releasesの最新版タグを取得し、現在の実行中バージョンと比較する。
    /// UIは一切ブロックせず、失敗しても(オフライン等)アプリの動作に影響を与えない。
    /// </summary>
    private static async System.Threading.Tasks.Task CheckForUpdatesInBackgroundAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Purge-UpdateChecker");
            client.Timeout = TimeSpan.FromSeconds(10);

            var json = await client.GetStringAsync(GitHubReleasesApiUrl);
            using var doc = JsonDocument.Parse(json);

            var latestTag = doc.RootElement.GetProperty("tag_name").GetString();
            if (string.IsNullOrEmpty(latestTag))
            {
                return;
            }

            var latestVersionText = latestTag.TrimStart('v', 'V');
            var currentVersionText = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

            if (Version.TryParse(latestVersionText, out var latestVersion)
                && Version.TryParse(currentVersionText, out var currentVersion)
                && latestVersion > currentVersion)
            {
                AvailableUpdateTag = latestTag;

                // Releaseのassets一覧から、release.ymlが生成する ".msi" 拡張子のファイルを探す。
                // 見つからない場合(assetsが空、命名規則が変わった等)はURLをnullのままにし、
                // MainWindow側は「新バージョンがあります」の案内のみ出す(ダウンロードボタンは無効化)。
                if (doc.RootElement.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString();
                        if (name != null && name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                        {
                            AvailableUpdateMsiUrl = asset.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }
                }

                Current?.Dispatcher.Invoke(() => UpdateAvailable?.Invoke(AvailableUpdateTag, AvailableUpdateMsiUrl));
            }
        }
        catch
        {
            // 更新確認自体の失敗(オフライン、GitHub障害など)は静かに無視する。
            // 次回起動時にまた試みればよく、ユーザー操作を妨げるべきではない。
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        SharedLog.Error("UnhandledException", "UIスレッドで未処理の例外が発生", e.Exception.Message);
        var report = SharedLog.BuildErrorReport(e.Exception);

        var crashWindow = new CrashReportWindow(report);
        crashWindow.ShowDialog();

        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            SharedLog.Error("UnhandledException", "バックグラウンドスレッドで未処理の例外が発生", ex.Message);
            var report = SharedLog.BuildErrorReport(ex);

            try
            {
                var crashWindow = new CrashReportWindow(report);
                crashWindow.ShowDialog();
            }
            catch
            {
                // 表示自体に失敗した場合はこれ以上何もできないため黙って抜ける
            }
        }
    }
}
