using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Purge;
using Purge.Localization;

namespace Purge.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
/// <summary>更新確認の結果。</summary>
public enum UpdateCheckResult
{
    UpdateAvailable,
    UpToDate,
    Failed,
    AlreadyRunning,
}

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

        // 表示言語を最初のウィンドウより前に確定させる(保存済みの設定、なければOSの言語に従う)。
        Loc.Initialize();

        RegisterStartMenuShortcutInBackground();

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        // 起動をブロックしないよう、更新チェックは完全にバックグラウンドで行う。
        // このアプリは管理者権限が前提のため、ダウンロード・自動適用は行わない
        // (UACダイアログを勝手に出すことはできないため)。新バージョンの有無だけを
        // 確認し、あればMainWindow側で控えめに知らせ、実際の更新はユーザーが
        // Releasesページからダウンロード・手動実行する形にする。
        // 開発版(Debugビルド)は自動では確認しない(常に最新タグ以上のバージョンを名乗るうえ、
        // 開発中に更新バナーが出ても意味がないため)。確認したいときはメニューから手動で行える。
        if (!BuildInfo.IsDevBuild)
        {
            _ = CheckForUpdatesAsync();
        }
    }

    /// <summary>
    /// ZIP版を直接起動した場合に備え、スタートメニュー(アプリ一覧)へPurge.lnkを自動登録する(#84)。
    /// 起動を遅らせないようバックグラウンドで行い、失敗しても起動は妨げない(失敗は操作ログに残す)。
    /// ショートカット操作(IShellLink)はSTAスレッドで行う必要があるため専用スレッドを使う。
    /// </summary>
    private static void RegisterStartMenuShortcutInBackground()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return;
        }

        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                new StartMenuShortcutRegistrar(SharedLog).EnsureRegistered(exePath);
            }
            catch (Exception ex)
            {
                SharedLog.Warning("StartMenu", "スタートメニューへの自動登録に失敗しました(起動には影響しません)", ex.Message);
            }
        })
        {
            IsBackground = true,
            Name = "StartMenuRegistration",
        };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// GitHub Releasesの最新版タグを取得し、現在の実行中バージョンと比較する。
    /// UIは一切ブロックせず、失敗しても(オフライン等)アプリの動作に影響を与えない。
    /// </summary>
    public static async System.Threading.Tasks.Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        // 起動時チェックと手動チェックが重なったときの二重実行を避ける。
        if (System.Threading.Interlocked.Exchange(ref s_updateCheckRunning, 1) == 1)
        {
            return UpdateCheckResult.AlreadyRunning;
        }

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
                return UpdateCheckResult.Failed;
            }

            var latestVersionText = latestTag.TrimStart('v', 'V');
            var currentVersionText = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

            if (Version.TryParse(latestVersionText, out var latestVersion)
                && Version.TryParse(currentVersionText, out var currentVersion)
                && latestVersion > currentVersion)
            {
                AvailableUpdateTag = latestTag;

                // Releaseのassets一覧から、release.ymlが生成する日本語版MSI(-ja.msi)を優先して探す。
                // release.ymlはja/en2つのMSIを同じリリースに並べて公開するため、単純に
                // 「拡張子が.msiの最初の1件」を採用すると、GitHub API側の返却順序次第で
                // 英語版(-en.msi)を誤って選んでしまうことがあった(実際に発生した不具合)。
                // アプリの表示文言・UIは日本語のみ対応のため、更新も常に日本語版を優先する。
                string? fallbackMsiUrl = null;
                if (doc.RootElement.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString();
                        if (name == null || !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;

                        var url = asset.GetProperty("browser_download_url").GetString();
                        if (name.Contains("-ja.msi", StringComparison.OrdinalIgnoreCase))
                        {
                            AvailableUpdateMsiUrl = url;
                            break;
                        }

                        // 日本語版が見つからない場合(命名規則変更等)に備えて、最初に見つかった
                        // .msiを控えておく。
                        fallbackMsiUrl ??= url;
                    }

                    AvailableUpdateMsiUrl ??= fallbackMsiUrl;
                }

                Current?.Dispatcher.Invoke(() => UpdateAvailable?.Invoke(AvailableUpdateTag, AvailableUpdateMsiUrl));
                return UpdateCheckResult.UpdateAvailable;
            }

            return UpdateCheckResult.UpToDate;
        }
        catch
        {
            // 更新確認自体の失敗(オフライン、GitHub障害など)は例外にせず結果として返す。
            // 起動時チェックでは無視し、手動チェックでは呼び出し側が失敗を表示する。
            return UpdateCheckResult.Failed;
        }
        finally
        {
            System.Threading.Volatile.Write(ref s_updateCheckRunning, 0);
        }
    }

    private static int s_updateCheckRunning;

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
