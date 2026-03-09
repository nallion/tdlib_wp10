using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.ApplicationModel.DataTransfer;
using Newtonsoft.Json.Linq;

namespace TelegramWP10
{
    public sealed partial class MainPage : Page
    {
        private IntPtr _client;
        private ObservableCollection<ChatItem> _chatListItems = new ObservableCollection<ChatItem>();
        private ObservableCollection<MessageItem> _messageItems = new ObservableCollection<MessageItem>();
        private Dictionary<long, ChatItem> _chatsDict = new Dictionary<long, ChatItem>();
        private Dictionary<long, long> _fileToChatId = new Dictionary<long, long>();
        private Dictionary<long, long> _fileToMsgId = new Dictionary<long, long>();
        private Dictionary<long, MessageItem> _messagesDict = new Dictionary<long, MessageItem>();
        private long _currentChatId = 0;

        public MainPage()
        {
            this.InitializeComponent();
            Application.Current.UnhandledException += (s, e) => { Log("CRASH: " + e.Message); e.Handled = true; };
            _client = TdJson.td_json_client_create();
            ChatListView.ItemsSource = _chatListItems;
            MessagesListView.ItemsSource = _messageItems;
            Task.Run(() => LongPolling());
            SendParameters();
        }

        private void Log(string m) { var t = Run(() => DebugConsole.Text += "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + m + "\n"); }
        private async Task Run(Action a) { await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => a()); }

        private void SendParameters()
        {
            JObject req = new JObject
            {
                ["@type"] = "setTdlibParameters",
                ["parameters"] = new JObject
                {
                    ["use_test_dc"] = true, // ВКЛЮЧЕНО: Тестовые датацентры Telegram
                    ["use_file_database"] = true,
                    ["use_chat_info_database"] = true,
                    ["use_message_database"] = true,
                    ["use_secret_chats"] = false,
                    ["api_id"] = 2390141, 
                    ["api_hash"] = "9f27092921966d48f7605d8f28f00155",
                    ["system_language_code"] = "ru",
                    ["device_model"] = "Lumia",
                    ["system_version"] = "Windows 10 Mobile",
                    ["application_version"] = "1.0",
                    ["database_directory"] = ApplicationData.Current.LocalFolder.Path + "\\tdlib_test", // Изменено на test для изоляции
                    ["files_directory"] = ApplicationData.Current.LocalFolder.Path + "\\tdlib_files_test"
                }
            };
            TdJson.SendUtf8(_client, req.ToString());
            TdJson.SendUtf8(_client, "{\"@type\":\"checkDatabaseEncryptionKey\",\"encryption_key\":\"\"}");
        }

        private async void LongPolling()
        {
            while (true)
            {
                IntPtr res = TdJson.td_json_client_receive(_client, 10.0);
                if (res == IntPtr.Zero) continue;
                string s = TdJson.PtrToUtf8String(res);
                await Run(() => HandleUpdate(s));
            }
        }

        private void HandleUpdate(string json)
        {
            try
            {
                JObject u = JObject.Parse(json);
                string type = (string)u["@type"];

                if (type == "updateAuthorizationState")
                {
                    string state = (string)u["authorization_state"]["@type"];
                    Log("AUTH: " + state);
                    AuthPanel.Visibility = (state == "authorizationStateWaitPhoneNumber" || state == "authorizationStateWaitCode") ? Visibility.Visible : Visibility.Collapsed;
                    PhoneInput.Visibility = (state == "authorizationStateWaitPhoneNumber") ? Visibility.Visible : Visibility.Collapsed;
                    CodeInput.Visibility = (state == "authorizationStateWaitCode") ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (type == "updateNewChat")
                {
                    var c = u["chat"];
                    long id = (long)c["id"];
                    var item = new ChatItem { Id = id, Title = (string)c["title"] };
                    _chatsDict[id] = item;
                    _chatListItems.Add(item);
                }
                else if (type == "updateNewMessage")
                {
                    var m = u["message"];
                    long chatId = (long)m["chat_id"];
                    if (chatId == _currentChatId)
                    {
                        var msg = new MessageItem { Text = GetMsgText(m) };
                        _messageItems.Add(msg);
                    }
                }
                else if (type == "updateFile")
                {
                    var f = u["file"];
                    if ((bool)f["local"]["is_downloading_completed"])
                    {
                        long fid = (long)f["id"];
                        if (_fileToChatId.ContainsKey(fid))
                        {
                            long cid = _fileToChatId[fid];
                            _chatsDict[cid].Photo = new BitmapImage(new Uri((string)f["local"]["path"]));
                        }
                    }
                }
            }
            catch (Exception ex) { Log("ERR: " + ex.Message); }
        }

        private string GetMsgText(JToken m)
        {
            var c = m["content"];
            string type = (string)c["@type"];
            if (type == "messageText") return (string)c["text"]["text"];
            if (type == "messagePhoto") return "[Фото]";
            return "[" + type + "]";
        }

        private void Chat_Click(object sender, ItemClickEventArgs e)
        {
            var item = (ChatItem)e.ClickedItem;
            _currentChatId = item.Id;
            _messageItems.Clear();
            StartPanel.Visibility = Visibility.Collapsed;
            MessagesPanel.Visibility = Visibility.Visible;
            ChatTitle.Text = item.Title;
            TdJson.SendUtf8(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + _currentChatId + ",\"from_message_id\":0,\"offset\":0,\"limit\":50}");
        }

        private void SendMessage_Click(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(MessageInput.Text)) return;
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

        private void BackButton_Click(object sender, RoutedEventArgs e) { _currentChatId = 0; MessagesPanel.Visibility = Visibility.Collapsed; StartPanel.Visibility = Visibility.Visible; }
        private void SendPhone_Click(object sender, RoutedEventArgs e) => TdJson.SendUtf8(_client, "{\"@type\":\"setAuthenticationPhoneNumber\",\"phone_number\":\"" + PhoneInput.Text + "\"}");
        private void SendCode_Click(object sender, RoutedEventArgs e) => TdJson.SendUtf8(_client, "{\"@type\":\"checkAuthenticationCode\",\"code\":\"" + CodeInput.Text + "\"}");
        private void CopyLog_Click(object sender, RoutedEventArgs e) { var p = new DataPackage(); p.SetText(DebugConsole.Text); Clipboard.SetContent(p); }
    }
}
