using System.Text.Json;
using UninstallTool;

namespace UninstallTool.Tests;

public class RemovalRestoreServiceTests
{
    [Fact]
    public void ファイルスナップショットを復元できる()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"UninstallToolRestoreTests_{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(directory, "source");
        var targetDirectory = Path.Combine(directory, "target");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "settings.ini"), "original");

        try
        {
            var writer = new RemovalBackupWriter(new OperationLog());
            var item = new ResidueItem
            {
                Category = ResidueCategory.MftFile,
                Location = sourceDirectory,
                Detail = "テスト対象",
            };
            var manifestPath = writer.Write(new[] { item }, directory);
            Directory.Delete(sourceDirectory, recursive: true);

            var manifest = JsonSerializer.Deserialize<RemovalBackupManifest>(File.ReadAllText(manifestPath));
            Assert.NotNull(manifest);
            manifest!.Entries[0] = new RemovalBackupEntry
            {
                Category = manifest.Entries[0].Category,
                Location = targetDirectory,
                Detail = manifest.Entries[0].Detail,
                Confidence = manifest.Entries[0].Confidence,
                ExistsBeforeRemoval = manifest.Entries[0].ExistsBeforeRemoval,
                SnapshotPath = manifest.Entries[0].SnapshotPath,
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

            var restored = new RemovalRestoreService(new OperationLog()).Restore(manifestPath);

            Assert.Equal(1, restored);
            Assert.Equal("original", File.ReadAllText(Path.Combine(targetDirectory, "settings.ini")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
