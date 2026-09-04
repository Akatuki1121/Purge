using System.Diagnostics;
using System.Text.Json;

namespace UninstallTool;

public sealed class RemovalBackupWriter
{
    private readonly OperationLog _log;

    public RemovalBackupWriter(OperationLog log)
    {
        _log = log;
    }

    public string Write(IReadOnlyCollection<ResidueItem> items, string? directory = null)
    {
        var backupDirectory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UninstallTool", "RemovalBackups");
        Directory.CreateDirectory(backupDirectory);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var manifestPath = Path.Combine(backupDirectory, $"Removal_{timestamp}_{Guid.NewGuid():N}.json");
        var entries = new List<RemovalBackupEntry>();

        foreach (var item in items)
        {
            var entry = new RemovalBackupEntry
            {
                Category = item.Category.ToString(),
                Location = item.Location,
                Detail = item.Detail,
                Confidence = item.Confidence.ToString(),
                ExistsBeforeRemoval = GetExistence(item),
            };

            if (item.Category == ResidueCategory.Registry)
            {
                entry.RegistryBackupPath = ExportRegistryKey(item.Location, backupDirectory, timestamp);
            }
            else if (item.Category is ResidueCategory.MftFile or ResidueCategory.Startup)
            {
                entry.SnapshotPath = SnapshotFile(item.Location, backupDirectory, entries.Count);
            }
            else if (item.Category is ResidueCategory.Service or ResidueCategory.ScheduledTask)
            {
                entry.DefinitionSnapshot = CaptureCommandOutput(item);
            }

            entries.Add(entry);
        }

        var manifest = new RemovalBackupManifest
        {
            CreatedAt = DateTimeOffset.Now,
            Entries = entries,
        };
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(manifestPath, json);
        _log.Info("ResidueBackup", "削除前マニフェストを保存", manifestPath);
        return manifestPath;
    }

    private static bool? GetExistence(ResidueItem item)
    {
        if (item.Category is ResidueCategory.Registry or ResidueCategory.Startup)
        {
            return item.Location.StartsWith("HKEY_", StringComparison.Ordinal)
                ? RegistryPathExists(item.Location)
                : File.Exists(item.Location);
        }

        return item.Category == ResidueCategory.MftFile
            ? File.Exists(item.Location) || Directory.Exists(item.Location)
            : null;
    }

    private static bool RegistryPathExists(string path)
    {
        var separator = path.IndexOf('\\');
        if (separator <= 0) return false;

        var rootName = path[..separator];
        var subPath = path[(separator + 1)..];
        var root = new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine }
            .FirstOrDefault(key => key.Name.Equals(rootName, StringComparison.OrdinalIgnoreCase));
        using var key = root?.OpenSubKey(subPath);
        return key != null;
    }

    private static string ExportRegistryKey(string registryPath, string directory, string timestamp)
    {
        var fileName = $"Registry_{timestamp}_{SanitizeFileName(registryPath)}.reg";
        var outputPath = Path.Combine(directory, fileName);
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "reg.exe",
            Arguments = $"export \"{registryPath}\" \"{outputPath}\" /y",
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("reg.exeを起動できませんでした");
        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException($"レジストリバックアップに失敗しました: {registryPath}");
        }

        return outputPath;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static string? SnapshotFile(string path, string directory, int index)
    {
        if (File.Exists(path))
        {
            var target = Path.Combine(directory, $"File_{index}_{SanitizeFileName(Path.GetFileName(path))}");
            File.Copy(path, target, overwrite: true);
            return target;
        }

        if (Directory.Exists(path))
        {
            var target = Path.Combine(directory, $"Folder_{index}_{SanitizeFileName(Path.GetFileName(path))}");
            CopyDirectory(path, target);
            return target;
        }

        return null;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var child in Directory.GetDirectories(source))
        {
            CopyDirectory(child, Path.Combine(target, Path.GetFileName(child)));
        }
    }

    private static string? CaptureCommandOutput(ResidueItem item)
    {
        var (fileName, arguments) = item.Category == ResidueCategory.Service
            ? ("sc.exe", $"qc \"{item.Location}\"")
            : ("schtasks.exe", $"/Query /TN \"{item.Location}\" /XML");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        if (process == null) return null;
        process.WaitForExit();
        return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd() : null;
    }
}

public sealed class RemovalBackupManifest
{
    public DateTimeOffset CreatedAt { get; init; }
    public List<RemovalBackupEntry> Entries { get; init; } = new();
}

public sealed class RemovalBackupEntry
{
    public string Category { get; init; } = "";
    public string Location { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Confidence { get; init; } = "";
    public bool? ExistsBeforeRemoval { get; init; }
    public string? RegistryBackupPath { get; set; }
    public string? SnapshotPath { get; set; }
    public string? DefinitionSnapshot { get; set; }
}