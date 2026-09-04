using System.Text.Json;
using UninstallTool;

namespace UninstallTool.Tests;

public class RemovalBackupWriterTests
{
    [Fact]
    public void 削除前マニフェストに対象と検出根拠が保存される()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"UninstallToolTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var writer = new RemovalBackupWriter(new OperationLog());
            var item = new ResidueItem
            {
                Category = ResidueCategory.MftFile,
                Location = Path.Combine(directory, "leftover-folder"),
                Detail = "MFT検索でファイル名/フォルダ名が一致",
            };

            var manifestPath = writer.Write(new[] { item }, directory);
            var manifest = JsonSerializer.Deserialize<RemovalBackupManifest>(File.ReadAllText(manifestPath));

            Assert.NotNull(manifest);
            var entry = Assert.Single(manifest!.Entries);
            Assert.Equal("MftFile", entry.Category);
            Assert.Equal(item.Location, entry.Location);
            Assert.Equal(item.Detail, entry.Detail);
            Assert.False(entry.ExistsBeforeRemoval);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void サービスやPATHは未確認として保存される()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"UninstallToolTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var writer = new RemovalBackupWriter(new OperationLog());
            var items = new[]
            {
                new ResidueItem { Category = ResidueCategory.Service, Location = "ExampleService" },
                new ResidueItem { Category = ResidueCategory.EnvironmentPath, Location = @"C:\Example" },
            };

            var manifestPath = writer.Write(items, directory);
            var manifest = JsonSerializer.Deserialize<RemovalBackupManifest>(File.ReadAllText(manifestPath));

            Assert.NotNull(manifest);
            Assert.All(manifest!.Entries, entry => Assert.Null(entry.ExistsBeforeRemoval));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
