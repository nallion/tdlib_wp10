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
        private Dictionary<long, JToken> _usersDict = new Dictionary<long, JToken>(); // userId → user object
        private Dictionary<long, long> _fileToChatId = new Dictionary<long, long>();
        private Dictionary<long, long> _fileToMsgId = new Dictionary<long, long>();
        private Dictionary<long, MessageItem> _messagesDict = new Dictionary<long, MessageItem>();
        private long _currentChatId = 0;
        private long _pendingHistoryChatId = 0;
        private string _dbPath = "";
        private bool _connectionReady = false;
        private bool _isAuthorized = false;
        private bool _isLoadingHistory = false;
        private StorageFolder _filesFolder = null;
        private StorageFile _logFile = null;

        public MainPage()
        {
            this.InitializeComponent();
            _client = TdJson.td_json_client_create();
            ChatListView.ItemsSource = _chatListItems;
            MessagesListView.ItemsSource = _messageItems;
            // Сбрасываем UI в начальное состояние (на случай restore после suspend)
            LoginPanel.Visibility = Visibility.Visible;
            ChatListView.Visibility = Visibility.Collapsed;
            MessagesPanel.Visibility = Visibility.Collapsed;
            StartPanel.Visibility = Visibility.Visible;
            LoadingIndicator.Visibility = Visibility.Collapsed;
            MessagesListView.Visibility = Visibility.Collapsed;
            LogoutButton.Visibility = Visibility.Collapsed;
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
                                type != "updateMessageInteractionInfo" &&
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
                        LogoutButton.Visibility = Visibility.Visible;
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"offset_order\":\"9223372036854775807\",\"offset_chat_id\":0,\"limit\":1000}");
                    }
                    if (s == "authorizationStateLoggingOut" || s == "authorizationStateClosed") {
                        _isAuthorized = false;
                        _chatListItems.Clear();
                        _chatsDict.Clear();
                        ChatListView.Visibility = Visibility.Collapsed;
                        LogoutButton.Visibility = Visibility.Collapsed;
                        LoginPanel.Visibility = Visibility.Visible;
                        LoginStatus.Text = "Введите номер телефона";
                        PhoneInput.Text = "";
                        PhoneInput.IsEnabled = true;
                        PhoneButton.IsEnabled = true;
                        CodeInput.Visibility = Visibility.Collapsed;
                        CodeButton.Visibility = Visibility.Collapsed;
                        LoginStatus.Text = "";
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
                        LogoutButton.Visibility = Visibility.Visible;
                        Log("After switch: LoginPanel=" + LoginPanel.Visibility + " ChatListView=" + ChatListView.Visibility);
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"offset_order\":\"9223372036854775807\",\"offset_chat_id\":0,\"limit\":1000}");
                        Log("getChats sent");
                    }
                    if (!_chatsDict.ContainsKey(chatId))
                        _chatsDict[chatId] = new ChatItem { Id = chatId, Title = c["title"]?.ToString() };
                    var chatItem = _chatsDict[chatId];
                    // Заполняем последнее сообщение
                    var lastMsg = c["last_message"];
                    if (lastMsg != null) FillChatLastMessage(chatItem, lastMsg, c);
                    // Непрочитанные
                    chatItem.UnreadCount = c["unread_count"]?.ToObject<int>() ?? 0;
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
                        bool isCompleted = fileObj["local"]?["is_downloading_completed"]?.ToObject<bool>() ?? false;
                        long downloaded = fileObj["local"]?["downloaded_size"]?.ToObject<long>() ?? 0;
                        long total = fileObj["size"]?.ToObject<long>() ?? 0;
                        Log("FILE id=" + fid + " path=" + fpath);
                        if (fid != 0) {
                            if (_fileToChatId.ContainsKey(fid) && !string.IsNullOrEmpty(fpath))
                                { var t = UpdateAvatar(_fileToChatId[fid], fpath); }
                            if (_fileToMsgId.ContainsKey(fid)) {
                                long mid = _fileToMsgId[fid];
                                if (!string.IsNullOrEmpty(fpath))
                                    { var t = UpdateMessagePhoto(mid, fpath); }
                                if (_messagesDict.ContainsKey(mid)) {
                                    var msgItem = _messagesDict[mid];
                                    if (msgItem.IsVideo && !string.IsNullOrEmpty(fpath))
                                        msgItem.FilePath = fpath;
                                    if (msgItem.IsDocument) {
                                        if (isCompleted && !string.IsNullOrEmpty(fpath)) {
                                            msgItem.FilePath = fpath;
                                            msgItem.IsDownloaded = true;
                                            msgItem.DownloadStatus = "📂 Открыть";
                                        } else if (total > 0) {
                                            int pct = (int)(downloaded * 100 / total);
                                            msgItem.DownloadStatus = "⏳ " + pct + "%";
                                        }
                                    }
                                }
                            }
                        }
                    }
                    break;

                case "updateNewMessage":
                    var newMsg = update["message"];
                    long newMsgChatId = newMsg?["chat_id"]?.ToObject<long>() ?? 0;
                    Log("updateNewMessage chat=" + newMsgChatId + " current=" + _currentChatId);
                    // Игнорируем если история ещё не загружена (LoadingIndicator видим)
                    if (newMsgChatId == _currentChatId && newMsg != null && !_isLoadingHistory) {
                        var newItem = ParseMessage(newMsg);
                        if (newItem != null) {
                            _messageItems.Add(newItem);
                            MessagesListView.UpdateLayout();
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

                case "updateUser":
                    var user = update["user"];
                    long uid = user?["id"]?.ToObject<long>() ?? 0;
                    if (uid != 0) {
                        _usersDict[uid] = user;
                        if (_chatsDict.ContainsKey(uid)) {
                            string uStatus = user["status"]?["@type"]?.ToString();
                            _chatsDict[uid].IsOnline = uStatus == "userStatusOnline";
                        }
                        // Обновляем шапку если открыт чат с этим пользователем
                        if (uid == _currentChatId)
                            UpdateChatStatus(user["status"]);
                    }
                    break;

                case "updateUserStatus":
                    long userId = update["user_id"]?.ToObject<long>() ?? 0;
                    string statusType = update["status"]?["@type"]?.ToString();
                    bool isOnline = statusType == "userStatusOnline";
                    if (_chatsDict.ContainsKey(userId))
                        _chatsDict[userId].IsOnline = isOnline;
                    // Обновляем шапку если открыт чат с этим пользователем
                    if (userId == _currentChatId)
                        UpdateChatStatus(update["status"]);
                    break;

                case "updateChatLastMessage":
                    long ulcId = update["chat_id"]?.ToObject<long>() ?? 0;
                    var ulcMsg = update["last_message"];
                    if (ulcId != 0 && ulcMsg != null && _chatsDict.ContainsKey(ulcId))
                        FillChatLastMessage(_chatsDict[ulcId], ulcMsg, update);
                    break;

                case "updateChatReadInbox":
                    long ucriId = update["chat_id"]?.ToObject<long>() ?? 0;
                    if (ucriId != 0 && _chatsDict.ContainsKey(ucriId))
                        _chatsDict[ucriId].UnreadCount = update["unread_count"]?.ToObject<int>() ?? 0;
                    break;

                case "updateMessageInteractionInfo":
                    long umiChatId = update["chat_id"]?.ToObject<long>() ?? 0;
                    long umiMsgId = update["message_id"]?.ToObject<long>() ?? 0;
                    if (umiChatId == _currentChatId && _messagesDict.ContainsKey(umiMsgId)) {
                        var reacts = update["interaction_info"]?["reactions"]?["reactions"] as JArray;
                        _messagesDict[umiMsgId].Reactions = reacts != null && reacts.Count > 0
                            ? BuildReactionsString(reacts) : "";
                    }
                    break;

                case "updateChatReadOutbox":
                    long ucrId = update["chat_id"]?.ToObject<long>() ?? 0;
                    if (ucrId != 0 && _chatsDict.ContainsKey(ucrId))
                        _chatsDict[ucrId].IsRead = true;
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
                    ChatCountText.Text = _chatListItems.Count.ToString();
                    Log("chats after fill _chatListItems=" + _chatListItems.Count);
                    break;

                case "messages":
                    long expectedChat = _pendingHistoryChatId;
                    var msgs = update["messages"] as JArray;
                    int totalCount = update["total_count"]?.ToObject<int>() ?? 0;
                    Log("messages expected=" + expectedChat + " current=" + _currentChatId + " count=" + msgs?.Count + " total=" + totalCount);
                    if (expectedChat != _currentChatId) { Log("SKIP — user switched chat"); break; }
                    int gotCount = msgs?.Count ?? 0;
                    // Retry только если total_count обещает больше чем пришло — иначе сообщений реально столько
                    if (gotCount < 10 && totalCount > gotCount) {
                        Log("messages too few (" + gotCount + "/" + totalCount + ") — retrying after delay");
                        var retryChat = _currentChatId;
                        Task.Delay(800).ContinueWith(_ =>
                            Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                                if (_currentChatId == retryChat)
                                    TdJson.SendUtf8(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + retryChat + ",\"from_message_id\":0,\"offset\":0,\"limit\":50}");
                            }));
                        break;
                    }
                    _messageItems.Clear();
                    long lastMsgId = 0;
                    for (int i = msgs.Count - 1; i >= 0; i--) {
                        var item = ParseMessage(msgs[i]);
                        if (item != null) _messageItems.Add(item);
                    }
                    // id последнего (самого нового) сообщения — первый элемент массива TDLib
                    lastMsgId = msgs[0]?["id"]?.ToObject<long>() ?? 0;
                    Log("rendered " + _messageItems.Count + " messages");
                    _isLoadingHistory = false;
                    LoadingIndicator.Visibility = Visibility.Collapsed;
                    MessagesListView.Visibility = Visibility.Visible;
                    if (_messageItems.Count > 0) {
                        MessagesListView.UpdateLayout();
                        MessagesListView.ScrollIntoView(_messageItems[_messageItems.Count - 1]);
                    }
                    // Помечаем сообщения как прочитанные — TDLib пришлёт updateChatReadInbox с unread_count=0
                    if (lastMsgId != 0)
                        TdJson.SendUtf8(_client, "{\"@type\":\"viewMessages\",\"chat_id\":" + expectedChat + ",\"message_ids\":[" + lastMsgId + "],\"force_read\":true}");
                    break;
            }
        }

        private ScrollViewer FindScrollViewer(DependencyObject element) {
            if (element is ScrollViewer sv) return sv;
            int count = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++) {
                var result = FindScrollViewer(Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i));
                if (result != null) return result;
            }
            return null;
        }

        private void UpdateChatStatus(JToken status) {
            if (status == null) { CurrentChatStatus.Text = ""; return; }
            string type = status["@type"]?.ToString();
            string text = "";
            switch (type) {
                case "userStatusOnline":
                    text = "в сети";
                    CurrentChatStatus.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.LightGreen);
                    break;
                case "userStatusOffline":
                    long wasOnline = status["was_online"]?.ToObject<long>() ?? 0;
                    text = wasOnline > 0 ? "был(а) " + FormatLastSeen(wasOnline) : "не в сети";
                    CurrentChatStatus.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 204, 232, 255));
                    break;
                case "userStatusRecently":
                    text = "был(а) недавно";
                    CurrentChatStatus.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 204, 232, 255));
                    break;
                case "userStatusLastWeek":
                    text = "был(а) на этой неделе";
                    CurrentChatStatus.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 204, 232, 255));
                    break;
                case "userStatusLastMonth":
                    text = "был(а) в этом месяце";
                    CurrentChatStatus.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 204, 232, 255));
                    break;
            }
            CurrentChatStatus.Text = text;
        }

        private string FormatLastSeen(long unixTime) {
            var dt = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;
            var now = DateTime.Now;
            var diff = now - dt;
            if (diff.TotalMinutes < 1) return "только что";
            if (diff.TotalMinutes < 60) return (int)diff.TotalMinutes + " мин. назад";
            if (diff.TotalHours < 24 && dt.Day == now.Day) return "сегодня в " + dt.ToString("HH:mm");
            if ((now - dt).TotalDays < 2 && dt.Day == now.AddDays(-1).Day) return "вчера в " + dt.ToString("HH:mm");
            return dt.ToString("d MMM в HH:mm");
        }

        private string FormatFileSize(long bytes) {
            if (bytes <= 0) return "";
            if (bytes < 1024) return bytes + " Б";
            if (bytes < 1024 * 1024) return (bytes / 1024) + " КБ";
            if (bytes < 1024 * 1024 * 1024) return (bytes / (1024 * 1024)) + " МБ";
            return (bytes / (1024 * 1024 * 1024)) + " ГБ";
        }

        private void FillChatLastMessage(ChatItem item, JToken msg, JToken chatOrUpdate) {
            try {
                var content = msg["content"];
                string mtype = content?["@type"]?.ToString() ?? "";
                string text = mtype == "messageText"
                    ? content["text"]?["text"]?.ToString() ?? ""
                    : mtype == "messagePhoto" ? "📷 Фото"
                    : mtype == "messageVideo" ? "🎥 Видео"
                    : mtype == "messageVoiceNote" ? "🎤 Голосовое"
                    : mtype == "messageSticker" ? "😊 Стикер"
                    : mtype == "messageDocument" ? "📄 Документ"
                    : "[" + mtype.Replace("message", "") + "]";
                item.LastMessage = text;
                long date = msg["date"]?.ToObject<long>() ?? 0;
                if (date > 0)
                    item.LastMessageTime = DateTimeOffset.FromUnixTimeSeconds(date).LocalDateTime.ToString("HH:mm");
                item.IsOutgoing = msg["is_outgoing"]?.ToObject<bool>() ?? false;
                // Статус: прочитано если last_read_outbox_message_id >= id сообщения
                long msgId = msg["id"]?.ToObject<long>() ?? 0;
                long readOutbox = chatOrUpdate["last_read_outbox_message_id"]?.ToObject<long>() ?? 0;
                item.IsRead = item.IsOutgoing && readOutbox >= msgId;
            } catch { }
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

                // Реакции
                var reactions = msg["interaction_info"]?["reactions"]?["reactions"] as JArray;
                if (reactions != null && reactions.Count > 0)
                    item.Reactions = BuildReactionsString(reactions);

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
                } else if (type == "messageDocument") {
                    var doc = content["document"];
                    var docFile = doc?["document"] as JObject;
                    string docName = doc?["file_name"]?.ToString() ?? "Файл";
                    long docSize = docFile?["size"]?.ToObject<long>() ?? 0;
                    item.IsDocument = true;
                    item.DocumentName = docName;
                    item.DocumentSize = FormatFileSize(docSize);
                    if (docFile != null) {
                        long dfid = (long)docFile["id"];
                        _fileToMsgId[dfid] = msgId;
                        _messagesDict[msgId] = item;
                        string dPath = docFile["local"]?["path"]?.ToString();
                        Log("DOC msg=" + msgId + " file_id=" + dfid + " name=" + docName + " path=" + dPath);
                        if (!string.IsNullOrEmpty(dPath)) {
                            item.FilePath = dPath;
                            item.IsDownloaded = true;
                            item.DownloadStatus = "📂 Открыть";
                        }
                    }
                }
                if (string.IsNullOrEmpty(item.Text) && type != "messagePhoto" && type != "messageVideo" && type != "messageDocument")
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
            // Показываем статус если это личный чат
            if (_usersDict.ContainsKey(_currentChatId))
                UpdateChatStatus(_usersDict[_currentChatId]["status"]);
            else
                CurrentChatStatus.Text = "";
            _isLoadingHistory = true;
            LoadingIndicator.Visibility = Visibility.Visible;
            MessagesListView.Visibility = Visibility.Collapsed;
            Log("OPEN CHAT id=" + _currentChatId + " title=" + chat.Title);
            // openChat запускает синхронизацию истории с сервером
            TdJson.SendUtf8(_client, "{\"@type\":\"openChat\",\"chat_id\":" + _currentChatId + "}");
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

        private async void DocumentButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e) {
            var btn = sender as Windows.UI.Xaml.Controls.Button;
            if (btn?.Tag == null) return;
            long msgId = (long)btn.Tag;
            if (!_messagesDict.ContainsKey(msgId)) return;
            var item = _messagesDict[msgId];
            if (item.IsDownloaded && !string.IsNullOrEmpty(item.FilePath)) {
                try {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.FilePath);
                    await Windows.System.Launcher.LaunchFileAsync(file);
                } catch (Exception ex) { Log("DOC open ERR: " + ex.Message); }
            } else {
                // Запускаем скачивание — ищем file_id по msgId
                foreach (var kv in _fileToMsgId) {
                    if (kv.Value == msgId) {
                        item.DownloadStatus = "⏳ Загрузка...";
                        TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + kv.Key + ",\"priority\":10,\"synchronous\":false}");
                        break;
                    }
                }
            }
        }

        private string BuildReactionsString(JArray reactions) {
            var parts = new System.Text.StringBuilder();
            foreach (var r in reactions) {
                string emoji = r["type"]?["emoji"]?.ToString() ?? "👍";
                int count = r["total_count"]?.ToObject<int>() ?? 0;
                if (count > 0) {
                    if (parts.Length > 0) parts.Append("  ");
                    parts.Append(emoji);
                    if (count > 1) parts.Append(" " + count);
                }
            }
            return parts.ToString();
        }

        private async void AttachFile_Click(object sender, RoutedEventArgs e) {
            if (_currentChatId == 0) return;
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            Log("ATTACH file=" + file.Path + " name=" + file.Name);
            // Копируем файл в папку приложения чтобы TDLib мог его прочитать
            var copy = await file.CopyAsync(_filesFolder, file.Name, Windows.Storage.NameCollisionOption.ReplaceExisting);
            string path = copy.Path;
            // Отправляем как документ
            var req = new Newtonsoft.Json.Linq.JObject {
                ["@type"] = "sendMessage",
                ["chat_id"] = _currentChatId,
                ["input_message_content"] = new Newtonsoft.Json.Linq.JObject {
                    ["@type"] = "inputMessageDocument",
                    ["document"] = new Newtonsoft.Json.Linq.JObject {
                        ["@type"] = "inputFileLocal",
                        ["path"] = path
                    },
                    ["caption"] = new Newtonsoft.Json.Linq.JObject {
                        ["@type"] = "formattedText",
                        ["text"] = ""
                    }
                }
            };
            TdJson.SendUtf8(_client, req.ToString(Newtonsoft.Json.Formatting.None));
            Log("SEND DOC path=" + path);
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e) {
            TdJson.SendUtf8(_client, "{\"@type\":\"logOut\"}");
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) {
            if (_currentChatId != 0)
                TdJson.SendUtf8(_client, "{\"@type\":\"closeChat\",\"chat_id\":" + _currentChatId + "}");
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
