using Windows.UI.Xaml;

namespace TelegramWP10
{
    public class MessageItem
    {
        public long Id { get; set; }
        public string Text { get; set; }
        
        // Выравнивание: Right для исходящих, Left для входящих
        public HorizontalAlignment Alignment { get; set; } 
        
        // Цвет пузыря
        public string Background { get; set; } 
    }
}
