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

        private void Log(string m) {
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => {
                DebugConsole.Text = $"[{DateTime.Now:HH:mm:ss}] {m}\n" + DebugConsole.Text;
            });
        }

        private void SendParameters() {
            string path = ApplicationData.Current.LocalFolder.Path.Replace("\\", "/");
            JObject p = new JObject {
                ["@type"] = "setTdlibParameters",
                ["use_test_dc"] = false,
                ["database_directory"] = path + "/td_db_v40", 
                ["api_id"] = 26688287,
                ["api_hash"] = "5f4afe72bc71dc6ec40f7dcb0c9a822b",
                ["system_language_code"] = "ru",
                ["device_model"] = "Lumia",
                ["application_version"] = "1.2"
            };
            TdJson.SendUtf8(_client, p.ToString());
        }

        private void LongPolling() {
            while (true) {
                IntPtr resPtr = TdJson.td_json_client_receive(_client, 1.0);
                if (resPtr != IntPtr.Zero) {
                    string json = TdJson.IntPtrToStringUtf8(resPtr);
                    if (string.IsNullOrEmpty(json)) continue; // Исправление ArgumentNull

                    var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                        try {
                            var update = JObject.Parse(json);
                            string type = update["@type"]?.ToString();
                            // Глубокая отладка: логируем все входящие события кроме шумных
                            if (type != "updateFile" && type != "updateOption" && type != "updateChatLastMessage" && type != "updateChatReadInbox" && type != "updateChatReadOutbox")
                                Log("← " + type + ": " + json.Substring(0, Math.Min(json.Length, 300)));
                            if (type == "error") Log("TG ERR: " + json);
                            HandleUpdate(type, update);
                        } catch (Exception ex) { Log("JSON ERR: " + ex.Message); }
                    });
                }
            }
        }

        private void HandleUpdate(string type, JObject update) {
            switch (type) {
                case "updateConnectionState":
                    var connState = update["state"]?["@type"]?.ToString();
                    Log("CONNECTION: " + connState);
                    break;
                case "updateOption":
                    var optName = update["name"]?.ToString();
                    if (optName == "is_test_dc") {
                        bool isTest = update["value"]?["value"]?.ToObject<bool>() ?? false;
                        Log(isTest ? "✓ ПОДКЛЮЧЕНО К ТЕСТОВОМУ DC" : "✗ ПОДКЛЮЧЕНО К БОЕВОМУ DC");
                    }
                    break;
                case "updateAuthorizationState":
                    var s = update["authorization_state"]?["@type"]?.ToString();
                    if (s == "authorizationStateWaitPhoneNumber") LoginPanel.Visibility = Visibility.Visible;
                    if (s == "authorizationStateWaitCode") { CodeInput.Visibility = Visibility.Visible; CodeButton.Visibility = Visibility.Visible; }
                    if (s == "authorizationStateReady") { LoginPanel.Visibility = Visibility.Collapsed; ChatListView.Visibility = Visibility.Visible; TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"offset_order\":\"9223372036854775807\",\"offset_chat_id\":0,\"limit\":30}"); }
                    break;
                case "updateNewChat":
                    var c = update["chat"]; long id = (long)c["id"];
                    if (!_chatsDict.ContainsKey(id)) _chatsDict[id] = new ChatItem { Id = id, Title = c["title"]?.ToString() };
                    var p = c["photo"]?["small"];
                    if (p != null) {
                        _fileToChatId[(long)p["id"]] = id;
                        bool isCompleted = p["local"]?["is_completed"]?.ToObject<bool>() ?? false;
                        ProcessFile((long)p["id"], p["local"]?["path"]?.ToString(), isCompleted);
                    }
                    break;
                case "updateFile":
                    var f = update["file"];
                    if (f != null && (f["local"]?["is_completed"]?.ToObject<bool>() ?? false)) {
                        long fid = (long)f["id"]; string path = f["local"]["path"]?.ToString();
                        if (_fileToChatId.ContainsKey(fid)) { var t = UpdateAvatar(_fileToChatId[fid], path); }
                        if (_fileToMsgId.ContainsKey(fid)) { var t = UpdateMessagePhoto(_fileToMsgId[fid], path); }
                    }
                    break;
                case "chats":
                    foreach (var cId in update["chat_ids"]) {
                        long cid = (long)cId;
                        if (_chatsDict.ContainsKey(cid) && !_chatListItems.Contains(_chatsDict[cid])) _chatListItems.Add(_chatsDict[cid]);
                    }
                    break;
                case "messages":
                    // Fix bug 2: check that the response belongs to the current open chat
                    if (update["chat_id"] != null && (long)update["chat_id"] != _currentChatId) break;
                    _messageItems.Clear();
                    foreach (var m in update["messages"]) { var item = ParseMessage(m); if (item != null) _messageItems.Insert(0, item); }
                    break;
            }
        }

        private MessageItem ParseMessage(JToken msg) {
            try {
                long msgId = (long)msg["id"];
                var content = msg["content"];
                string type = content["@type"]?.ToString();
                string txt = type == "messageText" ? content["text"]?["text"]?.ToString() : content["caption"]?["text"]?.ToString() ?? "";
                
                var item = new MessageItem {
                    Id = msgId, Text = txt,
                    Date = DateTimeOffset.FromUnixTimeSeconds((long)msg["date"]).LocalDateTime.ToString("HH:mm"),
                    Alignment = (bool)msg["is_outgoing"] ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Background = (bool)msg["is_outgoing"] ? "#0088cc" : "#333333"
                };

                // Fix bug 4: in TDLib the reply info is in msg["reply_to"]["message_id"], not msg["reply_to_message_id"]
                long rId = 0;
                var replyTo = msg["reply_to"];
                if (replyTo != null && replyTo["message_id"] != null && replyTo["message_id"].Type == JTokenType.Integer)
                    rId = (long)replyTo["message_id"];
                item.ReplyToText = (rId != 0) ? "Ответ на сообщение" : null;

                if (type == "messagePhoto") {
                    var ph = content["photo"]?["sizes"]?.Last?["photo"];
                    if (ph != null) {
                        long phFileId = (long)ph["id"];
                        _fileToMsgId[phFileId] = msgId;
                        _messagesDict[msgId] = item;
                        bool phReady = ph["local"]?["is_completed"]?.ToObject<bool>() ?? false;
                        ProcessFile(phFileId, ph["local"]?["path"]?.ToString(), phReady);
                    }
                } else if (type == "messageVideo") {
                    item.IsVideo = true;
                    var v = content["video"]?["thumbnail"]?["file"];
                    if (v != null) {
                        long vFileId = (long)v["id"];
                        _fileToMsgId[vFileId] = msgId;
                        _messagesDict[msgId] = item;
                        bool vReady = v["local"]?["is_completed"]?.ToObject<bool>() ?? false;
                        ProcessFile(vFileId, v["local"]?["path"]?.ToString(), vReady);
                    }
                }

                if (string.IsNullOrEmpty(item.Text) && type != "messagePhoto" && type != "messageVideo") item.Text = "[" + type.Replace("message", "") + "]";
                return item;
            } catch { return null; }
        }

        private void ProcessFile(long fId, string path, bool ready) {
            if (ready && !string.IsNullOrEmpty(path)) {
                Log("FILE READY id=" + fId + " path=" + path);
                if (_fileToChatId.ContainsKey(fId)) { Log("→ UpdateAvatar chatId=" + _fileToChatId[fId]); var t = UpdateAvatar(_fileToChatId[fId], path); }
                if (_fileToMsgId.ContainsKey(fId)) { Log("→ UpdateMsgPhoto msgId=" + _fileToMsgId[fId]); var t = UpdateMessagePhoto(_fileToMsgId[fId], path); }
            } else {
                Log("FILE DOWNLOAD id=" + fId + " ready=" + ready + " path=" + (path ?? "null"));
                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + fId + ",\"priority\":10}");
            }
        }

        private async Task UpdateAvatar(long chatId, string path) {
            try {
                if (string.IsNullOrEmpty(path)) { Log("AVATAR ERR: пустой путь для чата " + chatId); return; }
                var bitmap = new BitmapImage();
                var file = await StorageFile.GetFileFromPathAsync(path);
                using (var s = await file.OpenReadAsync()) {
                    await bitmap.SetSourceAsync(s);
                    if (_chatsDict.ContainsKey(chatId)) _chatsDict[chatId].Photo = bitmap;
                }
            } catch (Exception ex) { Log("AVATAR ERR: " + ex.Message + " | path=" + path); }
        }

        private async Task UpdateMessagePhoto(long msgId, string path) {
            try {
                if (string.IsNullOrEmpty(path)) { Log("PHOTO ERR: пустой путь для msg " + msgId); return; }
                var bitmap = new BitmapImage();
                var file = await StorageFile.GetFileFromPathAsync(path);
                using (var s = await file.OpenReadAsync()) {
                    await bitmap.SetSourceAsync(s);
                    if (_messagesDict.ContainsKey(msgId)) {
                        _messagesDict[msgId].AttachedPhoto = bitmap;
                        _messagesDict[msgId].OnPropertyChanged("PhotoVisibility");
                    }
                }
            } catch (Exception ex) { Log("PHOTO ERR: " + ex.Message + " | path=" + path); }
        }

        private void ChatListView_ItemClick(object sender, ItemClickEventArgs e) {
            var chat = (ChatItem)e.ClickedItem; _currentChatId = chat.Id; CurrentChatTitle.Text = chat.Title;
            _messageItems.Clear(); _messagesDict.Clear(); StartPanel.Visibility = Visibility.Collapsed; MessagesPanel.Visibility = Visibility.Visible;
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
