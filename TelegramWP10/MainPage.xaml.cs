using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
        
        // Словари для вложений
        private Dictionary<long, long> _fileToMsgId = new Dictionary<long, long>();
        private Dictionary<long, MessageItem> _messagesDict = new Dictionary<long, MessageItem>();
        
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
                ["database_directory"] = path + "/td_db_v20", 
                ["api_id"] = 26688287,
                ["api_hash"] = "5f4afe72bc71dc6ec40f7dcb0c9a822b",
                ["system_language_code"] = "ru",
                ["device_model"] = "Lumia",
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
                    if (state == "authorizationStateWaitCode") { CodeInput.Visibility = Visibility.Visible; CodeButton.Visibility = Visibility.Visible; }
                    if (state == "authorizationStateReady") {
                        LoginPanel.Visibility = Visibility.Collapsed;
                        ChatListView.Visibility = Visibility.Visible;
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"offset_order\":\"9223372036854775807\",\"offset_chat_id\":0,\"limit\":30}");
                    }
                    break;

                case "updateNewChat":
                    var c = update["chat"];
                    long id = (long)c["id"];
                    var chatItem = new ChatItem { Id = id, Title = c["title"]?.ToString() };
                    _chatsDict[id] = chatItem;
                    var photo = c["photo"]?["small"];
                    if (photo != null) {
                        long fId = (long)photo["id"];
                        _fileToChatId[fId] = id;
                        ProcessFile(fId, photo["local"]?["path"]?.ToString(), (bool)photo["local"]["is_completed"]);
                    }
                    break;

                case "updateFile":
                    var f = update["file"];
                    if (f != null && (bool)f["local"]["is_completed"]) {
                        long fid = (long)f["id"];
                        string path = f["local"]["path"]?.ToString();
                        if (_fileToChatId.ContainsKey(fid)) var t = UpdateAvatar(_fileToChatId[fid], path);
                        if (_fileToMsgId.ContainsKey(fid)) var t = UpdateMessagePhoto(_fileToMsgId[fid], path);
                    }
                    break;

                case "chats":
                    foreach (var cId in update["chat_ids"]) {
                        if (_chatsDict.ContainsKey((long)cId)) _chatListItems.Add(_chatsDict[(long)cId]);
                    }
                    break;

                case "messages":
                    _messageItems.Clear();
                    foreach (var m in update["messages"]) {
                        var item = ParseMessage(m);
                        if (item != null) _messageItems.Insert(0, item);
                    }
                    break;
            }
        }

        private MessageItem ParseMessage(JToken msg) {
            long msgId = (long)msg["id"];
            string txt = msg["content"]?["text"]?["text"]?.ToString() ?? msg["content"]?["caption"]?["text"]?.ToString() ?? "";
            var item = new MessageItem {
                Id = msgId, Text = txt,
                Date = DateTimeOffset.FromUnixTimeSeconds((long)msg["date"]).LocalDateTime.ToString("HH:mm"),
                Alignment = (bool)msg["is_outgoing"] ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Background = (bool)msg["is_outgoing"] ? "#0088cc" : "#333333"
            };

            if (msg["reply_to_message_id"]?.Value<long>() != 0) item.ReplyToText = "Цитата";

            string type = msg["content"]["@type"].ToString();
            if (type == "messagePhoto") {
                var p = msg["content"]["photo"]["sizes"].Last["photo"];
                ProcessMediaItem((long)p["id"], msgId, item, p);
            } else if (type == "messageVideo") {
                item.IsVideo = true;
                var v = msg["content"]["video"]["thumbnail"]?["file"];
                if (v != null) ProcessMediaItem((long)v["id"], msgId, item, v);
            }
            return item;
        }

        private void ProcessMediaItem(long fId, long msgId, MessageItem item, JToken fileObj) {
            _fileToMsgId[fId] = msgId;
            _messagesDict[msgId] = item;
            ProcessFile(fId, fileObj["local"]?["path"]?.ToString(), (bool)fileObj["local"]["is_completed"]);
        }

        private void ProcessFile(long fId, string path, bool isReady) {
            if (isReady) {
                if (_fileToChatId.ContainsKey(fId)) var t = UpdateAvatar(_fileToChatId[fId], path);
                if (_fileToMsgId.ContainsKey(fId)) var t = UpdateMessagePhoto(_fileToMsgId[fId], path);
            } else {
                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + fId + ",\"priority\":10}");
            }
        }

        private async Task UpdateAvatar(long chatId, string path) {
            await Task.Delay(200);
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () => {
                try {
                    var bitmap = new BitmapImage();
                    using (var stream = await (await StorageFile.GetFileFromPathAsync(path)).OpenReadAsync()) {
                        await bitmap.SetSourceAsync(stream);
                        if (_chatsDict.ContainsKey(chatId)) _chatsDict[chatId].Photo = bitmap;
                    }
                } catch { }
            });
        }

        private async Task UpdateMessagePhoto(long msgId, string path) {
            await Task.Delay(200);
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () => {
                try {
                    var bitmap = new BitmapImage();
                    using (var stream = await (await StorageFile.GetFileFromPathAsync(path)).OpenReadAsync()) {
                        await bitmap.SetSourceAsync(stream);
                        if (_messagesDict.ContainsKey(msgId)) _messagesDict[msgId].AttachedPhoto = bitmap;
                    }
                } catch { }
            });
        }
        
        // ... Методы кнопок Back, Send, Login оставить прежними
    }
}
