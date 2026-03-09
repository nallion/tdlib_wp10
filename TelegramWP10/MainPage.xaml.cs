using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Newtonsoft.Json.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TelegramWP10
{
    public sealed partial class MainPage : Page
    {
        private IntPtr _client;
        private ObservableCollection<ChatItem> _chatListItems = new ObservableCollection<ChatItem>();
        private Dictionary<long, ChatItem> _chatsDict = new Dictionary<long, ChatItem>();
        private Dictionary<int, long> _fileToChatId = new Dictionary<int, long>();

        public MainPage()
        {
            this.InitializeComponent();
            _client = TdJson.td_json_client_create();
            ChatListView.ItemsSource = _chatListItems;
            Task.Run(() => LongPolling());
            SendParameters();
        }

        private void SendParameters()
        {
            string path = Windows.Storage.ApplicationData.Current.LocalFolder.Path.Replace("\\", "/");
            // ВСТАВЬТЕ СВОИ ДАННЫЕ
            string json = "{\"@type\":\"setTdlibParameters\",\"use_test_dc\":false,\"database_directory\":\"" + path + "/td_db_v4\",\"files_directory\":\"" + path + "/td_files_v4\",\"api_id\":26688287,\"api_hash\":\"5f4afe72bc71dc6ec40f7dcb0c9a822b\",\"system_language_code\":\"ru\",\"device_model\":\"Lumia\",\"system_version\":\"WP10\",\"application_version\":\"1.0\"}";
            TdJson.td_json_client_send(_client, json);
        }

        private void LongPolling()
        {
            while (true)
            {
                IntPtr resPtr = TdJson.td_json_client_receive(_client, 1.0);
                if (resPtr != IntPtr.Zero)
                {
                    string json = TdJson.IntPtrToStringUtf8(resPtr);
                    var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                        try {
                            var update = JObject.Parse(json);
                            HandleUpdate(update["@type"]?.ToString(), update);
                        } catch { }
                    });
                }
            }
        }

        private void HandleUpdate(string type, JObject update)
        {
            switch (type)
            {
                case "updateAuthorizationState":
                    var state = update["authorization_state"]?["@type"]?.ToString();
                    if (state == "authorizationStateWaitPhoneNumber") LoginPanel.Visibility = Visibility.Visible;
                    if (state == "authorizationStateWaitCode") {
                        CodeInput.Visibility = Visibility.Visible;
                        CodeButton.Visibility = Visibility.Visible;
                        StatusText.Text = "Введите код:";
                    }
                    if (state == "authorizationStateReady") {
                        LoginPanel.Visibility = Visibility.Collapsed;
                        ChatListView.Visibility = Visibility.Visible;
                        StatusText.Text = "Ваши чаты:";
                        // Запрашиваем 20 актуальных чатов
                        TdJson.td_json_client_send(_client, "{\"@type\":\"getChats\",\"offset_order\":\"9223372036854775807\",\"offset_chat_id\":0,\"limit\":20}");
                    }
                    break;

                // 1. TDLib рассказывает нам о существовании чата (но мы еще не выводим его на экран!)
                case "updateNewChat":
                    var chat = update["chat"];
                    if (chat == null) return;
                    
                    long id = (long)chat["id"];
                    if (!_chatsDict.ContainsKey(id)) {
                        _chatsDict[id] = new ChatItem { Id = id, Title = chat["title"]?.ToString() ?? "Чат" };

                        // Если есть фото, сразу просим его скачать
                        var smallPhoto = chat["photo"]?["small"];
                        if (smallPhoto != null) {
                            int fId = (int)smallPhoto["id"];
                            _fileToChatId[fId] = id;
                            
                            // Проверяем, может фото уже скачано
                            string localPath = smallPhoto["local"]?["path"]?.ToString();
                            if (!string.IsNullOrEmpty(localPath)) {
                                _chatsDict[id].Photo = new BitmapImage(new Uri(localPath));
                            } else {
                                TdJson.td_json_client_send(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + fId + ",\"priority\":1}");
                            }
                        }
                    }
                    break;

                // 2. TDLib присылает ответ на getChats - это наш РЕАЛЬНЫЙ список чатов
                case "chats":
                    var chatIds = update["chat_ids"];
                    if (chatIds != null) {
                        foreach (var cIdToken in chatIds) {
                            long cId = (long)cIdToken;
                            if (_chatsDict.ContainsKey(cId)) {
                                var item = _chatsDict[cId];
                                // Если чата еще нет на экране — добавляем
                                if (!_chatListItems.Contains(item)) {
                                    _chatListItems.Add(item);
                                }
                            }
                        }
                    }
                    break;

                // 3. TDLib скачала файл аватарки
                case "updateFile":
                    var file = update["file"];
                    if (file == null) return;
                    int fileId = (int)file["id"];
                    string path = file["local"]?["path"]?.ToString();

                    if (!string.IsNullOrEmpty(path) && file["local"]?["is_completed"]?.Value<bool>() == true) {
                        if (_fileToChatId.ContainsKey(fileId)) {
                            long cId = _fileToChatId[fileId];
                            if (_chatsDict.ContainsKey(cId)) {
                                // Превращаем путь в картинку (UWP BitmapImage)
                                _chatsDict[cId].Photo = new BitmapImage(new Uri(path));
                            }
                        }
                    }
                    break;
            }
        }

        private void SendPhone_Click(object sender, RoutedEventArgs e) =>
            TdJson.td_json_client_send(_client, "{\"@type\":\"setAuthenticationPhoneNumber\",\"phone_number\":\"" + PhoneInput.Text + "\"}");

        private void SendCode_Click(object sender, RoutedEventArgs e) =>
            TdJson.td_json_client_send(_client, "{\"@type\":\"checkAuthenticationCode\",\"code\":\"" + CodeInput.Text + "\"}");
    }

    public static class TdJson {
        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr td_json_client_create();
        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern void td_json_client_send(IntPtr client, [MarshalAs(UnmanagedType.LPStr)] string request);
        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr td_json_client_receive(IntPtr client, double timeout);
        public static string IntPtrToStringUtf8(IntPtr ptr) {
            if (ptr == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            byte[] buffer = new byte[len];
            Marshal.Copy(ptr, buffer, 0, len);
            return Encoding.UTF8.GetString(buffer);
        }
    }
}
