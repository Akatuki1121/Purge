using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;

namespace UninstallTool
{
    public enum ResidueCategory
    {
        Registry,
        Service,
        ScheduledTask,
        EnvironmentPath,
        Startup,
        MftFile,
    }

    public sealed record ScanProgress(string Category, string CurrentItem, int Percent);

    public sealed class ResidueItem
    {
        public ResidueCategory Category { get; init; }
        public string Location { get; init; } = "";
        public string Detail { get; init; } = "";

        /// <summary>
        /// EnvironmentPathカテゴリの場合、削除時にどちらのPATH変数から除去するかを保持する。
        /// Detailの文言("ユーザーPATH内"等)に依存せず削除ロジックを組めるようにするためのフィールド。
        /// </summary>
        public EnvironmentVariableTarget? PathTarget { get; init; }

        public override string ToString() => $"[{Category}] {Location} {Detail}".TrimEnd();
    }

    /// <summary>
    /// アプリ名/パブリッシャー名を手がかりに、Geek Uninstaller等が見ない領域を含めて
    /// 横断的に残存物を検出する。検出のみ行い、削除はしない(呼び出し側の責任)。
    /// </summary>
    public sealed class ResidueScanner
    {
        private readonly OperationLog _log;
        private readonly MftSearchEngine _mft;

        public ResidueScanner(OperationLog log)
        {
            _log = log;
            _mft = new MftSearchEngine(log);
        }

        /// <summary>
        /// アプリ名を軸に、全カテゴリを横断してスキャンする。
        /// MFT検索はオプション(重いため既定はfalse、必要な時だけ呼び出し側でtrueにする)。
        /// </summary>
        public List<ResidueItem> ScanAll(string appName, bool includeMftSearch = false,
            string mftDrive = WellKnownConstants.DefaultMftSearchDrive,
            CancellationToken cancellationToken = default, IProgress<ScanProgress>? progress = null)
        {
            _log.Info("ResidueScan", "横断残存物スキャンを開始", appName);
            var results = new List<ResidueItem>();

            progress?.Report(new ScanProgress("準備", appName, 0));
            results.AddRange(ScanRegistry(appName, cancellationToken, progress));
            results.AddRange(ScanServices(appName, cancellationToken, progress));
            results.AddRange(ScanScheduledTasks(appName, cancellationToken, progress));
            results.AddRange(ScanEnvironmentPath(appName, cancellationToken, progress));
            results.AddRange(ScanStartup(appName, cancellationToken, progress));

            if (includeMftSearch)
            {
                results.AddRange(ScanMftFiles(appName, mftDrive, cancellationToken, progress));
            }

            _log.Info("ResidueScan", "横断残存物スキャン完了", $"{results.Count}件");
            return results;
        }

