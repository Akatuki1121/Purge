using System.Text.Json;
using Purge;

namespace Purge.Tests;

public class RemovalRestoreServiceTests
{
    [Fact]
    public void ファイルスナップショットを復元できる()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PurgeRestoreTests_{Guid.NewGuid():N}");
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

public class RemovalRestoreServiceParsingTests
{
    private const string SampleScQcOutput = @"[SC] QueryServiceConfig SUCCESS

SERVICE_NAME: TestService
        TYPE               : 10  WIN32_OWN_PROCESS
        START_TYPE         : 2   AUTO_START
        ERROR_CONTROL      : 1   NORMAL
        BINARY_PATH_NAME   : C:\Program Files\Test\test.exe
        LOAD_ORDER_GROUP   :
        TAG                : 0
        DISPLAY_NAME       : Test Service
        DEPENDENCIES       : RPCSS
                           : Tcpip
        SERVICE_START_NAME : LocalSystem";

    [Fact]
    public void BINARY_PATH_NAMEを抽出できる()
    {
        var value = RemovalRestoreService.ExtractValue(SampleScQcOutput, "BINARY_PATH_NAME");
        Assert.Equal(@"C:\Program Files\Test\test.exe", value);
    }

    [Fact]
    public void DISPLAY_NAMEを抽出できる()
    {
        var value = RemovalRestoreService.ExtractValue(SampleScQcOutput, "DISPLAY_NAME");
        Assert.Equal("Test Service", value);
    }

    [Fact]
    public void 存在しないキーはnullを返す()
    {
        var value = RemovalRestoreService.ExtractValue(SampleScQcOutput, "NOT_A_KEY");
        Assert.Null(value);
    }

    [Fact]
    public void 複数行にまたがるDEPENDENCIESを抽出できる()
    {
        var dependencies = RemovalRestoreService.ExtractDependencies(SampleScQcOutput);
        Assert.Equal(new List<string> { "RPCSS", "Tcpip" }, dependencies);
    }

    [Fact]
    public void DEPENDENCIESが無い場合は空リストを返す()
    {
        const string noDeps = @"SERVICE_NAME: X
        BINARY_PATH_NAME   : C:\x.exe
        DISPLAY_NAME       : X";
        var dependencies = RemovalRestoreService.ExtractDependencies(noDeps);
        Assert.Empty(dependencies);
    }
}
