using Purge;

namespace Purge.Tests;

public class SelfUninstallerTests
{
    [Fact]
    public void 自己削除用batが対象プロセス終了後にmsiexecでアンインストールする()
    {
        var content = SelfUninstaller.BuildBatchContent(12345, "{4EB024C8-F520-48AE-A4EE-07BE753DA840}");

        Assert.Contains("tasklist /FI \"PID eq 12345\"", content);
        Assert.Contains("msiexec /x {4EB024C8-F520-48AE-A4EE-07BE753DA840} /qn /norestart", content);
        Assert.Contains("del /f /q \"%~f0\"", content);
    }
}
