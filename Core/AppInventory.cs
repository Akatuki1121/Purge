using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Purge
{
    /// <summary>
    /// レジストリのUninstallキーから読み取った1アプリ分の情報。
    /// </summary>
    public sealed class InstalledApp
    {
        public string DisplayName { get; init; } = "";
        public string? DisplayVersion { get; init; }
        public string? Publisher { get; init; }
        public string? InstallLocation { get; init; }
        public string? UninstallString { get; init; }
        public string? DisplayIconPath { get; init; }
        public string RegistryKeyPath { get; init; } = "";

        public override string ToString()
        {
            var version = string.IsNullOrEmpty(DisplayVersion) ? "" : $" v{DisplayVersion}";
            var publisher = string.IsNullOrEmpty(Publisher) ? "" : $" / {Publisher}";
            return $"{DisplayName}{version}{publisher}";
        }
    }

    /// <summary>
    /// レジストリのUninstallキーを横断的に読み取り、インストール済みアプリの一覧を取得する。
    /// 32bit/64bit、マシン全体/ユーザー単位の4系統を全てカバーする。
    /// </summary>
    public sealed class AppInventory
    {
        private readonly OperationLog _log;

        // (レジストリハイブ, サブキーパス) の組で4系統を定義
        private static readonly (RegistryHive Hive, string SubKey)[] UninstallKeyLocations = new[]
        {
            (RegistryHive.LocalMachine, WellKnownConstants.RegistryKeyPaths.UninstallSubKey),
            (RegistryHive.LocalMachine, WellKnownConstants.RegistryKeyPaths.UninstallSubKeyWow6432),
            (RegistryHive.CurrentUser, WellKnownConstants.RegistryKeyPaths.UninstallSubKey),
        };

        public AppInventory(OperationLog log)
        {
            _log = log;
        }

        public List<InstalledApp> GetInstalledApps()
        {
            _log.Info("AppList", "インストール済みアプリ一覧を取得開始");
            var apps = new List<InstalledApp>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (hive, subKey) in UninstallKeyLocations)
            {
                ScanUninstallKey(hive, subKey, apps, seenNames);
            }

            _log.Info("AppList", $"アプリ一覧取得完了", $"{apps.Count}件検出");
            return apps;
        }

        private void ScanUninstallKey(RegistryHive hive, string subKeyPath, List<InstalledApp> apps, HashSet<string> seenNames)
        {
            var fullPath = $@"{hive}\{subKeyPath}";
            _log.Info("AppList", "レジストリUninstallキーを列挙", fullPath);

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var uninstallKey = baseKey.OpenSubKey(subKeyPath);

                if (uninstallKey == null)
                {
                    _log.Warning("AppList", "Uninstallキーが存在しない", fullPath);
                    return;
                }

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using var appKey = uninstallKey.OpenSubKey(subKeyName);
                        if (appKey == null) continue;

                        var displayName = appKey.GetValue(WellKnownConstants.RegistryValueNames.DisplayName) as string;

                        // DisplayNameが無い、またはシステムコンポーネント扱いのものはスキップ
                        if (string.IsNullOrWhiteSpace(displayName))
                            continue;

                        var isSystemComponent =
                            (appKey.GetValue(WellKnownConstants.RegistryValueNames.SystemComponent) as int?)
                            == WellKnownConstants.RegistryValueNames.SystemComponentTrueValue;
                        if (isSystemComponent)
                            continue;

                        // 同名アプリの重複除去(32bit/64bit両方に登録されているケースがある)
                        if (!seenNames.Add(displayName))
                            continue;

                        var app = new InstalledApp
                        {
                            DisplayName = displayName,
                            DisplayVersion = appKey.GetValue(WellKnownConstants.RegistryValueNames.DisplayVersion) as string,
                            Publisher = appKey.GetValue(WellKnownConstants.RegistryValueNames.Publisher) as string,
                            InstallLocation = appKey.GetValue(WellKnownConstants.RegistryValueNames.InstallLocation) as string,
                            UninstallString = appKey.GetValue(WellKnownConstants.RegistryValueNames.UninstallString) as string,
                            DisplayIconPath = appKey.GetValue(WellKnownConstants.RegistryValueNames.DisplayIcon) as string,
                            RegistryKeyPath = $@"{fullPath}\{subKeyName}",
                        };

                        apps.Add(app);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("AppList", $"サブキー読み取りでエラー", $"{subKeyName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("AppList", "Uninstallキーへのアクセスでエラー", $"{fullPath}: {ex.Message}");
            }
        }
    }
}
