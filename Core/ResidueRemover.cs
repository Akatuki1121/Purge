using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Purge
{
    public enum RemovalResult
    {
        Success,
        DryRun,
        Failed,
        NotSupported,
    }

    /// <summary>
    /// ResidueScannerが検出した項目をカテゴリごとの方法で削除する。
    /// 安全のため既定はドライラン。ユーザーが選択した項目のみ削除する前提の設計。
    /// </summary>
    public sealed class ResidueRemover
    {
        private readonly OperationLog _log;

        /// <summary>レジストリパス文字列の先頭に必ず付くプレフィックス(HKEY_CURRENT_USER等)の共通部分。</summary>
        private const string RegistryPathPrefix = "HKEY_";

        public ResidueRemover(OperationLog log)
        {
            _log = log;
        }

        public RemovalResult Remove(ResidueItem item, bool dryRun = true)
        {
            _log.Info("ResidueRemove", $"削除対象: [{item.Category}] {item.Location}");

            if (dryRun)
            {
                _log.Info("ResidueRemove", "ドライランのため削除はスキップ", item.Location);
                return RemovalResult.DryRun;
            }

            return item.Category switch
            {
                ResidueCategory.Registry => RemoveRegistryKey(item),
                ResidueCategory.Service => RemoveService(item),
                ResidueCategory.ScheduledTask => RemoveScheduledTask(item),
                ResidueCategory.EnvironmentPath => RemovePathEntry(item),
                ResidueCategory.Startup => RemoveStartupItem(item),
                ResidueCategory.MftFile => RemoveMftFile(item),
                _ => RemovalResult.NotSupported,
            };
        }

        /// <summary>
        /// "HKEY_CURRENT_USER\Software\Foo" のような文字列を (RegistryKey親, サブパス) に分解する。
        /// ルート名の比較は "HKEY_CURRENT_USER" 等のマジックストリングを直接書かず、
        /// Registry.CurrentUser.Name 等の実際の値と照合することでズレを防ぐ。
        /// </summary>
        private static (RegistryKey? Root, string SubPath) SplitRegistryPath(string fullPath)
        {
            var parts = fullPath.Split('\\', 2);
            if (parts.Length != 2) return (null, "");

            var knownRoots = new[] { Registry.CurrentUser, Registry.LocalMachine };
            var root = knownRoots.FirstOrDefault(r => r.Name == parts[0]);

            return (root, parts[1]);
        }

        private RemovalResult RemoveRegistryKey(ResidueItem item)
        {
            var (root, subPath) = SplitRegistryPath(item.Location);
            if (root == null)
            {
                _log.Error("ResidueRemove", "レジストリルートの解釈に失敗", item.Location);
                return RemovalResult.Failed;
            }

            try
            {
                // 親キーと削除対象のキー名に分割
                var lastSlash = subPath.LastIndexOf('\\');
                if (lastSlash < 0)
                {
                    _log.Error("ResidueRemove", "レジストリパスの分解に失敗", item.Location);
                    return RemovalResult.Failed;
                }

                var parentPath = subPath[..lastSlash];
                var keyName = subPath[(lastSlash + 1)..];

                using var parentKey = root.OpenSubKey(parentPath, writable: true);
                if (parentKey == null)
                {
                    _log.Warning("ResidueRemove", "親キーが見つからない(既に削除済みの可能性)", item.Location);
                    return RemovalResult.Success;
                }

                parentKey.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
                _log.Info("ResidueRemove", "レジストリキーを削除", item.Location);
                return RemovalResult.Success;
            }
            catch (Exception ex)
            {
                _log.Error("ResidueRemove", "レジストリキー削除でエラー", $"{item.Location}: {ex.Message}");
                return RemovalResult.Failed;
            }
        }

        /// <summary>
        /// サービスの削除は sc.exe delete を呼ぶ(.NETに直接のサービス削除APIが無いため)。
        /// </summary>
        private RemovalResult RemoveService(ResidueItem item)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = WellKnownConstants.ExternalTools.ServiceControl,
                    Arguments = string.Format(
                        WellKnownConstants.ExternalTools.ServiceControlDeleteArgsFormat, item.Location),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    _log.Error("ResidueRemove", "sc.exeの起動に失敗", item.Location);
                    return RemovalResult.Failed;
                }

                process.WaitForExit();
                if (process.ExitCode == WellKnownConstants.ProcessExitCodeSuccess)
                {
                    _log.Info("ResidueRemove", "サービスを削除", item.Location);
                    return RemovalResult.Success;
                }

                var stderr = process.StandardError.ReadToEnd();
                _log.Error("ResidueRemove", "サービス削除に失敗", $"{item.Location}: {stderr}");
                return RemovalResult.Failed;
            }
            catch (Exception ex)
            {
                _log.Error("ResidueRemove", "サービス削除でエラー", $"{item.Location}: {ex.Message}");
                return RemovalResult.Failed;
            }
        }

        /// <summary>
        /// タスクスケジューラのタスク削除は schtasks.exe /Delete を呼ぶ。
        /// </summary>
        private RemovalResult RemoveScheduledTask(ResidueItem item)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = WellKnownConstants.ExternalTools.TaskScheduler,
                    Arguments = string.Format(
                        WellKnownConstants.ExternalTools.TaskSchedulerDeleteArgsFormat, item.Location),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    _log.Error("ResidueRemove", "schtasks.exeの起動に失敗", item.Location);
                    return RemovalResult.Failed;
                }

                process.WaitForExit();
                if (process.ExitCode == WellKnownConstants.ProcessExitCodeSuccess)
                {
                    _log.Info("ResidueRemove", "タスクを削除", item.Location);
                    return RemovalResult.Success;
                }

                var stderr = process.StandardError.ReadToEnd();
                _log.Error("ResidueRemove", "タスク削除に失敗", $"{item.Location}: {stderr}");
                return RemovalResult.Failed;
            }
            catch (Exception ex)
            {
                _log.Error("ResidueRemove", "タスク削除でエラー", $"{item.Location}: {ex.Message}");
                return RemovalResult.Failed;
            }
        }

        /// <summary>
        /// PATH環境変数から該当エントリのみを除去する(他のエントリは残す)。
        /// item.PathTarget で削除対象がユーザー/システムのどちらか判定する
        /// (Detailの文言に依存しない設計。PathTargetが無い場合は不整合として失敗扱いにする)。
        /// </summary>
        private RemovalResult RemovePathEntry(ResidueItem item)
        {
            if (item.PathTarget is not { } target)
            {
                _log.Error("ResidueRemove", "PathTargetが未設定のため削除対象を特定できない", item.Location);
                return RemovalResult.Failed;
            }

            try
            {
                var current = Environment.GetEnvironmentVariable(
                    WellKnownConstants.PathEnvironmentVariableName, target) ?? "";
                var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Where(e => !string.Equals(e, item.Location, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                var newValue = string.Join(';', entries);
                Environment.SetEnvironmentVariable(
                    WellKnownConstants.PathEnvironmentVariableName, newValue, target);

                _log.Info("ResidueRemove", "PATHからエントリを除去", item.Location);
                return RemovalResult.Success;
            }
            catch (Exception ex)
            {
                _log.Error("ResidueRemove", "PATH編集でエラー", $"{item.Location}: {ex.Message}");
                return RemovalResult.Failed;
            }
        }

        /// <summary>
        /// スタートアップ項目の削除。レジストリRun値、またはスタートアップフォルダ内のファイルの2パターン。
        /// </summary>
        private RemovalResult RemoveStartupItem(ResidueItem item)
        {
            try
            {
                if (item.Location.StartsWith(RegistryPathPrefix, StringComparison.Ordinal))
                {
                    var (root, subPath) = SplitRegistryPath(item.Location);
                    if (root == null)
                    {
                        return RemovalResult.Failed;
                    }

                    var lastSlash = subPath.LastIndexOf('\\');
                    var parentPath = subPath[..lastSlash];
                    var valueName = subPath[(lastSlash + 1)..];

                    using var key = root.OpenSubKey(parentPath, writable: true);
                    key?.DeleteValue(valueName, throwOnMissingValue: false);

                    _log.Info("ResidueRemove", "スタートアップRun値を削除", item.Location);
                    return RemovalResult.Success;
                }
                else
                {
                    if (File.Exists(item.Location))
                    {
                        File.Delete(item.Location);
                    }
                    _log.Info("ResidueRemove", "スタートアップフォルダのファイルを削除", item.Location);
                    return RemovalResult.Success;
                }
            }
            catch (Exception ex)
            {
                _log.Error("ResidueRemove", "スタートアップ項目削除でエラー", $"{item.Location}: {ex.Message}");
                return RemovalResult.Failed;
            }
        }

        /// <summary>
        /// MFT検索で見つかったファイル/フォルダの削除。
        /// フォルダの場合は中身ごと削除(再帰)するため、ユーザー確認は呼び出し側で必須とする。
        /// </summary>
        private RemovalResult RemoveMftFile(ResidueItem item)
        {
            try
            {
                if (Directory.Exists(item.Location))
                {
                    Directory.Delete(item.Location, recursive: true);
                    _log.Info("ResidueRemove", "フォルダを削除", item.Location);
                    return RemovalResult.Success;
                }

                if (File.Exists(item.Location))
                {
                    File.Delete(item.Location);
                    _log.Info("ResidueRemove", "ファイルを削除", item.Location);
                    return RemovalResult.Success;
                }

                _log.Warning("ResidueRemove", "対象が既に存在しない", item.Location);
                return RemovalResult.Success;
            }
            catch (Exception ex)
            {
                _log.Error("ResidueRemove", "ファイル/フォルダ削除でエラー", $"{item.Location}: {ex.Message}");
                return RemovalResult.Failed;
            }
        }
    }
}
