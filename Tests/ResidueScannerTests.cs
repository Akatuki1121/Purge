using Microsoft.Win32;
using Purge;

namespace Purge.Tests;

/// <summary>
/// ResidueScannerの「実際に検出できるか」を検証する回帰テスト。
/// テスト専用の一意な名前(GUID付き)で擬似的な残骸を作り、それが検出されること、
/// 無関係な項目は検出されないことを確認する。
/// 検出だけを行い、実環境のレジストリ・ファイルは書き換えない(テスト用キーは自分で作って自分で消す)。
/// </summary>
public class ResidueScannerTests
{
    private static ResidueScanner CreateScanner() => new(new OperationLog());

    [Fact]
    public void HKCUのSoftware直下にあるアプリ名一致キーを検出する()
    {
        var appName = $"PurgeScanTest{Guid.NewGuid():N}";
        Registry.CurrentUser.CreateSubKey($@"Software\{appName}").Dispose();

        try
        {
            var results = CreateScanner().ScanAll(appName);

            Assert.Contains(results, r =>
                r.Category == ResidueCategory.Registry &&
                r.Location.EndsWith($@"\Software\{appName}", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\{appName}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void 無関係な名前ではレジストリキーを検出しない()
    {
        var existingApp = $"PurgeScanTest{Guid.NewGuid():N}";
        var otherApp = $"UnrelatedZzz{Guid.NewGuid():N}";
        Registry.CurrentUser.CreateSubKey($@"Software\{existingApp}").Dispose();

        try
        {
            var results = CreateScanner().ScanAll(otherApp);

            Assert.DoesNotContain(results, r => r.Location.Contains(existingApp, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\{existingApp}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void スタートアップRunキーの値名一致を検出する()
    {
        var appName = $"PurgeScanRun{Guid.NewGuid():N}";
        const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using (var run = Registry.CurrentUser.CreateSubKey(runPath))
        {
            run.SetValue(appName, @"C:\x\app.exe");
        }

        try
        {
            var results = CreateScanner().ScanAll(appName);

            Assert.Contains(results, r =>
                r.Category == ResidueCategory.Startup &&
                r.Location.EndsWith($@"\{appName}", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            using var run = Registry.CurrentUser.OpenSubKey(runPath, writable: true);
            run?.DeleteValue(appName, throwOnMissingValue: false);
        }
    }

    [Fact]
    public void ユーザーPATHに含まれるアプリ名一致エントリを検出しPathTargetを持つ()
    {
        var appName = $"PurgeScanPath{Guid.NewGuid():N}";
        var entry = $@"C:\Tools\{appName}\bin";
        var original = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);

        try
        {
            Environment.SetEnvironmentVariable("PATH",
                string.IsNullOrEmpty(original) ? entry : original + ";" + entry, EnvironmentVariableTarget.User);

            var results = CreateScanner().ScanAll(appName);

            var hit = Assert.Single(results, r => r.Category == ResidueCategory.EnvironmentPath);
            Assert.Equal(entry, hit.Location);
            Assert.Equal(EnvironmentVariableTarget.User, hit.PathTarget);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original, EnvironmentVariableTarget.User);
        }
    }

    [Fact]
    public void スキャンは検出のみで実環境を書き換えない()
    {
        var appName = $"PurgeScanTest{Guid.NewGuid():N}";
        Registry.CurrentUser.CreateSubKey($@"Software\{appName}").Dispose();

        try
        {
            CreateScanner().ScanAll(appName);

            using var stillThere = Registry.CurrentUser.OpenSubKey($@"Software\{appName}");
            Assert.NotNull(stillThere);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\{appName}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void キャンセル要求でスキャンが中断される()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            CreateScanner().ScanAll($"PurgeScanTest{Guid.NewGuid():N}", cancellationToken: cts.Token));
    }
}
