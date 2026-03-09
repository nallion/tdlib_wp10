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
            p["database_directory"] = path + "/td_db_v5";
            p["files_directory"] = path + "/td_files_v5";
            p["api_id"] = 26688287; // ВСТАВЬ СВОЙ ID
            p["api_hash"] = "5f4afe72bc71dc6ec40f7dcb0c9a822b"; // ВСТАВЬ СВОЙ HASH
            p["system_language_code"] = "ru";
            p["device_model"] = "Lumia";
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
                        var photo = chat["photo"]?["small"];
                        if (photo != null) {
                            int fId = (int)photo["id"];
                            _fileToChatId[fId] = id;
                            string lp = photo["local"]?["path"]?.ToString();
                            if (!string.IsNullOrEmpty(lp)) _chatsDict[id].Photo = new BitmapImage(new Uri(lp));
                            else TdJson.td_json_client_send(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + fId + ",\"priority\":1}");
                        }
                    }
                    break;

                case "chats":
                    var ids = update["chat_ids"];
                    if (ids != null) {
                        foreach (var cId in ids) {
                            long idL = (long)cId;
                            if (_chatsDict.ContainsKey(idL) && !_chatListItems.Contains(_chatsDict[idL]))
                                _chatListItems.Add(_chatsDict[idL]);
                        }
                    }
                    break;

                case "messages":
                    var msgs = update["messages"];
                    if (msgs != null) {
                        foreach (var m in msgs) _messageItems.Insert(0, ParseMessage(m));
                        if (_messageItems.Count > 0) MessagesListView.ScrollIntoView(_messageItems[_messageItems.Count - 1]);
                    }
                    break;

                case "updateNewMessage":
                    var nm = update["message"];
                    if (nm != null && (long)nm["chat_id"] == _currentChatId) {
                        var item = ParseMessage(nm);
                        _messageItems.Add(item);
                        MessagesListView.ScrollIntoView(item);
                    }
                    break;

                case "updateFile":
                    var f = update["file"];
                    if (f != null && f["local"]?["is_completed"]?.Value<bool>() == true) {
                        int fid = (int)f["id"];
                        if (_fileToChatId.ContainsKey(fid))
                            _chatsDict[_fileToChatId[fid]].Photo = new BitmapImage(new Uri(f["local"]["path"].ToString()));
                    }
                    break;
            }
        }

        private MessageItem ParseMessage(JToken msg)
        {
            string txt = msg["content"]?["text"]?["text"]?.ToString() ?? "[Вложение]";
            bool outg = msg["is_outgoing"]?.Value<bool>() ?? false;
            return new MessageItem {
                Id = (long)msg["id"],
                Text = txt,
                Alignment = outg ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Background = outg ? "#0088cc" : "#444444"
            };
        }

        private void ChatListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var chat = (ChatItem)e.ClickedItem;
            _currentChatId = chat.Id;
            CurrentChatTitle.Text = chat.Title;
            StartPanel.Visibility = Visibility.Collapsed;
            MessagesPanel.Visibility = Visibility.Visible;
            _messageItems.Clear();
            TdJson.td_json_client_send(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + _currentChatId + ",\"from_message_id\":0,\"offset\":0,\"limit\":20}");
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            _currentChatId = 0;
            MessagesPanel.Visibility = Visibility.Collapsed;
            StartPanel.Visibility = Visibility.Visible;
        }

        private void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            string text = MessageInput.Text;
            if (string.IsNullOrWhiteSpace(text) || _currentChatId == 0) return;

            // ИСПРАВЛЕННАЯ ОТПРАВКА ЧЕРЕЗ JOBJECT
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
