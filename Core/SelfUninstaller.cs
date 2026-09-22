using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Purge
{
    /// <summary>
    /// 実行中の自分自身を含め、アプリの痕跡を完全に消去する機能。
    /// 以前はexe・フォルダを手動でdel/rmdirする方式だったが、ショートカットやレジストリ値、
    /// Windowsのインストール登録(プログラムと機能)が消し残る欠陥があった。
    /// 現在はWindows Installerに登録された自分自身のアンインストールを`msiexec /x`で
    /// 呼び出す方式にし、MSIが把握している範囲(ファイル・ショートカット・レジストリ・
    /// インストール登録)をまとめて正しく削除させる。
    /// </summary>
    public static class SelfUninstaller
    {
        /// <summary>
        /// 自己アンインストールを開始する。この呼び出し後、呼び出し側は速やかに
        /// Environment.Exit や Application.Shutdown でプロセスを終了させる必要がある
        /// (batはプロセスのPIDを見て終了を待つため)。
        /// </summary>
        public static void BeginSelfUninstall(OperationLog log, string? exePath = null)
        {
            exePath ??= Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                log.Error("SelfUninstall", "実行ファイルパスの取得に失敗したため自己削除を中止");
                return;
            }

            var productCode = FindInstalledProductCode();
            if (string.IsNullOrEmpty(productCode))
            {
                log.Error("SelfUninstall", "MSIのインストール登録(ProductCode)が見つからないため自己削除を中止。" +
                    "MSI経由でインストールされていない可能性があります。");
                return;
            }

            var currentPid = Environment.ProcessId;
            var batPath = Path.Combine(Path.GetTempPath(), $"uninstall_self_{Guid.NewGuid():N}.bat");

            log.Info("SelfUninstall", "自己削除用batファイルを生成", batPath);

            var batContent = BuildBatchContent(currentPid, productCode);

            File.WriteAllText(batPath, batContent);

            var psi = new ProcessStartInfo
            {
                FileName = batPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Process.Start(psi);
            log.Info("SelfUninstall", "削除用batを起動、プロセス終了待機に入りました", batPath);
        }

        /// <summary>
        /// レジストリの「プログラムと機能」一覧から、DisplayNameが"Purge"のエントリを探し、
        /// そのProductCode(GUID)を返す。見つからない場合はnull。
        /// </summary>
        internal static string? FindInstalledProductCode()
        {
            const string uninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            using var key = Registry.LocalMachine.OpenSubKey(uninstallKeyPath);
            if (key is null) return null;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(subKeyName);
                var displayName = subKey?.GetValue("DisplayName") as string;
                if (displayName == "Purge")
                {
                    // サブキー名自体がProductCode(波括弧付きGUID)になっている。
                    return subKeyName;
                }
            }

            return null;
        }

        internal static string BuildBatchContent(int processId, string productCode)
        {
            // タスクバーやエクスプローラーへの解放猶予を含め、PIDの終了をポーリング待機してから
            // msiexecでアンインストールする。ファイル・ショートカット・レジストリ・インストール
            // 登録の削除はすべてWindows Installerに任せる(手動delete方式は消し残しリスクがあるため廃止)。
            return $"""
                @echo off
                :waitloop
                tasklist /FI "PID eq {processId}" 2>NUL | find "{processId}" >NUL
                if not errorlevel 1 (
                    timeout /t 1 /nobreak >NUL
                    goto waitloop
                )
                msiexec /x {productCode} /qn /norestart
                del /f /q "%~f0"
                """;
        }
    }
}
