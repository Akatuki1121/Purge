namespace Purge
{
    /// <summary>
    /// レジストリ値名・外部プロセス名・環境変数名など、複数クラスにまたがって
    /// 使われる文字列/数値定数をここに集約する。個々のクラスに散らばせない。
    /// </summary>
    internal static class WellKnownConstants
    {
        /// <summary>Windowsのプロセス終了コードにおける「正常終了」を表す値。</summary>
        public const int ProcessExitCodeSuccess = 0;

        public static class RegistryValueNames
        {
            public const string DisplayName = "DisplayName";
            public const string DisplayVersion = "DisplayVersion";
            public const string Publisher = "Publisher";
            public const string InstallLocation = "InstallLocation";
            public const string UninstallString = "UninstallString";
            public const string DisplayIcon = "DisplayIcon";
            public const string SystemComponent = "SystemComponent";

            /// <summary>SystemComponent値がこの値の場合、システムコンポーネントとして一覧から除外する。</summary>
            public const int SystemComponentTrueValue = 1;

            /// <summary>タスクスケジューラのTaskCacheキー配下で、タスクのフルパスを保持する値名。</summary>
            public const string ScheduledTaskPath = "Path";
        }

        public static class RegistryKeyPaths
        {
            public const string UninstallSubKey =
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            public const string UninstallSubKeyWow6432 =
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
            public const string RunSubKey =
                @"Software\Microsoft\Windows\CurrentVersion\Run";
            public const string ScheduledTaskCacheSubKey =
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks";
        }

        public static class ExternalTools
        {
            public const string ServiceControl = "sc.exe";
            public const string ServiceControlDeleteArgsFormat = "delete \"{0}\"";

            public const string TaskScheduler = "schtasks.exe";
            public const string TaskSchedulerDeleteArgsFormat = "/Delete /TN \"{0}\" /F";
        }

        public const string PathEnvironmentVariableName = "PATH";

        /// <summary>MFT検索でドライブレターを省略した場合の既定ドライブ。</summary>
        public const string DefaultMftSearchDrive = "C";
    }
}
