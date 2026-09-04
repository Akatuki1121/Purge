using UninstallTool;

namespace UninstallTool.Tests;

public class OrphanExclusionStoreTests
{
    [Fact]
    public void 除外パスを保存して別インスタンスで読み込める()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"uninstalltool_exclusions_{Guid.NewGuid():N}.json");
        try
        {
            var store = new OrphanExclusionStore(filePath);
            store.Add([@"C:\Program Files\Example"]);

            var reloaded = new OrphanExclusionStore(filePath);
            Assert.True(reloaded.Contains(@"c:\program files\example\"));
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    [Fact]
    public void 同じパスの大文字小文字違いを重複登録しない()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"uninstalltool_exclusions_{Guid.NewGuid():N}.json");
        try
        {
            var store = new OrphanExclusionStore(filePath);
            store.Add([@"C:\Example", @"c:\example"]);

            Assert.Single(store.GetAll());
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }
}