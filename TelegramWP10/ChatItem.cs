using Windows.UI.Xaml.Media.Imaging;

namespace TelegramWP10
{
    public class ChatItem
    {
        public long Id { get; set; }
        public string Title { get; set; }
        public BitmapImage Photo { get; set; } = new BitmapImage(new Uri("ms-appx:///Assets/StoreLogo.png"));
    }
}
