using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace TelegramWP10
{
    public class InlineButton
    {
        public string Text { get; set; }
        public string CallbackData { get; set; } // null если не callback
        public string Url { get; set; }          // null если не url
    }

    public class InlineButtonRow
    {
        public List<InlineButton> Buttons { get; set; } = new List<InlineButton>();
    }

    public class MessageEntity {
        public int Offset { get; set; }
        public int Length { get; set; }
        public string Url { get; set; }
    }

    public class MessageItem : INotifyPropertyChanged
    {
        public long Id { get; set; }
        private string _text;
        public string Text { get => _text; set { _text = value; OnPropertyChanged("Text"); } }
        public string Date { get; set; }
        public HorizontalAlignment Alignment { get; set; }
        public string Background { get; set; }
        public string FilePath { get; set; } // путь к файлу видео для открытия

        private string _replyToText;
        public string ReplyToText { get => _replyToText; set { _replyToText = value; OnPropertyChanged("ReplyToText"); OnPropertyChanged("ReplyVisibility"); } }
        public Visibility ReplyVisibility => !string.IsNullOrEmpty(ReplyToText) ? Visibility.Visible : Visibility.Collapsed;

        private BitmapImage _attachedPhoto;
        public BitmapImage AttachedPhoto { get => _attachedPhoto; set { _attachedPhoto = value; OnPropertyChanged("AttachedPhoto"); OnPropertyChanged("PhotoVisibility"); } }
        // PhotoVisibility: показываем если есть превью ИЛИ это обычное видео (не GIF)
        public Visibility PhotoVisibility => (AttachedPhoto != null || (IsVideo && !IsGif)) ? Visibility.Visible : Visibility.Collapsed;

        private bool _isVideo;
        public bool IsVideo { get => _isVideo; set { _isVideo = value; OnPropertyChanged("IsVideo"); OnPropertyChanged("VideoIconVisibility"); OnPropertyChanged("PhotoVisibility"); } }
        // VideoIcon показываем только для обычного видео (не GIF), когда нет прогресса скачивания
        public Visibility VideoIconVisibility => (IsVideo && !IsGif) ? Visibility.Visible : Visibility.Collapsed;

        // GIF: отдельный плеер через MediaElement
        private bool _isGif;
        public bool IsGif { get => _isGif; set { _isGif = value; OnPropertyChanged("IsGif"); OnPropertyChanged("GifPlayerVisibility"); OnPropertyChanged("PhotoVisibility"); OnPropertyChanged("VideoIconVisibility"); } }

        private Uri _gifSource;
        public Uri GifSource { get => _gifSource; set { _gifSource = value; OnPropertyChanged("GifSource"); OnPropertyChanged("GifPlayerVisibility"); } }
        // GifPlayer показываем если это GIF (есть source или грузится)
        public Visibility GifPlayerVisibility => IsGif ? Visibility.Visible : Visibility.Collapsed;

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

        // Статус прочтения
        private bool _isOutgoing = false;
        private bool _isRead = false;
        public bool IsOutgoing { get => _isOutgoing; set { _isOutgoing = value; OnPropertyChanged("IsOutgoing"); OnPropertyChanged("ReadStatusVisibility"); OnPropertyChanged("ReadStatusText"); } }
        public bool IsRead { get => _isRead; set { _isRead = value; OnPropertyChanged("IsRead"); OnPropertyChanged("ReadStatusText"); } }
        public Visibility ReadStatusVisibility => _isOutgoing ? Visibility.Visible : Visibility.Collapsed;
        public string ReadStatusText => _isRead ? "✓✓" : "✓";

        // Ник отправителя (для групп, входящих)
        private string _senderName = "";
        public string SenderName { get => _senderName; set { _senderName = value; OnPropertyChanged("SenderName"); OnPropertyChanged("SenderNameVisibility"); } }
        public Visibility SenderNameVisibility => !string.IsNullOrEmpty(_senderName) && !_isOutgoing ? Visibility.Visible : Visibility.Collapsed;

        public string SenderColor { get; set; } = "#7EC8E3";

        // Ник автора цитаты
        public string ReplyAuthor { get; set; } = "";
        public string ReplyAuthorVisibility => !string.IsNullOrEmpty(ReplyAuthor) ? "Visible" : "Collapsed";

        // Аудио
        private bool _isAudio = false;
        private string _audioDuration = "";
        private string _audioTitle = "";
        private string _audioPlayStatus = "▶";
        private double _audioPosition = 0;
        private double _audioDurationSeconds = 1;
        private string _audioPositionText = "0:00";
        public bool IsAudio { get => _isAudio; set { _isAudio = value; OnPropertyChanged("IsAudio"); OnPropertyChanged("AudioVisibility"); } }
        public string AudioDuration { get => _audioDuration; set { _audioDuration = value; OnPropertyChanged("AudioDuration"); } }
        public string AudioTitle { get => _audioTitle; set { _audioTitle = value; OnPropertyChanged("AudioTitle"); } }
        public string AudioPlayStatus { get => _audioPlayStatus; set { _audioPlayStatus = value; OnPropertyChanged("AudioPlayStatus"); } }
        public double AudioPosition { get => _audioPosition; set { _audioPosition = value; OnPropertyChanged("AudioPosition"); } }
        public double AudioDurationSeconds { get => _audioDurationSeconds; set { _audioDurationSeconds = value > 0 ? value : 1; OnPropertyChanged("AudioDurationSeconds"); } }
        public string AudioPositionText { get => _audioPositionText; set { _audioPositionText = value; OnPropertyChanged("AudioPositionText"); } }
        public Visibility AudioVisibility => _isAudio ? Visibility.Visible : Visibility.Collapsed;

        // Inline-кнопки
        private ObservableCollection<InlineButtonRow> _inlineButtons = new ObservableCollection<InlineButtonRow>();
        public ObservableCollection<InlineButtonRow> InlineButtons { get => _inlineButtons; set { _inlineButtons = value; OnPropertyChanged("InlineButtons"); OnPropertyChanged("InlineButtonsVisibility"); } }
        public Visibility InlineButtonsVisibility => _inlineButtons != null && _inlineButtons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        private string _videoDownloadProgress;
        public string VideoDownloadProgress { get => _videoDownloadProgress; set { _videoDownloadProgress = value; OnPropertyChanged("VideoDownloadProgress"); OnPropertyChanged("VideoProgressVisibility"); } }
        public Visibility VideoProgressVisibility => !string.IsNullOrEmpty(_videoDownloadProgress) ? Visibility.Visible : Visibility.Collapsed;

        // Полный file_id фото для загрузки полноразмерной версии
        public long FullPhotoFileId { get; set; }
        // Entities для ссылок
        public List<MessageEntity> Entities { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
