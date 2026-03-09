using Windows.UI.Xaml;

namespace TelegramWP10
{
    public class MessageItem
    {
        public long Id { get; set; }
        public string Text { get; set; }
        public string Date { get; set; } // Новое поле для времени
        public HorizontalAlignment Alignment { get; set; } 
        public string Background { get; set; } 
    }
}
