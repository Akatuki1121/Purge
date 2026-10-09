using System.Diagnostics;
using System.Text.Json;

namespace Purge;

/// <summary>
/// 現在のユーザーに登録された、削除可能な MSIX/AppX アプリを列挙する。
/// WinRT API への追加参照を避け、Windows PowerShell の Appx モジュールを利用する。
/// </summary>
internal sealed class AppxPackageInventory
{
    private readonly OperationLog _log;

    public AppxPackageInventory(OperationLog log) => _log = log;

    public List<InstalledApp> GetInstalledApps()
    {
        var apps = new List<InstalledApp>();
        try
        {
            const string script = @"
$ErrorActionPreference = 'Stop'
$startApps = @(Get-StartApps -ErrorAction SilentlyContinue)
$packages = @(Get-AppxPackage | Where-Object {
    -not $_.IsFramework -and
    -not $_.NonRemovable -and
    $_.SignatureKind.ToString() -ne 'System'
})
$items = @(
    foreach ($package in $packages) {
        $appIdPrefix = $package.PackageFamilyName + '!*'
        $startApp = $startApps | Where-Object { $_.AppID -like $appIdPrefix } | Select-Object -First 1
        $displayName = if ($startApp -and -not [string]::IsNullOrWhiteSpace($startApp.Name)) { $startApp.Name } else { $package.Name }
        [pscustomobject]@{
            DisplayName = $displayName
            DisplayVersion = [string]$package.Version
            Publisher = [string]$package.PublisherDisplayName
            InstallLocation = [string]$package.InstallLocation
            PackageFullName = [string]$package.PackageFullName
            PackageFamilyName = [string]$package.PackageFamilyName
        }
    }
)
$json = ConvertTo-Json -InputObject $items -Compress -Depth 3
[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
";
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(script);

            using var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("PowerShellプロセスを起動できませんでした。");

            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Get-AppxPackage に失敗しました: {error.Trim()}");

            if (string.IsNullOrWhiteSpace(output))
                return apps;

            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(output));
            using var document = JsonDocument.Parse(json);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var displayName = GetString(item, "DisplayName");
                var packageFullName = GetString(item, "PackageFullName");
                if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(packageFullName))
                    continue;

                apps.Add(new InstalledApp
                {
                    DisplayName = displayName,
                    DisplayVersion = GetString(item, "DisplayVersion"),
                    Publisher = GetString(item, "Publisher"),
                    InstallLocation = GetString(item, "InstallLocation"),
                    PackageFullName = packageFullName,
                    PackageFamilyName = GetString(item, "PackageFamilyName"),
                    IsStorePackage = true,
                    RegistryKeyPath = $"MSIX/AppX:{packageFullName}",
                });
            }

            _log.Info("AppList", "MSIX/AppXパッケージを列挙", $"{apps.Count}件検出");
        }
        catch (Exception ex)
        {
            _log.Warning("AppList", "MSIX/AppXパッケージの列挙に失敗", ex.Message);
        }

        return apps;
    }

    private static string? GetString(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
