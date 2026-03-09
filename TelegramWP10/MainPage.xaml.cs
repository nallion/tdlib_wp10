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
        private ObservableCollection<MessageItem> _messageItems = new ObservableCollection<MessageItem>();
        
        private Dictionary<long, ChatItem> _chatsDict = new Dictionary<long, ChatItem>();
        private Dictionary<int, long> _fileToChatId = new Dictionary<int, long>();
        
        private long _currentChatId = 0;

        public MainPage()
        {
            this.InitializeComponent();
            _client = TdJson.td_json_client_create();
            ChatListView.ItemsSource = _chatListItems;
            MessagesListView.ItemsSource = _messageItems; // Привязка сообщений
            Task.Run(() => LongPolling());
            SendParameters();
        }

        private void SendParameters()
        {
            string path = Windows.Storage.ApplicationData.Current.LocalFolder.Path.Replace("\\", "/");
            // ВСТАВЬ СВОИ ДАННЫЕ
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
                        TdJson.td_json_client_send(_client, "{\"@type\":\"getChats\",\"offset_order\":\"9223372036854775807\",\"offset_chat_id\":0,\"limit\":20}");
                    }
                    break;

                case "updateNewChat":
                    var chat = update["chat"];
                    if (chat == null) return;
                    long id = (long)chat["id"];
                    if (!_chatsDict.ContainsKey(id)) {
                        _chatsDict[id] = new ChatItem { Id = id, Title = chat["title"]?.ToString() ?? "Чат" };
                        var smallPhoto = chat["photo"]?["small"];
                        if (smallPhoto != null) {
                            int fId = (int)smallPhoto["id"];
                            _fileToChatId[fId] = id;
                            string localPath = smallPhoto["local"]?["path"]?.ToString();
                            if (!string.IsNullOrEmpty(localPath)) {
                                _chatsDict[id].Photo = new BitmapImage(new Uri(localPath));
                            } else {
                                TdJson.td_json_client_send(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + fId + ",\"priority\":1}");
                            }
                        }
                    }
                    break;

                case "chats":
                    var chatIds = update["chat_ids"];
                    if (chatIds != null) {
                        foreach (var cIdToken in chatIds) {
                            long cId = (long)cIdToken;
                            if (_chatsDict.ContainsKey(cId) && !_chatListItems.Contains(_chatsDict[cId])) {
                                _chatListItems.Add(_chatsDict[cId]);
                            }
                        }
                    }
                    break;

                case "updateFile":
                    var file = update["file"];
                    if (file == null) return;
                    int fileId = (int)file["id"];
                    string path = file["local"]?["path"]?.ToString();
                    if (!string.IsNullOrEmpty(path) && file["local"]?["is_completed"]?.Value<bool>() == true) {
                        if (_fileToChatId.ContainsKey(fileId) && _chatsDict.ContainsKey(_fileToChatId[fileId])) {
                            _chatsDict[_fileToChatId[fileId]].Photo = new BitmapImage(new Uri(path));
                        }
                    }
                    break;

                // Загрузка истории чата (вызывается при клике)
                case "messages":
                    var msgs = update["messages"];
                    if (msgs != null) {
                        // Сообщения приходят от новых к старым, поэтому добавляем их в начало списка (индекс 0)
                        foreach (var msg in msgs) {
                            var item = ParseMessage(msg);
                            if (item != null) _messageItems.Insert(0, item);
                        }
                        // Прокручиваем вниз
                        if (_messageItems.Count > 0)
                            MessagesListView.ScrollIntoView(_messageItems[_messageItems.Count - 1]);
                    }
                    break;

                // Пришло новое сообщение в реальном времени
                case "updateNewMessage":
                    var newMsg = update["message"];
                    if (newMsg != null && (long)newMsg["chat_id"] == _currentChatId) {
                        var item = ParseMessage(newMsg);
                        if (item != null) {
                            _messageItems.Add(item);
                            MessagesListView.ScrollIntoView(item); // Автоскролл
                        }
                    }
                    break;
            }
        }

        // Вспомогательная функция для разбора JSON-сообщения
        private MessageItem ParseMessage(JToken msg)
        {
            var content = msg["content"];
            string text = "[Вложение]"; // Если прислали фото/стикер, пишем заглушку
            
            if (content?["@type"]?.ToString() == "messageText") {
                text = content["text"]?["text"]?.ToString() ?? "";
            }

            bool isOutgoing = msg["is_outgoing"]?.Value<bool>() ?? false;

            return new MessageItem {
                Id = (long)msg["id"],
                Text = text,
                Alignment = isOutgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Background = isOutgoing ? "#0088cc" : "#444444" // Синий - наши, Серый - чужие
            };
        }

        // КЛИК ПО ЧАТУ ИЗ СПИСКА
        private void ChatListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var clickedChat = (ChatItem)e.ClickedItem;
            _currentChatId = clickedChat.Id;
            CurrentChatTitle.Text = clickedChat.Title;

            // Переключаем интерфейс
            StartPanel.Visibility = Visibility.Collapsed;
            MessagesPanel.Visibility = Visibility.Visible;

            // Очищаем старые сообщения и запрашиваем новые
            _messageItems.Clear();
            TdJson.td_json_client_send(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + _currentChatId + ",\"from_message_id\":0,\"offset\":0,\"limit\":20,\"only_local\":false}");
        }

        // КНОПКА НАЗАД
        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            _currentChatId = 0;
            MessagesPanel.Visibility = Visibility.Collapsed;
            StartPanel.Visibility = Visibility.Visible;
            _messageItems.Clear();
        }

        // ОТПРАВКА СООБЩЕНИЯ
        private void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            string text = MessageInput.Text;
            if (string.IsNullOrWhiteSpace(text) || _currentChatId == 0) return;

            // Экранируем кавычки и переносы строк для JSON
            text = text.Replace("\"", "\\\"").Replace("\r\n", "\\n").Replace("\n", "\\n");

            string request = "{\"@type\":\"sendMessage\",\"chat_id\":" + _currentChatId + ",\"input_message_content\":{\"@type\":\"inputMessageText\",\"text\":{\"@type\":\"formattedText\",\"text\":\"" + text + "\"}}}";
            
            TdJson.td_json_client_send(_client, request);
            MessageInput.Text = ""; // Очищаем поле ввода
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
