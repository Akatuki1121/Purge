using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Purge;

/// <summary>
/// Windows Shell(IShellLink)による .lnk の読み書き。WSH(WScript.Shell)に依存しないよう
/// COMを直接呼ぶ。STAスレッドから呼ぶこと。
/// </summary>
internal sealed class ShellLinkFileStore : IShortcutFileStore
{
    private const int MaxPath = 260;
    private const int StgmRead = 0;
    private const uint GetPathFlagsDefault = 0;
    private const int IconIndexFirst = 0;

    public bool Exists(string shortcutPath) => File.Exists(shortcutPath);

    public string? ReadTarget(string shortcutPath)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(shortcutPath, StgmRead);

            var buffer = new StringBuilder(MaxPath);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, GetPathFlagsDefault);
            return buffer.Length == 0 ? null : buffer.ToString();
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            // 読めないショートカットは「壊れている」として呼び出し側で作り直させる。
            return null;
        }
        finally
        {
            Release(link);
        }
    }

    public void Write(string shortcutPath, string targetPath, string workingDirectory, string description)
    {
        var directory = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLinkCoClass();
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            link.SetIconLocation(targetPath, IconIndexFirst);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Release(link);
        }
    }

    private static void Release(IShellLinkW? link)
    {
        if (link != null && Marshal.IsComObject(link))
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    // メソッドの宣言順がCOMの仮想関数テーブルの並びそのものなので、順序を変えてはいけない。
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxChars, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxChars);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxChars);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxChars);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxChars, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
