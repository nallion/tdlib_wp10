using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TelegramWP10
{
    public class ChatItem : INotifyPropertyChanged
    {
        public long Id { get; set; }
        public string Title { get; set; }
        
        private string _photoPath = "ms-appx:///Assets/Square44x44Logo.png"; 
        public string PhotoPath 
        { 
            get => _photoPath; 
            set { _photoPath = value; OnPropertyChanged(); } 
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string prop = "") => 
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
