using System.Windows;
using UninstallTool;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace UninstallTool.UI
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
                StatusInfoBar.Title = "Pro版が有効です";
                StatusInfoBar.Message = "すべてのPro機能をご利用いただけます。";
                LicenseDetailText.Text =
                    $"登録メールアドレス: {license.Email}\n発行日: {license.IssuedAtUtc.ToLocalTime():yyyy/MM/dd}\nプラン: {license.Edition}(無期限)";
                DeactivateButton.Visibility = Visibility.Visible;
            }
            else
            {
                StatusInfoBar.Severity = InfoBarSeverity.Informational;
                StatusInfoBar.Title = "無料版で利用中";
                StatusInfoBar.Message = "Pro版のライセンスキーをお持ちの場合は、下の欄に貼り付けて「認証する」を押してください。";
                LicenseDetailText.Text = "";
                DeactivateButton.Visibility = Visibility.Collapsed;
            }
        }

        private void ActivateButton_Click(object sender, RoutedEventArgs e)
        {
            var key = LicenseKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                MessageBox.Show("ライセンスキーを入力してください。", "未入力",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = LicenseState.ActivateLicense(key);

            switch (result)
            {
                case LicenseValidationResult.Valid:
                    MessageBox.Show("Pro版が有効になりました。ありがとうございます。", "認証成功",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    LicenseKeyTextBox.Text = "";
                    RefreshStatusDisplay();
                    break;

                case LicenseValidationResult.InvalidFormat:
                    MessageBox.Show("ライセンスキーの形式が正しくありません。コピー時に一部が欠けていないか確認してください。",
                        "認証失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;

                case LicenseValidationResult.SignatureMismatch:
                    MessageBox.Show("このライセンスキーは有効なものとして確認できませんでした。購入時のメールを確認するか、サポートにお問い合わせください。",
                        "認証失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;

                case LicenseValidationResult.Expired:
                    MessageBox.Show("このライセンスキーは有効期限が切れています。",
                        "認証失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        }

        private void DeactivateButton_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "ライセンスを解除し、無料版に戻します。よろしいですか？",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);

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
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = LicenseState.PurchaseUrl,
                    UseShellExecute = true,
                });
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"購入ページを開けませんでした。\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
