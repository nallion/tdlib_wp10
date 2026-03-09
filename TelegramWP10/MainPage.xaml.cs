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
            MessagesListView.ItemsSource = _messageItems;
            Task.Run(() => LongPolling());
            SendParameters();
        }

        private void SendParameters()
        {
            string path = Windows.Storage.ApplicationData.Current.LocalFolder.Path.Replace("\\", "/");
            JObject p = new JObject();
            p["@type"] = "setTdlibParameters";
            p["use_test_dc"] = false;
            p["database_directory"] = path + "/td_db_final";
            p["files_directory"] = path + "/td_files_final";
            p["api_id"] = 26688287; // ВСТАВЬ СВОЙ ID
            p["api_hash"] = "5f4afe72bc71dc6ec40f7dcb0c9a822b"; // ВСТАВЬ СВОЙ HASH
            p["system_language_code"] = "ru";
            p["device_model"] = "Lumia WP10";
            p["system_version"] = "10";
            p["application_version"] = "1.0";
            TdJson.td_json_client_send(_client, p.ToString());
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
            // ДИАГНОСТИКА: Если пришла ошибка, выводим её в статус
            if (type == "error") {
                StatusText.Text = "Ошибка ТГ: " + update["message"]?.ToString();
                return;
            }

            switch (type)
            {
                case "updateAuthorizationState":
                    var state = update["authorization_state"]?["@type"]?.ToString();
                    if (state == "authorizationStateWaitPhoneNumber") LoginPanel.Visibility = Visibility.Visible;
                    if (state == "authorizationStateWaitCode") {
                        CodeInput.Visibility = Visibility.Visible;
                        CodeButton.Visibility = Visibility.Visible;
                    }
                    if (state == "authorizationStateReady") {
                        LoginPanel.Visibility = Visibility.Collapsed;
                        ChatListView.Visibility = Visibility.Visible;
                        TdJson.td_json_client_send(_client, "{\"@type\":\"getChats\",\"offset_order\":\"9223372036854775807\",\"offset_chat_id\":0,\"limit\":20}");
                    }
                    break;

                case "updateNewChat":
                    long id = (long)update["chat"]["id"];
                    if (!_chatsDict.ContainsKey(id)) {
                        _chatsDict[id] = new ChatItem { Id = id, Title = update["chat"]["title"]?.ToString() };
                        // Загрузка фото... (код из прошлых версий остается таким же)
                    }
                    break;

                case "chats":
                    var ids = update["chat_ids"];
                    if (ids != null) {
                        foreach (var cId in ids) {
                            if (_chatsDict.ContainsKey((long)cId) && !_chatListItems.Contains(_chatsDict[(long)cId]))
                                _chatListItems.Add(_chatsDict[(long)cId]);
                        }
                    }
                    break;

                // ОБРАБОТКА ИСТОРИИ
                case "messages":
                    var msgs = update["messages"];
                    if (msgs != null) {
                        _messageItems.Clear(); // Очищаем, чтобы не дублировать
                        foreach (var m in msgs) {
                            var parsed = ParseMessage(m);
                            if (parsed != null) _messageItems.Insert(0, parsed);
                        }
                        if (_messageItems.Count > 0)
                            MessagesListView.ScrollIntoView(_messageItems[_messageItems.Count - 1]);
                    }
                    break;

                // НОВОЕ СООБЩЕНИЕ (включая твои отправленные)
                case "updateNewMessage":
                    var nm = update["message"];
                    if (nm != null && (long)nm["chat_id"] == _currentChatId) {
                        var item = ParseMessage(nm);
                        _messageItems.Add(item);
                        MessagesListView.ScrollIntoView(item);
                    }
                    break;
            }
        }

        private MessageItem ParseMessage(JToken msg)
        {
            try {
                var content = msg["content"];
                string txt = "[Тип сообщения не поддерживается]";
                
                if (content["@type"]?.ToString() == "messageText") {
                    txt = content["text"]?["text"]?.ToString();
                }

                return new MessageItem {
                    Id = (long)msg["id"],
                    Text = txt,
                    Alignment = (bool)msg["is_outgoing"] ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Background = (bool)msg["is_outgoing"] ? "#0088cc" : "#333333"
                };
            } catch { return null; }
        }

        private void ChatListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var chat = (ChatItem)e.ClickedItem;
            _currentChatId = chat.Id;
            CurrentChatTitle.Text = chat.Title;
            StartPanel.Visibility = Visibility.Collapsed;
            MessagesPanel.Visibility = Visibility.Visible;
            
            _messageItems.Clear();
            StatusText.Text = "Загрузка истории...";
            
            // Запрашиваем 50 сообщений сразу
            JObject historyReq = new JObject {
                ["@type"] = "getChatHistory",
                ["chat_id"] = _currentChatId,
                ["from_message_id"] = 0,
                ["offset"] = 0,
                ["limit"] = 50,
                ["only_local"] = false
            };
            TdJson.td_json_client_send(_client, historyReq.ToString());
        }

        private void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(MessageInput.Text) || _currentChatId == 0) return;

            // ФИНАЛЬНАЯ СТРУКТУРА ОТПРАВКИ
            JObject req = new JObject {
                ["@type"] = "sendMessage",
                ["chat_id"] = _currentChatId,
                ["input_message_content"] = new JObject {
                    ["@type"] = "inputMessageText",
                    ["text"] = new JObject {
                        ["@type"] = "formattedText",
                        ["text"] = MessageInput.Text
                    }
                }
            };

            TdJson.td_json_client_send(_client, req.ToString());
            MessageInput.Text = ""; 
            // Мы не добавляем сообщение в список вручную! 
            // Оно само придет через `updateNewMessage`, когда сервер его примет.
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) {
            _currentChatId = 0;
            MessagesPanel.Visibility = Visibility.Collapsed;
            StartPanel.Visibility = Visibility.Visible;
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
