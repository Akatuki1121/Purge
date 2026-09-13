using System;
using System.Windows;
using System.Windows.Threading;
using Purge;
using Velopack;
using Velopack.Sources;

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
    /// バックグラウンドでダウンロード済みの更新情報。null以外なら次回起動 or 終了時に適用できる。
    /// </summary>
    private static Velopack.UpdateInfo? _pendingUpdate;

    private static readonly UpdateManager s_updateManager = new(
        new GithubSource("https://github.com/Akatuki1121/Purge", null, false));

    protected override void OnStartup(StartupEventArgs e)
    {
        // Velopackのインストール/アンインストール/更新後起動などのフック処理。
        // 通常起動時は何もせずそのまま処理が戻ってくる。
        VelopackApp.Build().Run();

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

        Exit += OnAppExit;

        // 起動をブロックしないよう、更新チェック～ダウンロードは完全にバックグラウンドで行う。
        // ユーザーへの通知やダイアログは出さず、適用は次回起動時 or 今回の終了時に静かに行う。
        _ = CheckForUpdatesInBackgroundAsync();
    }

    /// <summary>
    /// GitHub Releasesを更新元として、新バージョンの確認とダウンロードをバックグラウンドで行う。
    /// UIは一切ブロックせず、失敗しても(オフライン等)アプリの動作に影響を与えない。
    /// </summary>
    private static async System.Threading.Tasks.Task CheckForUpdatesInBackgroundAsync()
    {
        try
        {
            var newVersion = await s_updateManager.CheckForUpdatesAsync();
            if (newVersion is null)
            {
                return;
            }

            await s_updateManager.DownloadUpdatesAsync(newVersion);
            _pendingUpdate = newVersion;
        }
        catch
        {
            // 更新確認自体の失敗(オフライン、GitHub障害など)は静かに無視する。
            // 次回起動時にまた試みればよく、ユーザー操作を妨げるべきではない。
        }
    }

    /// <summary>
    /// アプリ終了時、ダウンロード済みの更新があれば適用して再起動する。
    /// ユーザーが普段通り「閉じる」を押した瞬間にだけ発動するため、起動時の邪魔にならない。
    /// </summary>
    private static void OnAppExit(object sender, ExitEventArgs e)
    {
        if (_pendingUpdate is not null)
        {
            s_updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
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
