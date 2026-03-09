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
using Windows.Storage;

namespace TelegramWP10
{
    public sealed partial class MainPage : Page
    {
        private IntPtr _client;
        private ObservableCollection<ChatItem> _chatListItems = new ObservableCollection<ChatItem>();
        private ObservableCollection<MessageItem> _messageItems = new ObservableCollection<MessageItem>();
        private Dictionary<long, ChatItem> _chatsDict = new Dictionary<long, ChatItem>();
        
        // ИСПРАВЛЕНО: long для ID файлов, чтобы не было обрезания
        private Dictionary<long, long> _fileToChatId = new Dictionary<long, long>();
        
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
            string path = ApplicationData.Current.LocalFolder.Path.Replace("\\", "/");
            JObject p = new JObject {
                ["@type"] = "setTdlibParameters",
                ["use_test_dc"] = false,
                ["database_directory"] = path + "/td_db_v12", 
                ["files_directory"] = path + "/td_files_v12",
                ["api_id"] = 26688287,
                ["api_hash"] = "5f4afe72bc71dc6ec40f7dcb0c9a822b",
                ["system_language_code"] = "ru",
                ["device_model"] = "Lumia UWP",
                ["application_version"] = "1.0"
            };
            TdJson.SendUtf8(_client, p.ToString());
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
                            HandleUpdate(type, update);
                        } catch { }
                    });
                }
            }
        }

        private void HandleUpdate(string type, JObject update)
        {
            if (type == "error") {
                DebugText.Text = "TDLib Error: " + update["message"];
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
                        StatusText.Text = "Статус: Онлайн";
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"offset_order\":\"9223372036854775807\",\"offset_chat_id\":0,\"limit\":30}");
                    }
                    break;

                case "updateNewChat":
                    var c = update["chat"];
                    long id = (long)c["id"];
                    if (!_chatsDict.ContainsKey(id)) {
                        var chatItem = new ChatItem { Id = id, Title = c["title"]?.ToString() };
                        _chatsDict[id] = chatItem;
                        
                        var photo = c["photo"]?["small"];
                        if (photo != null) {
                            long fId = (long)photo["id"]; // ИСПРАВЛЕНО: long
                            _fileToChatId[fId] = id;
                            string lp = photo["local"]?["path"]?.ToString();
                            if (!string.IsNullOrEmpty(lp) && (bool)photo["local"]["is_completed"]) {
                                var ignored = UpdateAvatar(id, lp);
                            } else {
                                // ИСПРАВЛЕНО: приоритет 5 для быстрой загрузки
                                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + fId + ",\"priority\":5}");
                            }
                        }
                    }
                    break;

                case "updateFile":
                    var f = update["file"];
                    if (f != null && f["local"]?["is_completed"]?.Value<bool>() == true) {
                        long fid = (long)f["id"]; // ИСПРАВЛЕНО: long
                        if (_fileToChatId.ContainsKey(fid)) {
                            var ignored = UpdateAvatar(_fileToChatId[fid], f["local"]["path"]?.ToString());
                        }
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

                case "messages": 
                    var msgs = update["messages"];
                    if (msgs != null) {
                        _messageItems.Clear();
                        foreach (var m in msgs) {
                            var parsed = ParseMessage(m);
                            if (parsed != null) _messageItems.Insert(0, parsed);
                        }
                        if (_messageItems.Count > 0) MessagesListView.ScrollIntoView(_messageItems[_messageItems.Count - 1]);
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

        // ИСПРАВЛЕНО: Асинхронная загрузка через StorageFile для надежности
        private async Task UpdateAvatar(long chatId, string path) {
            if (string.IsNullOrEmpty(path)) return;
            try {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using (var stream = await file.OpenReadAsync()) {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    _chatsDict[chatId].Photo = bitmap;
                }
                DebugText.Text = "Avatar OK: " + chatId;
            } catch (Exception ex) {
                DebugText.Text = "Avatar Err: " + ex.Message;
            }
        }

        private MessageItem ParseMessage(JToken msg)
        {
            try {
                string txt = msg["content"]?["text"]?["text"]?.ToString() ?? "[Вложение]";
                long unixTime = (long)msg["date"];
                DateTime dt = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;

                return new MessageItem {
                    Id = (long)msg["id"],
                    Text = txt,
                    Date = dt.ToString("HH:mm"),
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
            TdJson.SendUtf8(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + _currentChatId + ",\"from_message_id\":0,\"offset\":0,\"limit\":40,\"only_local\":false}");
        }

        private void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(MessageInput.Text) || _currentChatId == 0) return;
            JObject req = new JObject {
                ["@type"] = "sendMessage",
                ["chat_id"] = _currentChatId,
                ["input_message_content"] = new JObject {
                    ["@type"] = "inputMessageText",
                    ["text"] = new JObject { ["@type"] = "formattedText", ["text"] = MessageInput.Text }
                }
            };
            TdJson.SendUtf8(_client, req.ToString());
            MessageInput.Text = "";
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) {
            _currentChatId = 0;
            MessagesPanel.Visibility = Visibility.Collapsed;
            StartPanel.Visibility = Visibility.Visible;
        }

        private void SendPhone_Click(object sender, RoutedEventArgs e) =>
            TdJson.SendUtf8(_client, "{\"@type\":\"setAuthenticationPhoneNumber\",\"phone_number\":\"" + PhoneInput.Text + "\"}");

        private void SendCode_Click(object sender, RoutedEventArgs e) =>
            TdJson.SendUtf8(_client, "{\"@type\":\"checkAuthenticationCode\",\"code\":\"" + CodeInput.Text + "\"}");
    }

    public static class TdJson {
        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr td_json_client_create();
        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void td_json_client_send(IntPtr client, byte[] request);
        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr td_json_client_receive(IntPtr client, double timeout);
        public static void SendUtf8(IntPtr client, string json) {
            byte[] utf8Bytes = Encoding.UTF8.GetBytes(json + "\0");
            td_json_client_send(client, utf8Bytes);
        }
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
