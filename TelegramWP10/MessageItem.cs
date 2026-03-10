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
        public string FilePath { get; set; } // путь к файлу видео для открытия

        private string _replyToText;
        public string ReplyToText { get => _replyToText; set { _replyToText = value; OnPropertyChanged("ReplyToText"); OnPropertyChanged("ReplyVisibility"); } }
        public Visibility ReplyVisibility => !string.IsNullOrEmpty(ReplyToText) ? Visibility.Visible : Visibility.Collapsed;

        private BitmapImage _attachedPhoto;
        public BitmapImage AttachedPhoto { get => _attachedPhoto; set { _attachedPhoto = value; OnPropertyChanged("AttachedPhoto"); OnPropertyChanged("PhotoVisibility"); } }
        public Visibility PhotoVisibility => AttachedPhoto != null ? Visibility.Visible : Visibility.Collapsed;

        private bool _isVideo;
        public bool IsVideo { get => _isVideo; set { _isVideo = value; OnPropertyChanged("IsVideo"); OnPropertyChanged("VideoIconVisibility"); } }
        public Visibility VideoIconVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;

        // Документ
        private bool _isDocument;
        public bool IsDocument { get => _isDocument; set { _isDocument = value; OnPropertyChanged("IsDocument"); OnPropertyChanged("DocumentVisibility"); } }
        public Visibility DocumentVisibility => IsDocument ? Visibility.Visible : Visibility.Collapsed;

        public string DocumentName { get; set; }
        public string DocumentSize { get; set; }

        private string _downloadStatus = "⬇ Скачать";
        public string DownloadStatus { get => _downloadStatus; set { _downloadStatus = value; OnPropertyChanged("DownloadStatus"); } }

        private bool _isDownloaded = false;
        public bool IsDownloaded { get => _isDownloaded; set { _isDownloaded = value; OnPropertyChanged("IsDownloaded"); OnPropertyChanged("DownloadStatus"); } }

        // Реакции
        private string _reactions = "";
        public string Reactions { get => _reactions; set { _reactions = value; OnPropertyChanged("Reactions"); OnPropertyChanged("ReactionsVisibility"); } }
        public Visibility ReactionsVisibility => !string.IsNullOrEmpty(_reactions) ? Visibility.Visible : Visibility.Collapsed;

        // Аудио
        private bool _isAudio = false;
        private string _audioDuration = "";
        private string _audioTitle = "";
        private string _audioPlayStatus = "▶";
        public bool IsAudio { get => _isAudio; set { _isAudio = value; OnPropertyChanged("IsAudio"); OnPropertyChanged("AudioVisibility"); } }
        public string AudioDuration { get => _audioDuration; set { _audioDuration = value; OnPropertyChanged("AudioDuration"); } }
        public string AudioTitle { get => _audioTitle; set { _audioTitle = value; OnPropertyChanged("AudioTitle"); } }
        public string AudioPlayStatus { get => _audioPlayStatus; set { _audioPlayStatus = value; OnPropertyChanged("AudioPlayStatus"); } }
        public Visibility AudioVisibility => _isAudio ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
