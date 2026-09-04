using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using UninstallTool;

namespace UninstallTool.UI
{
    /// <summary>
    /// InstalledAppにアイコン(ImageSource)・補完済み発行元を付加した、AppListView表示専用のラッパー。
    ///
    /// アイコン抽出・FileVersionInfo読み取りはいずれも1件あたり数十msかかることがあり、
    /// インストール済みアプリが100件超だと合計で数秒規模の遅延要因になる。
    /// そのため、コンストラクタでは即座には解決せず、呼び出し側がバックグラウンドスレッドから
    /// ResolveIcon() / ResolvePublisher() を呼んだタイミングで解決する(起動時の体感待ち時間をなくすため)。
    ///
    /// 重要: INotifyPropertyChangedのPropertyChangedはWPFが自動でUIスレッドへマーシャリングしない
    /// (ObservableCollectionのCollectionChangedとは違う)。バックグラウンドスレッドから直接発火すると
    /// バインディング更新が失敗することがあるため、プロパティ設定と通知だけDispatcherでUIスレッドに戻す。
    /// </summary>
    public sealed class AppListItem : INotifyPropertyChanged
    {
        public InstalledApp App { get; }
        public ImageSource? Icon { get; private set; }

        private string? _publisher;

        public string DisplayName => App.DisplayName;
        public string? DisplayVersion => App.DisplayVersion;

        /// <summary>
        /// レジストリのPublisher値。空の場合、ResolvePublisher()呼び出し後は
        /// exe/dllのCompanyNameで補完された値に置き換わる(それでも取得できなければ空のまま)。
        /// </summary>
        public string? Publisher => _publisher;

        public string? InstallLocation => App.InstallLocation;

        public AppListItem(InstalledApp app)
        {
            App = app;
            _publisher = app.Publisher;
        }

        /// <summary>
        /// アイコンを解決する。重いネイティブ呼び出し(AppIconResolver.Resolve)自体は
        /// 呼び出し元のバックグラウンドスレッドでそのまま実行されるが、
        /// プロパティへの反映とUI通知だけは必ずUIスレッドのDispatcher経由で行う。
        /// </summary>
        public void ResolveIcon()
        {
            var resolved = AppIconResolver.Resolve(App);
            SetOnUiThread(() =>
            {
                Icon = resolved;
                RaisePropertyChanged(nameof(Icon));
            });
        }

        /// <summary>
        /// 発行元がレジストリに未登録の場合、exe/dllのCompanyNameで補完する。
        /// </summary>
        public void ResolvePublisher()
        {
            if (!string.IsNullOrWhiteSpace(_publisher))
            {
                return;
            }

            var resolved = AppPublisherResolver.Resolve(App);
            if (resolved == null)
            {
                return;
            }

            SetOnUiThread(() =>
            {
                _publisher = resolved;
                RaisePropertyChanged(nameof(Publisher));
            });
        }

        private static void SetOnUiThread(System.Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.BeginInvoke(action);
            }
        }

        private void RaisePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
