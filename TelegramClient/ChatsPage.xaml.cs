using TelegramClient.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace TelegramClient
{
    public sealed partial class ChatsPage : Page
    {
        private TdService _td => TdService.Instance;

        public ChatsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            ChatsList.ItemsSource = _td.Chats;
            _td.Chats.CollectionChanged += OnChatsChanged;

            UpdateEmptyState();
            Loader.IsActive = _td.Chats.Count == 0;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _td.Chats.CollectionChanged -= OnChatsChanged;
        }

        private void OnChatsChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            Loader.IsActive = false;
            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            EmptyState.Visibility = _td.Chats.Count == 0 && !Loader.IsActive
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OnChatClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ChatItem chat)
                Frame.Navigate(typeof(ChatPage), chat);
        }

        private void OnSearch(object sender, RoutedEventArgs e)
        {
            // TODO: поиск по чатам
        }
    }
}
