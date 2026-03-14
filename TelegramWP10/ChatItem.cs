using System;
using System.ComponentModel;
using Windows.UI.Xaml.Media.Imaging;

namespace TelegramWP10
{
    public class ChatItem : INotifyPropertyChanged
    {
        public long Id { get; set; }
        public long OutboxReadId { get; set; }
        public bool IsChannel { get; set; }
        public string Title { get; set; }

        private BitmapImage _photo = new BitmapImage(new Uri("ms-appx:///Assets/StoreLogo.png"));
        public BitmapImage Photo
        {
            get => _photo;
            set { _photo = value; OnPropertyChanged("Photo"); }
        }

        private string _lastMessage = "";
        public string LastMessage
        {
            get => _lastMessage;
            set { _lastMessage = value; OnPropertyChanged("LastMessage"); }
        }

        private string _lastMessageTime = "";
        public string LastMessageTime
        {
            get => _lastMessageTime;
            set { _lastMessageTime = value; OnPropertyChanged("LastMessageTime"); }
        }

        // true = исходящее, false = входящее
        private bool _isOutgoing = false;
        public bool IsOutgoing
        {
            get => _isOutgoing;
            set { _isOutgoing = value; OnPropertyChanged("IsOutgoing"); OnPropertyChanged("StatusText"); OnPropertyChanged("StatusVisibility"); }
        }

        // true = прочитано (двойная галочка), false = отправлено (одинарная)
        private bool _isRead = false;
        public bool IsRead
        {
            get => _isRead;
            set { _isRead = value; OnPropertyChanged("IsRead"); OnPropertyChanged("StatusText"); }
        }

        // Галочки показываем только для исходящих
        public string StatusVisibility => IsOutgoing ? "Visible" : "Collapsed";
        public string StatusText => IsRead ? "✓✓" : "✓";

        private bool _isOnline = false;
        public bool IsOnline
        {
            get => _isOnline;
            set { _isOnline = value; OnPropertyChanged("IsOnline"); OnPropertyChanged("OnlineVisibility"); }
        }
        public string OnlineVisibility => IsOnline ? "Visible" : "Collapsed";

        private int _unreadCount = 0;
        public int UnreadCount
        {
            get => _unreadCount;
            set { _unreadCount = value; OnPropertyChanged("UnreadCount"); OnPropertyChanged("UnreadVisibility"); OnPropertyChanged("UnreadText"); }
        }
        public string UnreadVisibility => _unreadCount > 0 ? "Visible" : "Collapsed";
        public string UnreadText => _unreadCount > 99 ? "99+" : _unreadCount.ToString();

        private bool _isPinned = false;
        public bool IsPinned
        {
            get => _isPinned;
            set { _isPinned = value; OnPropertyChanged("IsPinned"); OnPropertyChanged("PinVisibility"); }
        }
        public string PinVisibility => _isPinned ? "Visible" : "Collapsed";

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