        /// <summary>
        /// HKCU/HKLMの主要ハイブを、Uninstallキーに限らず文字列一致で横断検索する。
        /// 深い探索はコストが高いため、代表的な場所(Software直下、数階層まで)に絞る。
        /// </summary>
        private List<ResidueItem> ScanRegistry(string appName, CancellationToken cancellationToken, IProgress<ScanProgress>? progress)
        {
            _log.Info("ResidueScan", "レジストリ横断検索を開始", appName);
            var results = new List<ResidueItem>();

            var roots = new (RegistryKey Hive, string SubKey)[]
            {
                (Registry.CurrentUser, @"Software"),
                (Registry.LocalMachine, @"Software"),
                (Registry.LocalMachine, @"Software\WOW6432Node"),
            };

            foreach (var (hive, subKeyPath) in roots)
            {
                try
                {
                    using var root = hive.OpenSubKey(subKeyPath);
                    if (root == null) continue;

                    foreach (var childName in root.GetSubKeyNames())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(new ScanProgress("レジストリ", childName, 0));
                        if (NameMatcher.IsSafeMatch(childName, appName))
                        {
                            results.Add(new ResidueItem
                            {
                                Category = ResidueCategory.Registry,
                                Location = $@"{hive.Name}\{subKeyPath}\{childName}",
                                Detail = "キー名がアプリ名と一致",
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning("ResidueScan", "レジストリ検索でエラー", $"{hive.Name}\\{subKeyPath}: {ex.Message}");
                }
            }

            _log.Info("ResidueScan", "レジストリ横断検索完了", $"{results.Count}件");
            return results;
        }

        /// <summary>
        /// Windowsサービス登録の中からアプリ名に一致するものを探す。
        /// </summary>
        private List<ResidueItem> ScanServices(string appName, CancellationToken cancellationToken, IProgress<ScanProgress>? progress)
        {
            _log.Info("ResidueScan", "サービス登録を確認", appName);
            var results = new List<ResidueItem>();

            try
            {
                foreach (var service in ServiceController.GetServices())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new ScanProgress("サービス", service.DisplayName, 0));
                    if (NameMatcher.IsSafeMatch(service.ServiceName, appName) ||
                        NameMatcher.IsSafeMatch(service.DisplayName, appName))
                    {
                        results.Add(new ResidueItem
                        {
                            Category = ResidueCategory.Service,
                            Location = service.ServiceName,
                            Detail = $"表示名: {service.DisplayName}, 状態: {service.Status}",
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning("ResidueScan", "サービス一覧取得でエラー", ex.Message);
            }

            _log.Info("ResidueScan", "サービス登録確認完了", $"{results.Count}件");
            return results;
        }

        /// <summary>
        /// タスクスケジューラのタスクフォルダ(レジストリ経由)からアプリ名一致を探す。
        /// schtasks.exeを使わず、レジストリのタスク定義を直接読む簡易実装。
        /// </summary>
        private List<ResidueItem> ScanScheduledTasks(string appName, CancellationToken cancellationToken, IProgress<ScanProgress>? progress)
        {
            _log.Info("ResidueScan", "タスクスケジューラのエントリを確認", appName);
            var results = new List<ResidueItem>();

            const string taskCacheTasksPath = WellKnownConstants.RegistryKeyPaths.ScheduledTaskCacheSubKey;

            try
            {
                using var tasksKey = Registry.LocalMachine.OpenSubKey(taskCacheTasksPath);
                if (tasksKey == null)
                {
                    _log.Warning("ResidueScan", "タスクキャッシュキーが見つからない", taskCacheTasksPath);
                    return results;
                }

                foreach (var taskGuid in tasksKey.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new ScanProgress("タスク", taskGuid, 0));
                    using var taskKey = tasksKey.OpenSubKey(taskGuid);
                    var taskPath = taskKey?.GetValue(WellKnownConstants.RegistryValueNames.ScheduledTaskPath) as string;

                    if (!string.IsNullOrEmpty(taskPath) &&
                        NameMatcher.IsSafeMatchAnywhere(taskPath, appName))
                    {
                        results.Add(new ResidueItem
                        {
                            Category = ResidueCategory.ScheduledTask,
                            Location = taskPath,
                            Detail = $"タスクID: {taskGuid}",
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning("ResidueScan", "タスクスケジューラ確認でエラー", ex.Message);
            }

            _log.Info("ResidueScan", "タスクスケジューラ確認完了", $"{results.Count}件");
            return results;
        }

        /// <summary>
        /// システム/ユーザーのPATH環境変数に、アプリ名を含むパスが残っていないか確認する。
        /// </summary>
        private List<ResidueItem> ScanEnvironmentPath(string appName, CancellationToken cancellationToken, IProgress<ScanProgress>? progress)
        {
            _log.Info("ResidueScan", "環境変数PATHを確認", appName);
            var results = new List<ResidueItem>();

            var targets = new[]
            {
                (EnvironmentVariableTarget.User, "ユーザー"),
                (EnvironmentVariableTarget.Machine, "システム"),
            };

            foreach (var (target, label) in targets)
            {
                try
                {
                    var pathValue = Environment.GetEnvironmentVariable(
                        WellKnownConstants.PathEnvironmentVariableName, target) ?? "";
                    var entries = pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries);

                    foreach (var entry in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(new ScanProgress("PATH", entry, 0));
                        if (NameMatcher.IsSafeMatchAnywhere(entry, appName))
                        {
                            results.Add(new ResidueItem
                            {
                                Category = ResidueCategory.EnvironmentPath,
                                Location = entry,
                                Detail = $"{label}PATH内",
                                PathTarget = target,
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning("ResidueScan", "PATH確認でエラー", $"{label}: {ex.Message}");
                }
            }

            _log.Info("ResidueScan", "環境変数PATH確認完了", $"{results.Count}件");
            return results;
        }

        /// <summary>
        /// スタートアップ項目(レジストリRunキー + スタートアップフォルダ)を確認する。
        /// </summary>
        private List<ResidueItem> ScanStartup(string appName, CancellationToken cancellationToken, IProgress<ScanProgress>? progress)
        {
            _log.Info("ResidueScan", "スタートアップ項目を確認", appName);
            var results = new List<ResidueItem>();

            var runKeyLocations = new (RegistryKey Hive, string SubKey)[]
            {
                (Registry.CurrentUser, WellKnownConstants.RegistryKeyPaths.RunSubKey),
                (Registry.LocalMachine, WellKnownConstants.RegistryKeyPaths.RunSubKey),
            };

            foreach (var (hive, subKeyPath) in runKeyLocations)
            {
                try
                {
                    using var runKey = hive.OpenSubKey(subKeyPath);
                    if (runKey == null) continue;

                    foreach (var valueName in runKey.GetValueNames())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(new ScanProgress("スタートアップ", valueName, 0));
                        var value = runKey.GetValue(valueName) as string ?? "";
                        if (NameMatcher.IsSafeMatch(valueName, appName) ||
                            NameMatcher.IsSafeMatchAnywhere(value, appName))
                        {
                            results.Add(new ResidueItem
                            {
                                Category = ResidueCategory.Startup,
                                Location = $@"{hive.Name}\{subKeyPath}\{valueName}",
                                Detail = value,
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning("ResidueScan", "スタートアップRunキー確認でエラー", ex.Message);
                }
            }

            try
            {
                var startupFolders = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                };

                foreach (var folder in startupFolders)
                {
                    if (!Directory.Exists(folder)) continue;

                    foreach (var file in Directory.EnumerateFiles(folder))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(new ScanProgress("スタートアップ", Path.GetFileName(file), 0));
                        if (NameMatcher.IsSafeMatch(Path.GetFileName(file), appName))
                        {
                            results.Add(new ResidueItem
                            {
                                Category = ResidueCategory.Startup,
                                Location = file,
                                Detail = "スタートアップフォルダ内のショートカット",
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning("ResidueScan", "スタートアップフォルダ確認でエラー", ex.Message);
            }

            _log.Info("ResidueScan", "スタートアップ項目確認完了", $"{results.Count}件");
            return results;
        }

        /// <summary>
        /// Phase4のMFT高速検索エンジンを使い、ファイルシステム全体からアプリ名一致を探す。
        /// 他のスキャンより大幅に時間がかかるため、呼び出し側の判断でオプトインさせる。
        /// </summary>
        private List<ResidueItem> ScanMftFiles(string appName, string driveLetter,
            CancellationToken cancellationToken, IProgress<ScanProgress>? progress)
        {
            progress?.Report(new ScanProgress("MFT", $"{driveLetter}: 全体を走査中", 0));
            var paths = _mft.Search(driveLetter, appName, cancellationToken,
                new Progress<int>(percent => progress?.Report(new ScanProgress("MFT", $"{driveLetter}: 全体を走査中", percent))));
            return paths.Select(p => new ResidueItem
            {
                Category = ResidueCategory.MftFile,
                Location = p,
                Detail = "MFT検索でファイル名/フォルダ名が一致",
            }).ToList();
        }
    }
}
