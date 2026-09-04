using System.Diagnostics;
using System.Text.Json;

namespace UninstallTool;

public sealed class RemovalRestoreService
{
    private readonly OperationLog _log;

    public RemovalRestoreService(OperationLog log)
    {
        _log = log;
    }

    public int Restore(string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<RemovalBackupManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("復元マニフェストを読み込めませんでした");
        var restored = 0;

        foreach (var entry in manifest.Entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.RegistryBackupPath) && RestoreRegistry(entry.RegistryBackupPath))
            {
                restored++;
            }
            else if (!string.IsNullOrWhiteSpace(entry.SnapshotPath) && RestoreSnapshot(entry.Location, entry.SnapshotPath))
            {
                restored++;
            }
            else if (!string.IsNullOrWhiteSpace(entry.DefinitionSnapshot))
            {
                _log.Warning("ResidueRestore", "サービス/タスク定義は記録済みですが自動復元には未対応", entry.Location);
            }
        }

        _log.Info("ResidueRestore", "バックアップからの復元完了", $"{restored}件");
        return restored;
    }

    private bool RestoreRegistry(string backupPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "reg.exe",
            Arguments = $"import \"{backupPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process == null) return false;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static bool RestoreSnapshot(string location, string snapshotPath)
    {
        if (Directory.Exists(snapshotPath))
        {
            CopyDirectory(snapshotPath, location);
            return true;
        }

        if (File.Exists(snapshotPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(location) ?? ".");
            File.Copy(snapshotPath, location, overwrite: true);
            return true;
        }

        return false;
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
}