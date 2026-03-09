using System.ComponentModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace TelegramWP10
{
    public class MessageItem : INotifyPropertyChanged
    {
        public long Id { get; set; }
        public string Text { get; set; }
        public string Date { get; set; }
        public HorizontalAlignment Alignment { get; set; }
        public string Background { get; set; }

        private string _replyToText;
        public string ReplyToText { get => _replyToText; set { _replyToText = value; OnPropertyChanged("ReplyToText"); OnPropertyChanged("ReplyVisibility"); } }
        public Visibility ReplyVisibility => !string.IsNullOrEmpty(ReplyToText) ? Visibility.Visible : Visibility.Collapsed;

        private BitmapImage _attachedPhoto;
        public BitmapImage AttachedPhoto { get => _attachedPhoto; set { _attachedPhoto = value; OnPropertyChanged("AttachedPhoto"); OnPropertyChanged("PhotoVisibility"); } }
        public Visibility PhotoVisibility => AttachedPhoto != null ? Visibility.Visible : Visibility.Collapsed;

        private bool _isVideo;
        public bool IsVideo { get => _isVideo; set { _isVideo = value; OnPropertyChanged("IsVideo"); OnPropertyChanged("VideoIconVisibility"); } }
        public Visibility VideoIconVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
