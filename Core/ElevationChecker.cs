using System.Security.Principal;

namespace Purge
{
    /// <summary>
    /// 現在のプロセスが管理者権限で実行されているか判定するヘルパー。
    /// MFT検索(USN Journal)やレジストリの一部書き込みは管理者権限が無いと
    /// 例外を投げずサイレントに失敗することがあるため、起動時にUI側で明示的に警告するために使う。
    /// </summary>
    public static class ElevationChecker
    {
        public static bool IsRunningAsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
