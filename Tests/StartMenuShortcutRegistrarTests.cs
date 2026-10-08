namespace Purge.Tests;

public class StartMenuShortcutRegistrarTests : IDisposable
{
    private const string ExePath = @"C:\Tools\Purge\Purge.exe";
    private const string OtherExePath = @"D:\Moved\Purge\Purge.exe";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "PurgeStartMenuTests_" + Guid.NewGuid().ToString("N"));
    private readonly string _programs;
    private readonly string _commonPrograms;

    public StartMenuShortcutRegistrarTests()
    {
        _programs = Path.Combine(_root, "UserPrograms");
        _commonPrograms = Path.Combine(_root, "CommonPrograms");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private StartMenuShortcutRegistrar CreateRegistrar(FakeShortcutStore store, bool msiInstalled = false, OperationLog? log = null)
        => new(log ?? new OperationLog(), store, _programs, _commonPrograms, () => msiInstalled);

    [Fact]
    public void ユーザー単位のショートカットはProgramsのPurgeフォルダーに作る()
    {
        var registrar = CreateRegistrar(new FakeShortcutStore());

        Assert.Equal(Path.Combine(_programs, "Purge", "Purge.lnk"), registrar.UserShortcutPath);
        Assert.Equal(Path.Combine(_commonPrograms, "Purge.lnk"), registrar.MsiShortcutPath);
    }

    [Fact]
    public void ショートカットが無ければ現在の実行ファイルを指して作成する()
    {
        var store = new FakeShortcutStore();
        var registrar = CreateRegistrar(store);

        var result = registrar.EnsureRegistered(ExePath);

        Assert.Equal(StartMenuRegistrationResult.Created, result);
        var written = Assert.Single(store.Writes);
        Assert.Equal(registrar.UserShortcutPath, written.ShortcutPath);
        Assert.Equal(ExePath, written.TargetPath);
        Assert.Equal(@"C:\Tools\Purge", written.WorkingDirectory);
    }

    [Fact]
    public void 現在の実行ファイルを指していれば何もしない_大文字小文字は区別しない()
    {
        var store = new FakeShortcutStore();
        var registrar = CreateRegistrar(store);
        store.Files[registrar.UserShortcutPath] = ExePath.ToUpperInvariant();

        var result = registrar.EnsureRegistered(ExePath);

        Assert.Equal(StartMenuRegistrationResult.AlreadyUpToDate, result);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void 展開フォルダーを移動したあとの起動では新しい場所を指すよう更新する()
    {
        var store = new FakeShortcutStore();
        var registrar = CreateRegistrar(store);
        store.Files[registrar.UserShortcutPath] = ExePath;

        var result = registrar.EnsureRegistered(OtherExePath);

        Assert.Equal(StartMenuRegistrationResult.Updated, result);
        Assert.Equal(OtherExePath, Assert.Single(store.Writes).TargetPath);
    }

    [Fact]
    public void ショートカットが壊れていて読めなければ作り直す()
    {
        var store = new FakeShortcutStore();
        var registrar = CreateRegistrar(store);
        store.Files[registrar.UserShortcutPath] = null;

        var result = registrar.EnsureRegistered(ExePath);

        Assert.Equal(StartMenuRegistrationResult.Updated, result);
        Assert.Single(store.Writes);
    }

    [Fact]
    public void リンク先が不正なパス文字列でも作り直す()
    {
        var store = new FakeShortcutStore();
        var registrar = CreateRegistrar(store);
        store.Files[registrar.UserShortcutPath] = "C:\\bad|path\0\\Purge.exe";

        var result = registrar.EnsureRegistered(ExePath);

        Assert.Equal(StartMenuRegistrationResult.Updated, result);
    }

    [Fact]
    public void MSI版がインストール済みなら何も作らない()
    {
        var store = new FakeShortcutStore();
        var registrar = CreateRegistrar(store, msiInstalled: true);

        var result = registrar.EnsureRegistered(ExePath);

        Assert.Equal(StartMenuRegistrationResult.SkippedMsiInstalled, result);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void 作成に失敗しても例外を投げず操作ログに警告を残す()
    {
        var store = new FakeShortcutStore { WriteException = new UnauthorizedAccessException("denied") };
        var log = new OperationLog();
        var registrar = CreateRegistrar(store, log: log);

        var result = registrar.EnsureRegistered(ExePath);

        Assert.Equal(StartMenuRegistrationResult.Failed, result);
        Assert.Contains(log.GetRecent(), s => s.Category == "StartMenu" && s.Level == OperationStepLevel.Warning);
    }

    [Fact]
    public void ピン留め案内はMSIの全ユーザー用ショートカットが現在の実行ファイルを指していればそれを使う()
    {
        var store = new FakeShortcutStore();
        var registrar = CreateRegistrar(store, msiInstalled: true);
        store.Files[registrar.MsiShortcutPath] = ExePath;

        var path = registrar.EnsureShortcutForPinning(ExePath);

        Assert.Equal(registrar.MsiShortcutPath, path);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void ピン留め案内はショートカットが無ければ作る_MSIでショートカットを作らない選択をしていても()
    {
        var store = new FakeShortcutStore();
        var registrar = CreateRegistrar(store, msiInstalled: true);

        var path = registrar.EnsureShortcutForPinning(ExePath);

        Assert.Equal(registrar.UserShortcutPath, path);
        Assert.Equal(ExePath, Assert.Single(store.Writes).TargetPath);
    }

    [Fact]
    public void ピン留め案内は失敗したらnullを返す()
    {
        var store = new FakeShortcutStore { WriteException = new IOException("disk") };
        var registrar = CreateRegistrar(store);

        Assert.Null(registrar.EnsureShortcutForPinning(ExePath));
    }

    [Fact]
    public void 実際のShellでショートカットを作成し読み戻せる()
    {
        var exePath = typeof(StartMenuShortcutRegistrar).Assembly.Location;
        var shortcutPath = Path.Combine(_root, "Real", "Purge", "Purge.lnk");
        var store = new ShellLinkFileStore();

        string? target = null;
        var exists = false;
        RunOnStaThread(() =>
        {
            store.Write(shortcutPath, exePath, Path.GetDirectoryName(exePath)!, "Purge");
            exists = store.Exists(shortcutPath);
            target = store.ReadTarget(shortcutPath);
        });

        Assert.True(exists);
        Assert.Equal(exePath, target, ignoreCase: true);
    }

    [Fact]
    public void 実際のShellで壊れたファイルはnullとして扱う()
    {
        var shortcutPath = Path.Combine(_root, "Broken", "Purge.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        File.WriteAllText(shortcutPath, "this is not a shortcut");
        var store = new ShellLinkFileStore();

        string? target = "unset";
        RunOnStaThread(() => target = store.ReadTarget(shortcutPath));

        Assert.Null(target);
    }

    [Fact]
    public void 実際のShellで作ったショートカットが現在の場所なら再登録しない()
    {
        var exePath = typeof(StartMenuShortcutRegistrar).Assembly.Location;
        var registrar = new StartMenuShortcutRegistrar(new OperationLog(), new ShellLinkFileStore(), _programs, _commonPrograms, () => false);

        StartMenuRegistrationResult first = default;
        StartMenuRegistrationResult second = default;
        RunOnStaThread(() =>
        {
            first = registrar.EnsureRegistered(exePath);
            second = registrar.EnsureRegistered(exePath);
        });

        Assert.Equal(StartMenuRegistrationResult.Created, first);
        Assert.Equal(StartMenuRegistrationResult.AlreadyUpToDate, second);
        Assert.True(File.Exists(registrar.UserShortcutPath));
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            throw new Xunit.Sdk.XunitException("STAスレッド内で例外: " + error);
        }
    }

    private sealed class FakeShortcutStore : IShortcutFileStore
    {
        /// <summary>パス → リンク先。値が null のものは「存在するが読めない(壊れている)」。</summary>
        public Dictionary<string, string?> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string ShortcutPath, string TargetPath, string WorkingDirectory, string Description)> Writes { get; } = new();

        public Exception? WriteException { get; init; }

        public bool Exists(string shortcutPath) => Files.ContainsKey(shortcutPath);

        public string? ReadTarget(string shortcutPath) => Files.TryGetValue(shortcutPath, out var target) ? target : null;

        public void Write(string shortcutPath, string targetPath, string workingDirectory, string description)
        {
            if (WriteException != null)
            {
                throw WriteException;
            }

            Writes.Add((shortcutPath, targetPath, workingDirectory, description));
            Files[shortcutPath] = targetPath;
        }
    }
}
