using Microsoft.Win32;
using Purge;

namespace Purge.Tests;

/// <summary>
/// ResidueRemoverの「実際に削除できるか」を検証する回帰テスト。
/// 過去に「動くと思ってリリースしたが機能していなかった」不具合が起きた領域のため、
/// ドライランではなく実削除の結果(ファイル/レジストリが本当に消えたか)を確認する。
/// レジストリはHKCU配下のテスト専用キー(GUID付き)のみを作成・削除し、実環境の設定には触れない。
/// </summary>
public class ResidueRemoverTests
{
    private static ResidueRemover CreateRemover() => new(new OperationLog());

    [Fact]
    public void ドライランでは何も削除されない()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PurgeRemoverTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "keep.txt"), "x");

        try
        {
            var item = new ResidueItem { Category = ResidueCategory.MftFile, Location = directory };

            var result = CreateRemover().Remove(item, dryRun: true);

            Assert.Equal(RemovalResult.DryRun, result);
            Assert.True(File.Exists(Path.Combine(directory, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void フォルダを中身ごと実際に削除できる()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PurgeRemoverTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        File.WriteAllText(Path.Combine(directory, "sub", "a.txt"), "x");

        try
        {
            var item = new ResidueItem { Category = ResidueCategory.MftFile, Location = directory };

            var result = CreateRemover().Remove(item, dryRun: false);

            Assert.Equal(RemovalResult.Success, result);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ファイルを実際に削除できる()
    {
        var file = Path.Combine(Path.GetTempPath(), $"PurgeRemoverTests_{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "x");

        try
        {
            var item = new ResidueItem { Category = ResidueCategory.MftFile, Location = file };

            var result = CreateRemover().Remove(item, dryRun: false);

            Assert.Equal(RemovalResult.Success, result);
            Assert.False(File.Exists(file));
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void 既に存在しない対象の削除は成功扱いになる()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"PurgeRemoverTests_missing_{Guid.NewGuid():N}");
        var item = new ResidueItem { Category = ResidueCategory.MftFile, Location = missing };

        var result = CreateRemover().Remove(item, dryRun: false);

        Assert.Equal(RemovalResult.Success, result);
    }

    [Fact]
    public void レジストリキーをサブキーごと実際に削除できる()
    {
        var keyName = $"PurgeTest_{Guid.NewGuid():N}";
        using (var created = Registry.CurrentUser.CreateSubKey($@"Software\{keyName}\Child"))
        {
            created.SetValue("v", "1");
        }

        try
        {
            var item = new ResidueItem
            {
                Category = ResidueCategory.Registry,
                Location = $@"{Registry.CurrentUser.Name}\Software\{keyName}",
            };

            var result = CreateRemover().Remove(item, dryRun: false);

            Assert.Equal(RemovalResult.Success, result);
            using var after = Registry.CurrentUser.OpenSubKey($@"Software\{keyName}");
            Assert.Null(after);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\{keyName}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void 解釈できないレジストリルートは失敗になり何も消さない()
    {
        var item = new ResidueItem
        {
            Category = ResidueCategory.Registry,
            Location = @"HKEY_UNKNOWN_ROOT\Software\Foo",
        };

        var result = CreateRemover().Remove(item, dryRun: false);

        Assert.Equal(RemovalResult.Failed, result);
    }

    [Fact]
    public void スタートアップのRun値だけを削除し同じキーの他の値は残る()
    {
        var keyPath = $@"Software\PurgeTestRun_{Guid.NewGuid():N}";
        using (var created = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            created.SetValue("PurgeTargetEntry", @"C:\x\target.exe");
            created.SetValue("OtherAppEntry", @"C:\x\other.exe");
        }

        try
        {
            var item = new ResidueItem
            {
                Category = ResidueCategory.Startup,
                Location = $@"{Registry.CurrentUser.Name}\{keyPath}\PurgeTargetEntry",
            };

            var result = CreateRemover().Remove(item, dryRun: false);

            Assert.Equal(RemovalResult.Success, result);
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            Assert.NotNull(key);
            Assert.Null(key!.GetValue("PurgeTargetEntry"));
            Assert.Equal(@"C:\x\other.exe", key.GetValue("OtherAppEntry"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void スタートアップフォルダのファイルを実際に削除できる()
    {
        var file = Path.Combine(Path.GetTempPath(), $"PurgeStartupTest_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(file, "x");

        try
        {
            var item = new ResidueItem { Category = ResidueCategory.Startup, Location = file };

            var result = CreateRemover().Remove(item, dryRun: false);

            Assert.Equal(RemovalResult.Success, result);
            Assert.False(File.Exists(file));
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void PathTarget未設定のPATH削除は失敗になる()
    {
        var item = new ResidueItem
        {
            Category = ResidueCategory.EnvironmentPath,
            Location = @"C:\Some\Path",
            PathTarget = null,
        };

        var result = CreateRemover().Remove(item, dryRun: false);

        Assert.Equal(RemovalResult.Failed, result);
    }
}
