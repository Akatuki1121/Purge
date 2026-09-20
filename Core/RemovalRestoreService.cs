using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Purge;

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
                if (entry.Category == nameof(ResidueCategory.ScheduledTask)
                    && RestoreScheduledTask(entry.Location, entry.DefinitionSnapshot))
                {
                    restored++;
                }
                else if (entry.Category == nameof(ResidueCategory.Service)
                    && RestoreService(entry.Location, entry.DefinitionSnapshot))
                {
                    restored++;
                }
                else
                {
                    _log.Warning("ResidueRestore", "サービス/タスク定義の自動復元に失敗しました。手動での再作成が必要です", entry.Location);
                }
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

    /// <summary>
    /// タスクスケジューラの定義を復元する。RemovalBackupWriterが
    /// `schtasks /Query /TN &lt;name&gt; /XML` の出力(タスク定義そのもののXML)を
    /// DefinitionSnapshotとして保存しているため、それを一時ファイルへ書き出し、
    /// `schtasks /Create /XML` でそのまま再登録できる。
    /// </summary>
    private bool RestoreScheduledTask(string taskName, string definitionXml)
    {
        var tempXmlPath = Path.Combine(Path.GetTempPath(), $"PurgeTaskRestore_{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks /Query /XML の出力はUTF-16 LEのXML宣言を含むことがあるため、
            // 受け取った文字列をそのままUTF-8で書き出すと文字化けする場合がある。
            // .NETのFile.WriteAllTextは既定でUTF-8(BOM無し)だが、schtasksは
            // Unicode(UTF-16)を要求するため明示的に指定する。
            File.WriteAllText(tempXmlPath, definitionXml, System.Text.Encoding.Unicode);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Create /TN \"{taskName}\" /XML \"{tempXmlPath}\" /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });
            if (process == null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        finally
        {
            if (File.Exists(tempXmlPath)) File.Delete(tempXmlPath);
        }
    }

    /// <summary>
    /// Windowsサービスの定義を復元する。RemovalBackupWriterが
    /// `sc qc &lt;name&gt;` の出力(人間可読なキー: 値の形式)をDefinitionSnapshotとして
    /// 保存しているため、必要な項目を正規表現で抽出し、`sc create` で再登録する。
    ///
    /// 復元できるのはサービス定義(実行ファイルパス、開始種別、表示名、依存関係)のみで、
    /// サービス固有の内部状態は当然復元されない。
    /// </summary>
    private bool RestoreService(string serviceName, string definitionText)
    {
        var binaryPath = ExtractValue(definitionText, "BINARY_PATH_NAME");
        if (string.IsNullOrWhiteSpace(binaryPath)) return false;

        var startTypeRaw = ExtractValue(definitionText, "START_TYPE");
        var startType = startTypeRaw switch
        {
            var s when s != null && s.Contains("AUTO_START") => "auto",
            var s when s != null && s.Contains("DEMAND_START") => "demand",
            var s when s != null && s.Contains("DISABLED") => "disabled",
            _ => "demand",
        };

        var displayName = ExtractValue(definitionText, "DISPLAY_NAME") ?? serviceName;

        var arguments = $"create \"{serviceName}\" binPath= \"{binaryPath}\" start= {startType} DisplayName= \"{displayName}\"";

        var dependencies = ExtractDependencies(definitionText);
        if (dependencies.Count > 0)
        {
            arguments += $" depend= \"{string.Join('/', dependencies)}\"";
        }

        using var createProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (createProcess == null) return false;
        createProcess.WaitForExit();
        return createProcess.ExitCode == 0;
    }

    internal static string? ExtractValue(string text, string key)
    {
        // "KEY                : value" の形式(コロン前の空白幅は可変)から値を取り出す。
        // 行末までを値とし、前後の空白をトリムする。
        var match = Regex.Match(text, $@"^\s*{Regex.Escape(key)}\s*:\s*(.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    internal static List<string> ExtractDependencies(string text)
    {
        // DEPENDENCIES行は複数行にまたがることがあり、継続行は
        // "                           : 値" の形式(キー名を伴わない)で並ぶ。
        var lines = text.Split('\n');
        var dependencies = new List<string>();
        var inDependencies = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.TrimStart().StartsWith("DEPENDENCIES", StringComparison.Ordinal))
            {
                inDependencies = true;
                var value = ExtractValue(line, "DEPENDENCIES");
                if (!string.IsNullOrWhiteSpace(value)) dependencies.Add(value);
                continue;
            }

            if (inDependencies)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith(':'))
                {
                    var value = trimmed[1..].Trim();
                    if (!string.IsNullOrWhiteSpace(value)) dependencies.Add(value);
                    continue;
                }

                // 別のキーの行に入ったら依存関係の連続行は終わり。
                inDependencies = false;
            }
        }

        return dependencies;
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
