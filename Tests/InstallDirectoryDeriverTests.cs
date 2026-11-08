using Purge;

namespace Purge.Tests;

public class InstallDirectoryDeriverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PurgeDeriverTests_" + Guid.NewGuid().ToString("N"));
    private readonly string _appDirectory;

    public InstallDirectoryDeriverTests()
    {
        _appDirectory = Directory.CreateDirectory(Path.Combine(_root, "MyApp")).FullName;
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

    private string ExePath => Path.Combine(_appDirectory, "app.exe");

    private static InstalledApp App(string? uninstallString = null, string? displayIcon = null)
        => new()
        {
            DisplayName = "Test App",
            UninstallString = uninstallString,
            DisplayIconPath = displayIcon,
        };

    [Fact]
    public void QuotedUninstallStringWithArguments_ReturnsDirectory()
    {
        var app = App(uninstallString: $"\"{ExePath}\" /uninstall");
        Assert.Equal(_appDirectory, InstallDirectoryDeriver.Derive(app));
    }

    [Fact]
    public void UnquotedUninstallString_ReturnsDirectory()
    {
        var app = App(uninstallString: Path.Combine(_appDirectory, "unins000.exe"));
        Assert.Equal(_appDirectory, InstallDirectoryDeriver.Derive(app));
    }

    [Fact]
    public void MsiExecUninstallString_ReturnsNull()
    {
        var app = App(uninstallString: "MsiExec.exe /X{11111111-2222-3333-4444-555555555555}");
        Assert.Null(InstallDirectoryDeriver.Derive(app));
    }

    [Fact]
    public void DisplayIconWithIndex_ReturnsDirectory()
    {
        var app = App(displayIcon: ExePath + ",0");
        Assert.Equal(_appDirectory, InstallDirectoryDeriver.Derive(app));
    }

    [Fact]
    public void EnvironmentVariableInPath_IsExpanded()
    {
        const string variableName = "PURGE_DERIVER_TEST_ROOT";
        Environment.SetEnvironmentVariable(variableName, _root);
        try
        {
            var app = App(uninstallString: $@"%{variableName}%\MyApp\app.exe");
            Assert.Equal(_appDirectory, InstallDirectoryDeriver.Derive(app));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void MissingDirectory_ReturnsNull()
    {
        var app = App(uninstallString: Path.Combine(_root, "NoSuchFolder", "app.exe"));
        Assert.Null(InstallDirectoryDeriver.Derive(app));
    }

    [Fact]
    public void NoInformation_ReturnsNull()
    {
        Assert.Null(InstallDirectoryDeriver.Derive(App()));
    }
}
