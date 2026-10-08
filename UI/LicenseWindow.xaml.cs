using System.Windows;
using Purge;
using Purge.Localization;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace Purge.UI
{
    /// <summary>
    /// ライセンスキーの入力・認証・解除を行う画面。
    /// 検証はLicenseState.ActivateLicense経由でLicenseKeyVerifier(オフライン署名検証)に委譲する。
    /// </summary>
    public partial class LicenseWindow : FluentWindow
    {
        public LicenseWindow()
        {
            InitializeComponent();
            RefreshStatusDisplay();
        }

        private void RefreshStatusDisplay()
        {
            if (LicenseState.IsProUnlocked && LicenseState.CurrentLicense is { } license)
            {
                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Title = Loc.T("Lic_ProTitle");
                StatusInfoBar.Message = Loc.T("Lic_ProMessage");
                LicenseDetailText.Text =
                    $"登録メールアドレス: {license.Email}\n発行日: {license.IssuedAtUtc.ToLocalTime():yyyy/MM/dd}\nプラン: {license.Edition}(無期限)";
                DeactivateButton.Visibility = Visibility.Visible;
                SetKeyEntryVisible(false);
            }
            else
            {
                StatusInfoBar.Severity = InfoBarSeverity.Informational;
                StatusInfoBar.Title = Loc.T("Lic_FreeTitle");
                StatusInfoBar.Message = Loc.T("Lic_FreeMessage");
                LicenseDetailText.Text = "";
                DeactivateButton.Visibility = Visibility.Collapsed;
                SetKeyEntryVisible(true);
            }
        }

        /// <summary>
        /// キー入力欄・有効化・購入ボタンは無料版のときだけ表示する(#95)。
        /// Pro有効時は状態表示と「ライセンスを解除」だけにする。
        /// </summary>
        private void SetKeyEntryVisible(bool visible)
        {
            var v = visible ? Visibility.Visible : Visibility.Collapsed;
            LicenseKeyLabel.Visibility = v;
            LicenseKeyTextBox.Visibility = v;
            ActivateButton.Visibility = v;
            PurchaseButton.Visibility = v;
        }

        private void ActivateButton_Click(object sender, RoutedEventArgs e)
        {
            var key = LicenseKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                MessageBox.Show(Loc.T("Msg_EnterKey"), Loc.T("Common_MissingInput"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = LicenseState.ActivateLicense(key);

            switch (result)
            {
                case LicenseValidationResult.Valid:
                    MessageBox.Show(Loc.T("Msg_Activated"), Loc.T("Title_Activated"),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    LicenseKeyTextBox.Text = "";
                    RefreshStatusDisplay();
                    break;

                case LicenseValidationResult.InvalidFormat:
                    MessageBox.Show(Loc.T("Msg_KeyBadFormat"),
                        Loc.T("Title_ActivationFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;

                case LicenseValidationResult.SignatureMismatch:
                    MessageBox.Show(Loc.T("Msg_KeyInvalid"),
                        Loc.T("Title_ActivationFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;

                case LicenseValidationResult.Expired:
                    MessageBox.Show(Loc.T("Msg_KeyExpired"),
                        Loc.T("Title_ActivationFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        }

        private void DeactivateButton_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                Loc.T("Msg_ConfirmDeactivate"),
                Loc.T("Common_Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            LicenseState.DeactivateLicense();
            RefreshStatusDisplay();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 購入ページ(Stripe Payment Link)を既定ブラウザで開く。
        /// </summary>
        private void PurchaseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LicenseState.OpenPurchasePage();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"購入ページを開けませんでした。\n{ex.Message}",
                    Loc.T("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
