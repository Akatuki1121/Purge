using System;
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
