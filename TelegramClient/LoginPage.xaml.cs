using System;
using TelegramClient.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace TelegramClient
{
    public sealed partial class LoginPage : Page
    {
        private TdService _td => TdService.Instance;

        public LoginPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // API_ID и API_HASH — получи на https://my.telegram.org
            _td.Initialize(YOUR_API_ID, "YOUR_API_HASH");

            _td.AuthorizationStateChanged += OnAuthStateChanged;
            _td.Error += OnError;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _td.AuthorizationStateChanged -= OnAuthStateChanged;
            _td.Error -= OnError;
        }

        private void OnAuthStateChanged(string state)
        {
            Loader.IsActive = false;
            ErrorText.Visibility = Visibility.Collapsed;

            switch (_td.State)
            {
                case TdService.AuthState.WaitPhone:
                    ShowPanel(PhonePanel);
                    SubtitleText.Text = "Введите номер телефона";
                    break;

                case TdService.AuthState.WaitCode:
                    ShowPanel(CodePanel);
                    SubtitleText.Text = "Код отправлен на ваш телефон";
                    break;

                case TdService.AuthState.WaitPassword:
                    ShowPanel(PasswordPanel);
                    SubtitleText.Text = "Введите пароль двухфакторной аутентификации";
                    break;

                case TdService.AuthState.Ready:
                    Frame.Navigate(typeof(ChatsPage));
                    break;
            }
        }

        private void OnError(string message)
        {
            Loader.IsActive = false;
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void OnSendPhone(object sender, RoutedEventArgs e)
        {
            var phone = PhoneBox.Text.Trim();
            if (string.IsNullOrEmpty(phone)) return;
            SetLoading(true);
            _td.SetPhone(phone);
        }

        private void OnSendCode(object sender, RoutedEventArgs e)
        {
            var code = CodeBox.Text.Trim();
            if (string.IsNullOrEmpty(code)) return;
            SetLoading(true);
            _td.SetCode(code);
        }

        private void OnSendPassword(object sender, RoutedEventArgs e)
        {
            var pwd = PasswordBox.Password;
            if (string.IsNullOrEmpty(pwd)) return;
            SetLoading(true);
            _td.SetPassword(pwd);
        }

        private void ShowPanel(StackPanel panel)
        {
            PhonePanel.Visibility    = Visibility.Collapsed;
            CodePanel.Visibility     = Visibility.Collapsed;
            PasswordPanel.Visibility = Visibility.Collapsed;
            panel.Visibility         = Visibility.Visible;
        }

        private void SetLoading(bool loading)
        {
            Loader.IsActive          = loading;
            ErrorText.Visibility     = Visibility.Collapsed;
            PhonePanel.IsHitTestVisible    = !loading;
            CodePanel.IsHitTestVisible     = !loading;
            PasswordPanel.IsHitTestVisible = !loading;
        }
    }
}
