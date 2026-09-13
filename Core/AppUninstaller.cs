using System;
using System.Diagnostics;

namespace Purge
{
    public enum UninstallResult
    {
        Success,
        DryRun,
        NoUninstallString,
        Failed,
    }

    /// <summary>
    /// InstalledAppのUninstallStringを解析し、既存の公式アンインストーラーを起動する。
    ///
    /// 安全のため、既定ではドライラン(実際には実行せずコマンドをログに残すだけ)。
    /// 実際に実行するには ExecuteUninstall(app, dryRun: false) を明示的に呼ぶ必要がある。
    /// テスト用の使い捨てアプリができるまでは dryRun: true のまま使うこと。
    /// </summary>
    public sealed class AppUninstaller
    {
        private readonly OperationLog _log;

        public AppUninstaller(OperationLog log)
        {
            _log = log;
        }

        public UninstallResult ExecuteUninstall(InstalledApp app, bool dryRun = true)
        {
            _log.Info("AppUninstall", "対象アプリを選択", app.DisplayName);

            if (string.IsNullOrWhiteSpace(app.UninstallString))
            {
                _log.Warning("AppUninstall", "UninstallStringが存在しない", app.DisplayName);
                return UninstallResult.NoUninstallString;
            }

            var (fileName, arguments) = ParseUninstallString(app.UninstallString);
            _log.Info("AppUninstall", "アンインストールコマンドを組み立て", $"{fileName} {arguments}");

            if (dryRun)
            {
                _log.Info("AppUninstall", "ドライランのため実行はスキップ", app.DisplayName);
                return UninstallResult.DryRun;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true,
                };

                _log.Info("AppUninstall", "アンインストーラーを起動", app.DisplayName);
                using var process = Process.Start(psi);
                if (process == null)
                {
                    _log.Error("AppUninstall", "プロセス起動に失敗", app.DisplayName);
                    return UninstallResult.Failed;
                }

                process.WaitForExit();
                _log.Info("AppUninstall", "アンインストーラーが終了", $"ExitCode={process.ExitCode}");

                return process.ExitCode == WellKnownConstants.ProcessExitCodeSuccess
                    ? UninstallResult.Success
                    : UninstallResult.Failed;
            }
            catch (Exception ex)
            {
                _log.Error("AppUninstall", "アンインストール実行中にエラー", ex.Message);
                return UninstallResult.Failed;
            }
        }

        /// <summary>
        /// UninstallStringは "実行ファイルパス 引数..." の形式だが、
        /// パスが空白を含む場合はダブルクォートで囲まれている。
        /// これを実行ファイル部分と引数部分に分割する。
        /// </summary>
        internal static (string FileName, string Arguments) ParseUninstallString(string uninstallString)
        {
            var trimmed = uninstallString.Trim();

            if (trimmed.StartsWith('"'))
            {
                var closingQuoteIndex = trimmed.IndexOf('"', 1);
                if (closingQuoteIndex > 0)
                {
                    var fileName = trimmed.Substring(1, closingQuoteIndex - 1);
                    var arguments = trimmed.Length > closingQuoteIndex + 1
                        ? trimmed[(closingQuoteIndex + 1)..].Trim()
                        : "";
                    return (fileName, arguments);
                }
            }

            // クォートなしの場合、最初の空白までを実行ファイルとみなす
            var spaceIndex = trimmed.IndexOf(' ');
            if (spaceIndex < 0)
            {
                return (trimmed, "");
            }

            return (trimmed[..spaceIndex], trimmed[(spaceIndex + 1)..].Trim());
        }
    }
}
