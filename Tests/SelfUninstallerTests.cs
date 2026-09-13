using Purge;

namespace Purge.Tests;

public class SelfUninstallerTests
{
    [Fact]
    public void 自己削除用batが対象プロセス終了後にexeとフォルダを削除する()
    {
        var content = SelfUninstaller.BuildBatchContent(12345, @"C:\Deploy\Purge\Purge.exe");

        Assert.Contains("tasklist /FI \"PID eq 12345\"", content);
        Assert.Contains("del /f /q \"C:\\Deploy\\Purge\\Purge.exe\"", content);
        Assert.Contains("rmdir \"C:\\Deploy\\Purge\"", content);
        Assert.Contains("del /f /q \"%~f0\"", content);
    }

    [Fact]
    public void 親フォルダのないexeパスは拒否する()
    {
        Assert.Throws<ArgumentException>(() => SelfUninstaller.BuildBatchContent(1, "tool.exe"));
    }
}