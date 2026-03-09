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
            p["database_directory"] = path + "/td_db_final_v1"; // Новая папка для чистого теста
            p["files_directory"] = path + "/td_files_final_v1";
            p["api_id"] = 26688287; // ТВОЙ ID
            p["api_hash"] = "5f4afe72bc71dc6ec40f7dcb0c9a822b"; // ТВОЙ HASH
            p["system_language_code"] = "ru";
            p["device_model"] = "Lumia";
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
                            string type = update["@type"]?.ToString();
                            DebugText.Text = "Последний апдейт: " + type; // ДЕБАГ
                            HandleUpdate(type, update);
                        } catch { }
                    });
                }
            }
        }

        private void HandleUpdate(string type, JObject update)
        {
            if (type == "error") {
                StatusText.Text = "ОШИБКА: " + update["message"];
                return;
            }

            switch (type)
            {
                case "updateAuthorizationState":
                    var state = update["authorization_state"]?["@type"]?.ToString();
                    if (state == "authorizationStateWaitPhoneNumber") LoginPanel.Visibility = Visibility.Visible;
                    if (state == "authorizationStateWaitCode") { CodeInput.Visibility = Visibility.Visible; CodeButton.Visibility = Visibility.Visible; }
                    if (state == "authorizationStateReady") {
                        LoginPanel.Visibility = Visibility.Collapsed;
                        ChatListView.Visibility = Visibility.Visible;
                        StatusText.Text = "Статус: Готов";
                        TdJson.td_json_client_send(_client, "{\"@type\":\"getChats\",\"offset_order\":\"9223372036854775807\",\"offset_chat_id\":0,\"limit\":30}");
                    }
                    break;

                case "updateNewChat":
                    var c = update["chat"];
                    long id = (long)c["id"];
                    if (!_chatsDict.ContainsKey(id)) {
                        _chatsDict[id] = new ChatItem { Id = id, Title = c["title"]?.ToString() };
                        // Тут можно добавить код загрузки фото из прошлых ответов
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

                case "messages": // Ответ на getChatHistory
                    var msgs = update["messages"];
                    if (msgs != null) {
                        _messageItems.Clear(); // Чистим перед загрузкой
                        foreach (var m in msgs) {
                            var parsed = ParseMessage(m);
                            if (parsed != null) _messageItems.Insert(0, parsed); // В начало, т.к. ТГ шлет от новых к старым
                        }
                        if (_messageItems.Count > 0)
                            MessagesListView.ScrollIntoView(_messageItems[_messageItems.Count - 1]);
                    }
                    break;

                case "updateNewMessage":
                    var nm = update["message"];
                    if (nm != null && (long)nm["chat_id"] == _currentChatId) {
                        var item = ParseMessage(nm);
                        if (item != null) {
                            _messageItems.Add(item);
                            MessagesListView.ScrollIntoView(item);
                        }
                    }
                    break;
            }
        }

        private MessageItem ParseMessage(JToken msg)
        {
            try {
                string txt = "";
                var content = msg["content"];
                if (content["@type"].ToString() == "messageText") txt = content["text"]["text"].ToString();
                else txt = "[Вложение]";

                return new MessageItem {
                    Id = (long)msg["id"],
                    Text = txt,
                    Alignment = (bool)msg["is_outgoing"] ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Background = (bool)msg["is_outgoing"] ? "#0088cc" : "#444444"
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
            
            // ЗАПРОС ИСТОРИИ (Явный и чистый)
            JObject h = new JObject();
            h["@type"] = "getChatHistory";
            h["chat_id"] = _currentChatId;
            h["from_message_id"] = 0;
            h["offset"] = 0;
            h["limit"] = 30;
            h["only_local"] = false;
            TdJson.td_json_client_send(_client, h.ToString());
        }

        private void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            string text = MessageInput.Text;
            if (string.IsNullOrWhiteSpace(text) || _currentChatId == 0) return;

            // ФОРМИРУЕМ ОТПРАВКУ СТРОГО
            JObject req = new JObject {
                ["@type"] = "sendMessage",
                ["chat_id"] = _currentChatId,
                ["input_message_content"] = new JObject {
                    ["@type"] = "inputMessageText",
                    ["text"] = new JObject {
                        ["@type"] = "formattedText",
                        ["text"] = text
                    }
                }
            };

            TdJson.td_json_client_send(_client, req.ToString());
            MessageInput.Text = "";
            // Сообщение появится в списке через updateNewMessage автоматически
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
