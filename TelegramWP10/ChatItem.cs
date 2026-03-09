using System;
using System.ComponentModel;
using Windows.UI.Xaml.Media.Imaging;

namespace TelegramWP10
{
    public class ChatItem : INotifyPropertyChanged
    {
        public long Id { get; set; }
        public string Title { get; set; }
        
        private BitmapImage _photo = new BitmapImage(new Uri("ms-appx:///Assets/StoreLogo.png"));
        public BitmapImage Photo 
        { 
            get => _photo; 
            set 
            { 
                _photo = value; 
                OnPropertyChanged("Photo"); 
            } 
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
