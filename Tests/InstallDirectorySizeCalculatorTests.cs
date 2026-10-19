using Purge;

namespace Purge.Tests;

public class InstallDirectorySizeCalculatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PurgeSizeTests_" + Guid.NewGuid().ToString("N"));

    public InstallDirectorySizeCalculatorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 一時フォルダーの後始末に失敗してもテスト結果には影響させない。
        }
    }

    [Fact]
    public void NestedFiles_AreIncludedInTotal()
    {
        File.WriteAllBytes(Path.Combine(_root, "one.bin"), new byte[12]);
        var nested = Directory.CreateDirectory(Path.Combine(_root, "nested")).FullName;
        File.WriteAllBytes(Path.Combine(nested, "two.bin"), new byte[30]);

        Assert.Equal(42L, InstallDirectorySizeCalculator.CalculateBytes(_root));
    }

    [Fact]
    public void MissingDirectory_ReturnsUnknown()
    {
        Assert.Null(InstallDirectorySizeCalculator.CalculateBytes(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void EmptyPath_ReturnsUnknown()
    {
        Assert.Null(InstallDirectorySizeCalculator.CalculateBytes(" "));
    }
}
