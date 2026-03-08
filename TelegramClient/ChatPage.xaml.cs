using TelegramClient.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace TelegramClient
{
    public sealed partial class ChatPage : Page
    {
        private TdService _td => TdService.Instance;
        private ChatItem  _chat;

        public ChatPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _chat = e.Parameter as ChatItem;
            if (_chat == null) { Frame.GoBack(); return; }

            ChatTitle.Text = _chat.Title;

            MessagesList.ItemsSource = _td.Messages;
            _td.Messages.CollectionChanged += OnMessagesChanged;

            _td.OpenChat(_chat.Id);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _td.Messages.CollectionChanged -= OnMessagesChanged;
        }

        private void OnMessagesChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // Скроллим вниз при новом сообщении
            if (_td.Messages.Count > 0)
                MessagesList.ScrollIntoView(_td.Messages[_td.Messages.Count - 1]);
        }

        private void OnSend(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }

        private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                SendMessage();
                e.Handled = true;
            }
        }

        private void SendMessage()
        {
            var text = InputBox.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            _td.SendMessage(text);
            InputBox.Text = string.Empty;
        }

        private void OnBack(object sender, RoutedEventArgs e)
        {
            Frame.GoBack();
        }
    }
}
