using System.Text.Json;

namespace UninstallTool;

/// <summary>
/// ユーザーが「今後表示しない」と指定した孤児候補のパスを保存する。
/// インストール先ではなくLocalAppDataに保存し、管理者権限なしでも更新できるようにする。
/// </summary>
public sealed class OrphanExclusionStore
{
    private readonly string _filePath;
    private readonly HashSet<string> _excludedPaths = new(StringComparer.OrdinalIgnoreCase);

    public OrphanExclusionStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UninstallTool",
            "orphan-exclusions.json");
        Load();
    }

    public bool Contains(string path) => _excludedPaths.Contains(Normalize(path));

    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _excludedPaths.Add(Normalize(path));
            }
        }

        Save();
    }

    public IReadOnlyCollection<string> GetAll() => _excludedPaths.ToArray();

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_filePath));
            if (paths == null) return;

            foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                _excludedPaths.Add(Normalize(path));
            }
        }
        catch
        {
            // 壊れた設定は無視し、スキャン自体は継続する。
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(_excludedPaths.Order(StringComparer.OrdinalIgnoreCase), new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(_filePath, json);
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
}