using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Storage;
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
            _client = TdJson.td_json_client_create();
            ChatListView.ItemsSource = _chatListItems;
            MessagesListView.ItemsSource = _messageItems;
            InitAsync();
        }

        private async void InitAsync() {
            await SendParameters();
            Task.Run(() => LongPolling());
        }

        private async Task SendParameters() {
            string path;
            try {
                var folder = await Windows.Storage.KnownFolders.DocumentsLibrary
                    .CreateFolderAsync("TelegramWP10", Windows.Storage.CreationCollisionOption.OpenIfExists);
                path = folder.Path.Replace("\\", "/");
            } catch (Exception ex) {
                var dialog = new Windows.UI.Popups.MessageDialog(
                    "Нет доступа к папке Документы.\nСессия не будет сохраняться между переустановками.\n\nОшибка: " + ex.Message,
                    "Внимание");
                await dialog.ShowAsync();
                path = ApplicationData.Current.LocalFolder.Path.Replace("\\", "/");
            }
            JObject p = new JObject {
                ["@type"] = "setTdlibParameters",
                ["use_test_dc"] = false,
                ["database_directory"] = path + "/td_db",
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
                    if (string.IsNullOrEmpty(json)) continue;

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

        private void HandleUpdate(string type, JObject update) {
            switch (type) {
                case "updateAuthorizationState":
                    var s = update["authorization_state"]?["@type"]?.ToString();
                    if (s == "authorizationStateWaitPhoneNumber") LoginPanel.Visibility = Visibility.Visible;
                    if (s == "authorizationStateWaitCode") { CodeInput.Visibility = Visibility.Visible; CodeButton.Visibility = Visibility.Visible; }
                    if (s == "authorizationStateReady") { LoginPanel.Visibility = Visibility.Collapsed; ChatListView.Visibility = Visibility.Visible; TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"offset_order\":\"9223372036854775807\",\"offset_chat_id\":0,\"limit\":30}"); }
                    break;
                case "updateNewChat":
                    var c = update["chat"]; long id = (long)c["id"];
                    if (!_chatsDict.ContainsKey(id)) {
                        _chatsDict[id] = new ChatItem { Id = id, Title = c["title"]?.ToString() };
                    }
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
                        if (_chatsDict.ContainsKey(cid) && !_chatListItems.Contains(_chatsDict[cid]))
                            _chatListItems.Add(_chatsDict[cid]);
                    }
                    // После добавления в список — обновляем аватарки для уже скачанных файлов
                    foreach (var kv in _fileToChatId) {
                        if (_chatsDict.ContainsKey(kv.Value) && _chatsDict[kv.Value].Photo != null
                            && _chatsDict[kv.Value].Photo.UriSource?.Scheme != "ms-appx") {
                            _chatsDict[kv.Value].OnPropertyChanged("Photo");
                        }
                    }
                    break;
                case "messages":
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

                var replyTo = msg["reply_to"];
                if (replyTo != null && replyTo["message_id"] != null && replyTo["message_id"].Type == JTokenType.Integer)
                    item.ReplyToText = "Ответ на сообщение";

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

                if (string.IsNullOrEmpty(item.Text) && type != "messagePhoto" && type != "messageVideo")
                    item.Text = "[" + type.Replace("message", "") + "]";
                return item;
            } catch { return null; }
        }

        private void ProcessFile(long fId, string path, bool ready) {
            if (ready && !string.IsNullOrEmpty(path)) {
                if (_fileToChatId.ContainsKey(fId)) { var t = UpdateAvatar(_fileToChatId[fId], path); }
                if (_fileToMsgId.ContainsKey(fId)) { var t = UpdateMessagePhoto(_fileToMsgId[fId], path); }
            } else {
                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + fId + ",\"priority\":10}");
            }
        }

        private async Task UpdateAvatar(long chatId, string path) {
            try {
                if (string.IsNullOrEmpty(path)) return;
                var bitmap = new BitmapImage();
                var file = await StorageFile.GetFileFromPathAsync(path);
                using (var s = await file.OpenReadAsync()) {
                    await bitmap.SetSourceAsync(s);
                    if (_chatsDict.ContainsKey(chatId)) _chatsDict[chatId].Photo = bitmap;
                }
            } catch { }
        }

        private async Task UpdateMessagePhoto(long msgId, string path) {
            try {
                if (string.IsNullOrEmpty(path)) return;
                var bitmap = new BitmapImage();
                var file = await StorageFile.GetFileFromPathAsync(path);
                using (var s = await file.OpenReadAsync()) {
                    await bitmap.SetSourceAsync(s);
                    if (_messagesDict.ContainsKey(msgId))
                        _messagesDict[msgId].AttachedPhoto = bitmap; // сеттер сам вызывает OnPropertyChanged для AttachedPhoto и PhotoVisibility
                }
            } catch { }
        }

        private void ChatListView_ItemClick(object sender, ItemClickEventArgs e) {
            var chat = (ChatItem)e.ClickedItem; _currentChatId = chat.Id; CurrentChatTitle.Text = chat.Title;
            _messageItems.Clear();
            _messagesDict.Clear();
            _fileToMsgId.Clear();
            StartPanel.Visibility = Visibility.Collapsed; MessagesPanel.Visibility = Visibility.Visible;
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
    }
}
