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
        private long _pendingHistoryChatId = 0;
        private string _dbPath = "";
        private bool _connectionReady = false;
        private bool _isAuthorized = false;
        private long _pendingChatHistoryId = 0;
        private StorageFolder _filesFolder = null;
        private StorageFile _logFile = null;

        public MainPage()
        {
            this.InitializeComponent();
            _client = TdJson.td_json_client_create();
            ChatListView.ItemsSource = _chatListItems;
            MessagesListView.ItemsSource = _messageItems;
            InitAsync();
        }

        private async void Log(string m) {
            try {
                if (_logFile == null) return;
                await FileIO.AppendTextAsync(_logFile, $"[{DateTime.Now:HH:mm:ss.fff}] {m}\r\n");
            } catch { }
        }

        private async void InitAsync() {
            try {
                var appFolder = await Windows.Storage.KnownFolders.MusicLibrary
                    .CreateFolderAsync("TelegramWP10", CreationCollisionOption.OpenIfExists);
                _dbPath = appFolder.Path.Replace("\\", "/") + "/td_db";
                _filesFolder = await appFolder.CreateFolderAsync("td_db_files", CreationCollisionOption.OpenIfExists);
                string logName = "log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                _logFile = await appFolder.CreateFileAsync(logName, CreationCollisionOption.ReplaceExisting);
                Log("=== СТАРТ === db=" + _dbPath);
                Log("files=" + _filesFolder.Path);
            } catch (Exception ex) {
                await new Windows.UI.Popups.MessageDialog("Ошибка хранилища:\n" + ex.Message).ShowAsync();
                return;
            }
            Task.Run(() => LongPolling());
        }

        private void SendParameters() {
            JObject p = new JObject {
                ["@type"] = "setTdlibParameters",
                ["use_test_dc"] = false,
                ["database_directory"] = _dbPath,
                ["files_directory"] = _filesFolder?.Path.Replace("\\", "/") ?? _dbPath + "_files",
                ["database_encryption_key"] = "",
                ["use_file_database"] = true,
                ["use_chat_info_database"] = true,
                ["use_message_database"] = true,
                ["use_secret_chats"] = false,
                ["api_id"] = 26688287,
                ["api_hash"] = "5f4afe72bc71dc6ec40f7dcb0c9a822b",
                ["system_language_code"] = "ru",
                ["device_model"] = "Lumia",
                ["system_version"] = "10",
                ["application_version"] = "1.2"
            };
            TdJson.SendUtf8(_client, p.ToString());
            Log("SendParameters sent");
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
                            if (type != "updateOption" && type != "updateChatLastMessage" &&
                                type != "updateChatReadInbox" && type != "updateChatReadOutbox" &&
                                type != "updateChatPosition" && type != "updateChatPermissions")
                                Log("← " + type + " | " + json.Substring(0, Math.Min(json.Length, 300)));
                            HandleUpdate(type, update);
                        } catch (Exception ex) { Log("PARSE ERR: " + ex.Message); }
                    });
                }
            }
        }

        private void HandleUpdate(string type, JObject update) {
            switch (type) {
                case "updateAuthorizationState":
                    var s = update["authorization_state"]?["@type"]?.ToString();
                    Log("AUTH: " + s);
                    if (s == "authorizationStateWaitTdlibParameters") SendParameters();
                    if (s == "authorizationStateWaitPhoneNumber") {
                        LoginStatus.Text = "Введите номер телефона";
                        PhoneInput.IsEnabled = true;
                        PhoneButton.IsEnabled = true;
                    }
                    if (s == "authorizationStateWaitCode") {
                        LoginStatus.Text = "Код отправлен. Проверьте Telegram или SMS.";
                        PhoneInput.IsEnabled = false;
                        PhoneButton.IsEnabled = false;
                        CodeInput.Visibility = Visibility.Visible;
                        CodeButton.Visibility = Visibility.Visible;
                        CodeInput.Focus(FocusState.Programmatic);
                    }
                    if (s == "authorizationStateWaitPassword")
                        LoginStatus.Text = "Введите пароль 2FA";
                    if (s == "authorizationStateReady") {
                        _isAuthorized = true;
                        LoginPanel.Visibility = Visibility.Collapsed;
                        ChatListView.Visibility = Visibility.Visible;
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"offset_order\":\"9223372036854775807\",\"offset_chat_id\":0,\"limit\":30}");
                    }
                    if (s == "authorizationStateLoggingOut" || s == "authorizationStateClosed") {
                        LoginPanel.Visibility = Visibility.Visible;
                        LoginStatus.Text = "Выход...";
                    }
                    break;

                case "error":
                    string errMsg = update["message"]?.ToString();
                    Log("ERROR: " + errMsg);
                    LoginStatus.Text = "Ошибка: " + errMsg;
                    PhoneButton.IsEnabled = true;
                    CodeButton.IsEnabled = true;
                    break;

                case "updateNewChat":
                    var c = update["chat"];
                    long chatId = (long)c["id"];
                    Log("updateNewChat id=" + chatId + " _isAuthorized=" + _isAuthorized
                        + " LoginPanel=" + LoginPanel.Visibility
                        + " ChatListView=" + ChatListView.Visibility
                        + " StartPanel=" + StartPanel.Visibility);
                    // Если пришёл updateNewChat — TDLib уже авторизован (сессия сохранена)
                    if (!_isAuthorized) {
                        _isAuthorized = true;
                        Log("AUTH via updateNewChat (saved session) — switching UI");
                        LoginPanel.Visibility = Visibility.Collapsed;
                        ChatListView.Visibility = Visibility.Visible;
                        Log("After switch: LoginPanel=" + LoginPanel.Visibility + " ChatListView=" + ChatListView.Visibility);
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"offset_order\":\"9223372036854775807\",\"offset_chat_id\":0,\"limit\":30}");
                        Log("getChats sent");
                    }
                    if (!_chatsDict.ContainsKey(chatId))
                        _chatsDict[chatId] = new ChatItem { Id = chatId, Title = c["title"]?.ToString() };
                    var phSmall = c["photo"]?["small"];
                    if (phSmall != null) {
                        long phFileId = (long)phSmall["id"];
                        _fileToChatId[phFileId] = chatId;
                        string phPath = phSmall["local"]?["path"]?.ToString();
                        Log("AVATAR chat=" + chatId + " file_id=" + phFileId + " path=" + phPath);
                        if (!string.IsNullOrEmpty(phPath))
                            { var t = UpdateAvatar(chatId, phPath); }
                        else
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + phFileId + ",\"priority\":1,\"synchronous\":false}");
                    }
                    break;

                case "updateFile":
                case "file":
                    var fileObj = (type == "updateFile") ? update["file"] as JObject : update;
                    if (fileObj != null) {
                        long fid = fileObj["id"] != null ? (long)fileObj["id"] : 0;
                        string fpath = fileObj["local"]?["path"]?.ToString();
                        Log("FILE id=" + fid + " path=" + fpath);
                        if (fid != 0 && !string.IsNullOrEmpty(fpath)) {
                            if (_fileToChatId.ContainsKey(fid)) { var t = UpdateAvatar(_fileToChatId[fid], fpath); }
                            if (_fileToMsgId.ContainsKey(fid)) {
                                long mid = _fileToMsgId[fid];
                                var t = UpdateMessagePhoto(mid, fpath);
                                if (_messagesDict.ContainsKey(mid) && _messagesDict[mid].IsVideo)
                                    _messagesDict[mid].FilePath = fpath;
                            }
                        }
                    }
                    break;

                case "updateNewMessage":
                    var newMsg = update["message"];
                    long newMsgChatId = newMsg?["chat_id"]?.ToObject<long>() ?? 0;
                    Log("updateNewMessage chat=" + newMsgChatId + " current=" + _currentChatId);
                    // Игнорируем если история ещё не загружена (LoadingIndicator видим)
                    if (newMsgChatId == _currentChatId && newMsg != null
                        && LoadingIndicator.Visibility == Visibility.Collapsed) {
                        var newItem = ParseMessage(newMsg);
                        if (newItem != null) {
                            _messageItems.Add(newItem);
                            MessagesListView.ScrollIntoView(newItem);
                        }
                    }
                    break;

                case "updateConnectionState":
                    var connState = update["state"]?["@type"]?.ToString();
                    Log("CONN: " + connState);
                    if (connState == "connectionStateReady") {
                        _connectionReady = true;
                        ConnectionStatus.Visibility = Visibility.Collapsed;
                        // Если есть отложенный запрос истории — выполняем
                        if (_pendingChatHistoryId != 0) {
                            TdJson.SendUtf8(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + _pendingChatHistoryId + ",\"from_message_id\":0,\"offset\":0,\"limit\":50}");
                            _pendingChatHistoryId = 0;
                        }
                    } else {
                        _connectionReady = false;
                        string connText = connState == "connectionStateConnecting" ? "Подключение..."
                            : connState == "connectionStateUpdating" ? "Обновление..."
                            : connState == "connectionStateWaitingForNetwork" ? "Нет сети..."
                            : "...";
                        ConnectionStatusText.Text = connText;
                        ConnectionStatus.Visibility = Visibility.Visible;
                    }
                    break;


                    Log("chats count=" + (update["chat_ids"] as JArray)?.Count);
                    foreach (var cId in update["chat_ids"]) {
                        long cid = (long)cId;
                        if (_chatsDict.ContainsKey(cid) && !_chatListItems.Contains(_chatsDict[cid]))
                            _chatListItems.Add(_chatsDict[cid]);
                    }
                    break;

                case "chats":
                    Log("chats received count=" + (update["chat_ids"] as JArray)?.Count
                        + " ChatListView=" + ChatListView.Visibility
                        + " _chatListItems=" + _chatListItems.Count
                        + " _chatsDict=" + _chatsDict.Count);
                    foreach (var cId in update["chat_ids"]) {
                        long cid = (long)cId;
                        if (_chatsDict.ContainsKey(cid) && !_chatListItems.Contains(_chatsDict[cid]))
                            _chatListItems.Add(_chatsDict[cid]);
                    }
                    Log("chats after fill _chatListItems=" + _chatListItems.Count);
                    break;

                case "messages":
                    long expectedChat = _pendingHistoryChatId;
                    var msgs = update["messages"] as JArray;
                    Log("messages expected=" + expectedChat + " current=" + _currentChatId + " count=" + msgs?.Count);
                    if (expectedChat != _currentChatId) { Log("SKIP — user switched chat"); break; }
                    // Обновляем только если новых сообщений больше чем уже показано
                    if (msgs == null || msgs.Count <= _messageItems.Count) {
                        Log("SKIP — no new messages (have " + _messageItems.Count + ", got " + msgs?.Count + ")");
                        break;
                    }
                    _messageItems.Clear();
                    for (int i = msgs.Count - 1; i >= 0; i--) {
                        var item = ParseMessage(msgs[i]);
                        if (item != null) _messageItems.Add(item);
                    }
                    Log("rendered " + _messageItems.Count + " messages");
                    // Показываем список только когда сообщения загружены
                    LoadingIndicator.Visibility = Visibility.Collapsed;
                    MessagesListView.Visibility = Visibility.Visible;
                    var ignored2 = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => {
                        if (_messageItems.Count > 0)
                            MessagesListView.ScrollIntoView(_messageItems[_messageItems.Count - 1]);
                    });
                    break;
            }
        }

        private MessageItem ParseMessage(JToken msg) {
            try {
                long msgId = (long)msg["id"];
                var content = msg["content"];
                string type = content["@type"]?.ToString();
                string txt = type == "messageText"
                    ? content["text"]?["text"]?.ToString() ?? ""
                    : content["caption"]?["text"]?.ToString() ?? "";

                var item = new MessageItem {
                    Id = msgId, Text = txt,
                    Date = DateTimeOffset.FromUnixTimeSeconds((long)msg["date"]).LocalDateTime.ToString("HH:mm"),
                    Alignment = (bool)msg["is_outgoing"] ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Background = (bool)msg["is_outgoing"] ? "#0088cc" : "#333333"
                };

                var replyTo = msg["reply_to"];
                if (replyTo != null && replyTo["@type"]?.ToString() == "messageReplyToMessage")
                    item.ReplyToText = "Ответ на сообщение";

                if (type == "messagePhoto") {
                    var sizes = content["photo"]?["sizes"] as JArray;
                    if (sizes != null && sizes.Count > 0) {
                        var fileToken = sizes[sizes.Count - 1]["photo"] as JObject;
                        Log("PHOTO msg=" + msgId + " fileToken=" + (fileToken?["id"]?.ToString() ?? "NULL")
                            + " path=" + fileToken?["local"]?["path"]);
                        if (fileToken != null) {
                            long pfid = (long)fileToken["id"];
                            _fileToMsgId[pfid] = msgId;
                            _messagesDict[msgId] = item;
                            string phPath = fileToken["local"]?["path"]?.ToString();
                            if (!string.IsNullOrEmpty(phPath))
                                { var t = UpdateMessagePhoto(msgId, phPath); }
                            else
                                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + pfid + ",\"priority\":10,\"synchronous\":false}");
                        }
                    }
                } else if (type == "messageVideo") {
                    item.IsVideo = true;
                    var videoFile = content["video"]?["video"] as JObject;
                    var thumb = content["video"]?["thumbnail"]?["file"] as JObject;
                    if (videoFile != null) {
                        long vfid = (long)videoFile["id"];
                        _fileToMsgId[vfid] = msgId;
                        _messagesDict[msgId] = item;
                        string vPath = videoFile["local"]?["path"]?.ToString();
                        Log("VIDEO file id=" + vfid + " path=" + vPath);
                        if (!string.IsNullOrEmpty(vPath)) item.FilePath = vPath;
                    }
                    if (thumb != null) {
                        long tfid = (long)thumb["id"];
                        _fileToMsgId[tfid] = msgId;
                        _messagesDict[msgId] = item;
                        string tPath = thumb["local"]?["path"]?.ToString();
                        Log("VIDEO thumb id=" + tfid + " path=" + tPath);
                        if (!string.IsNullOrEmpty(tPath))
                            { var t = UpdateMessagePhoto(msgId, tPath); }
                        else
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + tfid + ",\"priority\":10,\"synchronous\":false}");
                    }
                }

                if (string.IsNullOrEmpty(item.Text) && type != "messagePhoto" && type != "messageVideo")
                    item.Text = "[" + type.Replace("message", "") + "]";
                return item;
            } catch (Exception ex) { Log("ParseMessage ERR: " + ex.Message); return null; }
        }

        private async Task UpdateAvatar(long chatId, string path) {
            try {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var bitmap = new BitmapImage();
                using (var stream = await file.OpenReadAsync())
                    await bitmap.SetSourceAsync(stream);
                if (_chatsDict.ContainsKey(chatId)) {
                    _chatsDict[chatId].Photo = bitmap;
                    Log("UpdateAvatar OK chat=" + chatId);
                }
            } catch (Exception ex) { Log("UpdateAvatar ERR chat=" + chatId + " | " + ex.Message); }
        }

        private async Task UpdateMessagePhoto(long msgId, string path) {
            try {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var bitmap = new BitmapImage();
                using (var stream = await file.OpenReadAsync())
                    await bitmap.SetSourceAsync(stream);
                if (_messagesDict.ContainsKey(msgId)) {
                    _messagesDict[msgId].AttachedPhoto = bitmap;
                    Log("UpdateMsgPhoto OK msg=" + msgId);
                } else {
                    Log("UpdateMsgPhoto NOT IN DICT msg=" + msgId);
                }
            } catch (Exception ex) { Log("UpdateMsgPhoto ERR msg=" + msgId + " | " + ex.Message); }
        }

        private void ChatListView_ItemClick(object sender, ItemClickEventArgs e) {
            var chat = (ChatItem)e.ClickedItem;
            if (chat.Id == _currentChatId) return;
            _currentChatId = chat.Id;
            _pendingHistoryChatId = chat.Id;
            _messageItems.Clear();
            _messagesDict.Clear();
            _fileToMsgId.Clear();
            // Показываем панель чата с индикатором загрузки, но список сообщений ещё скрыт
            StartPanel.Visibility = Visibility.Collapsed;
            MessagesPanel.Visibility = Visibility.Visible;
            CurrentChatTitle.Text = chat.Title;
            LoadingIndicator.Visibility = Visibility.Visible;
            MessagesListView.Visibility = Visibility.Collapsed;
            Log("OPEN CHAT id=" + _currentChatId + " title=" + chat.Title);
            if (_connectionReady) {
                TdJson.SendUtf8(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + _currentChatId + ",\"from_message_id\":0,\"offset\":0,\"limit\":50}");
            } else {
                // Соединение ещё не готово — запомним и отправим когда подключится
                _pendingChatHistoryId = _currentChatId;
                Log("OPEN CHAT deferred — waiting for connection");
            }
            // Второй запрос с задержкой — на случай холодного старта когда TDLib ещё грузит базу
            var chatIdCopy = _currentChatId;
            var ignored = Task.Delay(1500).ContinueWith(_ => {
                var ignored2 = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    if (_currentChatId == chatIdCopy) // пользователь всё ещё в этом чате
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + chatIdCopy + ",\"from_message_id\":0,\"offset\":0,\"limit\":50}");
                });
            });
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

        private async void MessagesListView_ItemClick(object sender, ItemClickEventArgs e) {
            var item = e.ClickedItem as MessageItem;
            if (item == null || !item.IsVideo) return;
            if (string.IsNullOrEmpty(item.FilePath)) {
                Log("VIDEO tap — downloading");
                foreach (var kv in _fileToMsgId)
                    if (kv.Value == item.Id) {
                        TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + kv.Key + ",\"priority\":32,\"synchronous\":false}");
                        break;
                    }
                return;
            }
            try {
                var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                await Windows.System.Launcher.LaunchFileAsync(file);
                Log("VIDEO launched: " + item.FilePath);
            } catch (Exception ex) { Log("VIDEO ERR: " + ex.Message); }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) {
            _currentChatId = 0;
            _pendingHistoryChatId = 0;
            LoadingIndicator.Visibility = Visibility.Collapsed;
            MessagesListView.Visibility = Visibility.Visible;
            MessagesPanel.Visibility = Visibility.Collapsed;
            StartPanel.Visibility = Visibility.Visible;
        }

        private void SendPhone_Click(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(PhoneInput.Text)) return;
            PhoneButton.IsEnabled = false;
            LoginStatus.Text = "Отправка номера...";
            TdJson.SendUtf8(_client, "{\"@type\":\"setAuthenticationPhoneNumber\",\"phone_number\":\"" + PhoneInput.Text.Trim() + "\"}");
        }

        private void SendCode_Click(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(CodeInput.Text)) return;
            CodeButton.IsEnabled = false;
            LoginStatus.Text = "Проверка кода...";
            TdJson.SendUtf8(_client, "{\"@type\":\"checkAuthenticationCode\",\"code\":\"" + CodeInput.Text.Trim() + "\"}");
        }
    }
}
