using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
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
        // replyMsgId → MessageItem которому нужно заполнить ReplyToText
        private Dictionary<long, MessageItem> _replyRequests = new Dictionary<long, MessageItem>();
        private long _currentChatId = 0;
        private bool _currentChatIsGroup = false;
        private Windows.UI.Xaml.DispatcherTimer _statusTimer;
        private Windows.UI.Xaml.DispatcherTimer _audioPositionTimer;
        private bool _audioSliderDragging = false;
        private long _pendingHistoryChatId = 0;
        private int _historyRetryCount = 0;
        private long _currentChatOutboxReadId = 0;
        private bool _loadingChats = false;
        private Queue<long> _pendingChatIds = new Queue<long>();
        private string _dbPath = "";
        private bool _connectionReady = false;
        private bool _isAuthorized = false;
        private bool _isLoadingHistory = false;
        private bool _isRecording = false;
        private Windows.Media.Capture.MediaCapture _mediaCapture = null;
        private Windows.Storage.StorageFile _recordingFile = null;
        private Windows.Media.Playback.MediaPlayer _currentAudioPlayer = null;
        private long _currentAudioMsgId = 0;
        private Windows.Media.Core.MediaSource _currentAudioSource = null;
        private TimeSpan _currentAudioPosition = TimeSpan.Zero;
        private string _currentAudioFilePath = null;
        private Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionSession _mediaSession = null;
        private long _pendingDeleteChatId = 0;
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
            // Таймер обновления статуса "был(а) N мин. назад"
            _statusTimer = new Windows.UI.Xaml.DispatcherTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(60);
            _statusTimer.Tick += (s, e) => {
                if (_currentChatId != 0 && _usersDict.ContainsKey(_currentChatId))
                    UpdateChatStatus(_usersDict[_currentChatId]["status"]);
            };
            _statusTimer.Start();
            // Таймер обновления позиции аудио (каждые 500мс)
            _audioPositionTimer = new Windows.UI.Xaml.DispatcherTimer();
            _audioPositionTimer.Interval = TimeSpan.FromMilliseconds(500);
            _audioPositionTimer.Tick += (s, e) => {
                if (_currentAudioPlayer == null || _audioSliderDragging) return;
                var session = _currentAudioPlayer.PlaybackSession;
                if (session.NaturalDuration.TotalSeconds > 0 && _messagesDict.ContainsKey(_currentAudioMsgId)) {
                    var item = _messagesDict[_currentAudioMsgId];
                    item.AudioDurationSeconds = session.NaturalDuration.TotalSeconds;
                    item.AudioPosition = session.Position.TotalSeconds;
                    var pos = session.Position;
                    item.AudioPositionText = $"{(int)pos.TotalMinutes}:{pos.Seconds:D2}";
                    _currentAudioPosition = session.Position; // сохраняем для восстановления после resume
                }
            };
            _audioPositionTimer.Start();
            // Системная кнопка "назад"
            var sysNav = Windows.UI.Core.SystemNavigationManager.GetForCurrentView();
            sysNav.BackRequested += (s, e) => {
                if (_currentChatId != 0) {
                    BackButton_Click(null, null);
                    e.Handled = true;
                }
            };
            InitAsync();
            // Логируем lifecycle приложения для диагностики фонового аудио
            Application.Current.EnteredBackground += (s, e) => Log("APP EnteredBackground, player=" + (_currentAudioPlayer == null ? "null" : _currentAudioPlayer.PlaybackSession.PlaybackState.ToString()));
            Application.Current.LeavingBackground += (s, e) => Log("APP LeavingBackground");
            Application.Current.Suspending += (s, e) => {
                Log("APP Suspending, player=" + (_currentAudioPlayer == null ? "null" : _currentAudioPlayer.PlaybackSession.PlaybackState.ToString()));
                // Сохраняем позицию на случай если плеер упадёт после resume
                if (_currentAudioPlayer != null)
                    _currentAudioPosition = _currentAudioPlayer.PlaybackSession.Position;
            };
            Application.Current.Resuming += async (s, e) => {
                Log("APP Resuming, player=" + (_currentAudioPlayer == null ? "null" : _currentAudioPlayer.PlaybackSession.PlaybackState.ToString()));
                // Если плеер упал во время suspend — восстанавливаем
                await System.Threading.Tasks.Task.Delay(1500); // ждём пока AUDIO FAILED придёт
                Log("APP Resuming check, player=" + (_currentAudioPlayer == null ? "null" : _currentAudioPlayer.PlaybackSession.PlaybackState.ToString()));
                if (_currentAudioPlayer == null && _currentAudioFilePath != null && _messagesDict.ContainsKey(_currentAudioMsgId)) {
                    Log("APP Resuming: восстанавливаем плеер pos=" + _currentAudioPosition.TotalSeconds);
                    var savedMsgId = _currentAudioMsgId;
                    var savedPos = _currentAudioPosition;
                    var savedPath = _currentAudioFilePath;
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () => {
                        try {
                            var item = _messagesDict[savedMsgId];
                            var player = new Windows.Media.Playback.MediaPlayer();
                            player.AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Media;
                            var source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(savedPath));
                            _currentAudioSource = source;
                            player.Source = source;
                            _currentAudioPlayer = player;
                            _currentAudioMsgId = savedMsgId;
                            SetupPlayer(player, item, savedPos);
                            player.Play();
                            Log("APP Resuming: плеер восстановлен");
                        } catch (Exception ex) {
                            Log("APP Resuming: ошибка восстановления: " + ex.Message);
                            _currentAudioPlayer = null;
                            _currentAudioSource = null;
                            _currentAudioFilePath = null;
                        }
                    });
                }
            };
        }

        private async System.Threading.Tasks.Task RequestMediaSessionAsync() {
            _mediaSession?.Dispose();
            _mediaSession = null;
            var session = new Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionSession();
            session.Reason = Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionReason.Unspecified;
            session.Description = "Unogram audio";
            session.Revoked += (s, e) => Log("MEDIA SESSION revoked: " + e.Reason);
            var result = await session.RequestExtensionAsync();
            Log("MEDIA SESSION result: " + result);
            if (result == Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionResult.Allowed)
                _mediaSession = session;
            else
                session.Dispose();
        }
        private void ReleaseMediaSession() {
            _mediaSession?.Dispose();
            _mediaSession = null;
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
                    if (s == "authorizationStateWaitPassword") {
                        LoginStatus.Text = "Введите пароль 2FA";
                        CodeInput.Visibility = Visibility.Collapsed;
                        CodeButton.Visibility = Visibility.Collapsed;
                        PasswordInput.Visibility = Visibility.Visible;
                        PasswordButton.Visibility = Visibility.Visible;
                        PasswordInput.Focus(FocusState.Programmatic);
                    }
                    if (s == "authorizationStateReady") {
                        _isAuthorized = true;
                        LoginPanel.Visibility = Visibility.Collapsed;
                        ChatListView.Visibility = Visibility.Visible;
                        LogoutButton.Visibility = Visibility.Visible;
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"chat_list\":{\"@type\":\"chatListMain\"},\"limit\":1000}");
                        _loadingChats = true; Log("getChats sent from authReady");
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
                        PasswordInput.Password = "";
                        PasswordInput.Visibility = Visibility.Collapsed;
                        PasswordButton.Visibility = Visibility.Collapsed;
                        LoginStatus.Text = "";
                    }
                    break;

                case "error":
                    string errMsg = update["message"]?.ToString();
                    Log("ERROR: " + errMsg);
                    LoginStatus.Text = "Ошибка: " + errMsg;
                    PhoneButton.IsEnabled = true;
                    CodeButton.IsEnabled = true;
                    // loadChats вернёт error когда все чаты загружены
                    if (_loadingChats && (errMsg?.Contains("CHAT_LIST_EMPTY") ?? false)) {
                        _loadingChats = false;
                        Log("loadChats done — all chats loaded, total=" + _chatListItems.Count);
                    }
                    break;

                case "updateChatAddedToList":
                    // Игнорируем — используем getChats для правильного порядка
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
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"chat_list\":{\"@type\":\"chatListMain\"},\"limit\":1000}");
                        _loadingChats = true; Log("getChats sent from updateNewChat");
                    }
                    if (!_chatsDict.ContainsKey(chatId)) {
                        bool isChannel = c["type"]?["@type"]?.ToString() == "chatTypeSupergroup"
                            && (c["type"]?["is_channel"]?.ToObject<bool>() ?? false);
                        _chatsDict[chatId] = new ChatItem { Id = chatId, Title = c["title"]?.ToString(), OutboxReadId = c["last_read_outbox_message_id"]?.ToObject<long>() ?? 0, IsChannel = isChannel };
                    }
                    var chatItem = _chatsDict[chatId];
                    // Заполняем последнее сообщение
                    var lastMsg = c["last_message"];
                    if (lastMsg != null) FillChatLastMessage(chatItem, lastMsg, c);
                    // Непрочитанные
                    chatItem.UnreadCount = c["unread_count"]?.ToObject<int>() ?? 0;
                    // Если чат пришёл через LoadNextChat (из очереди) — добавляем в список
                    if (_pendingChatIds.Count >= 0 && !_chatListItems.Contains(chatItem) && _loadingChats) {
                        _chatListItems.Add(chatItem);
                        ChatCountText.Text = _chatListItems.Count.ToString();
                        LoadNextChat(); // грузим следующий
                    }
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
                                // Передаём в UpdateMessagePhoto только изображения, не .mp4
                                bool isImg = !string.IsNullOrEmpty(fpath) &&
                                    (fpath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                     fpath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                     fpath.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                                if (isImg)
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
                                    if (msgItem.IsAudio) {
                                        if (isCompleted && !string.IsNullOrEmpty(fpath)) {
                                            msgItem.FilePath = fpath;
                                            msgItem.AudioPlayStatus = "▶";
                                            Log("AUDIO file ready: " + fpath);
                                        } else if (total > 0) {
                                            int pct = (int)(downloaded * 100 / total);
                                            msgItem.AudioPlayStatus = "⏳" + pct + "%";
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
                        ConnectionStatusText.Text = "";
                    } else {
                        _connectionReady = false;
                        string connText = connState == "connectionStateConnecting" ? "· подключение..."
                            : connState == "connectionStateUpdating" ? "· обновление..."
                            : connState == "connectionStateWaitingForNetwork" ? "· нет сети"
                            : "...";
                        ConnectionStatusText.Text = connText;
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
                    // expires — серверное время, используем для калибровки часов
                    if (isOnline) {
                        long expires = update["status"]?["expires"]?.ToObject<long>() ?? 0;
                        if (expires > 0) UpdateServerTimeOffset(expires - 30); // expires = now+30s на сервере
                    }
                    if (_chatsDict.ContainsKey(userId))
                        _chatsDict[userId].IsOnline = isOnline;
                    // Синхронизируем статус в _usersDict чтобы при открытии чата был актуальный
                    if (_usersDict.ContainsKey(userId) && update["status"] != null)
                        _usersDict[userId]["status"] = update["status"];
                    if (userId == _currentChatId) {
                        long wo = update["status"]?["was_online"]?.ToObject<long>() ?? 0;
                        long nowUnix = LocalUnixNow();
                        Log("STATUS user=" + userId + " type=" + statusType + " was_online=" + wo + " now_unix=" + nowUnix + " diff=" + (nowUnix - wo) + "s");
                        UpdateChatStatus(update["status"]);
                    }
                    break;

                case "updateChatLastMessage":
                    long ulcId = update["chat_id"]?.ToObject<long>() ?? 0;
                    var ulcMsg = update["last_message"];
                    if (ulcId != 0 && ulcMsg != null && _chatsDict.ContainsKey(ulcId)) {
                        string ulcType = ulcMsg["content"]?["@type"]?.ToString() ?? "null";
                        Log("updateChatLastMessage chat=" + ulcId + " content=" + ulcType);
                        FillChatLastMessage(_chatsDict[ulcId], ulcMsg, update);
                        MoveChatToTop(ulcId);
                    }
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
                    long ucrMsgId = update["last_read_outbox_message_id"]?.ToObject<long>() ?? 0;
                    if (ucrId != 0 && _chatsDict.ContainsKey(ucrId)) {
                        _chatsDict[ucrId].IsRead = true;
                        _chatsDict[ucrId].OutboxReadId = ucrMsgId;
                    }
                    // Обновляем галочки в открытом чате
                    if (ucrId == _currentChatId) {
                        _currentChatOutboxReadId = ucrMsgId;
                        foreach (var m in _messageItems)
                            if (m.IsOutgoing && m.Id <= ucrMsgId)
                                m.IsRead = true;
                    }
                    break;

                case "ok":
                    break;

                case "chat":
                    // Ответ на getChat — обрабатывается как updateNewChat через общий путь
                    // TDLib также шлёт updateNewChat, поэтому просто грузим следующий
                    break;

                case "message":
                    // Ответ на getMessage — заполняем ReplyToText если ждали
                    long fetchedMsgId = update["id"]?.ToObject<long>() ?? 0;
                    if (fetchedMsgId != 0 && _replyRequests.ContainsKey(fetchedMsgId)) {
                        var waitingItem = _replyRequests[fetchedMsgId];
                        _replyRequests.Remove(fetchedMsgId);
                        var fc = update["content"];
                        string fType = fc?["@type"]?.ToString() ?? "";
                        string fText = fType == "messageText"
                            ? fc["text"]?["text"]?.ToString()
                            : fType == "messagePhoto" ? "📷 Фото"
                            : fType == "messageVideo" ? "🎥 Видео"
                            : fType == "messageDocument" ? "📄 Файл"
                            : fType == "messageAudio" ? "🎵 Аудио"
                            : fType == "messageVoiceNote" ? "🎤 Голосовое"
                            : "Сообщение";
                        waitingItem.ReplyToText = string.IsNullOrEmpty(fText) ? "Сообщение" : fText;
                    }
                    break;

                case "chats":
                    var chatIds = update["chat_ids"] as JArray;
                    Log("chats received count=" + chatIds?.Count);
                    if (chatIds != null) {
                        _pendingChatIds.Clear();
                        foreach (var cId in chatIds)
                            _pendingChatIds.Enqueue((long)cId);
                        LoadNextChat();
                    }
                    break;

                case "messages":
                    long expectedChat = _pendingHistoryChatId;
                    var msgs = update["messages"] as JArray;
                    int totalCount = update["total_count"]?.ToObject<int>() ?? 0;
                    Log("messages expected=" + expectedChat + " current=" + _currentChatId + " count=" + msgs?.Count + " total=" + totalCount);
                    if (expectedChat != _currentChatId) { Log("SKIP — user switched chat"); break; }
                    int gotCount = msgs?.Count ?? 0;
                    if (gotCount < 2 && _historyRetryCount < 2) {
                        _historyRetryCount++;
                        Log("messages too few (" + gotCount + ") retry #" + _historyRetryCount);
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

        private void LoadNextChat() {
            if (_pendingChatIds.Count == 0) {
                if (_loadingChats) {
                    _loadingChats = false;
                    Log("All chats loaded, total=" + _chatListItems.Count);
                }
                return;
            }
            long nextId = _pendingChatIds.Dequeue();
            Task.Delay(100).ContinueWith(_ =>
                Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    if (_chatsDict.ContainsKey(nextId)) {
                        var existing = _chatsDict[nextId];
                        if (!_chatListItems.Contains(existing)) {
                            _chatListItems.Add(existing);
                            ChatCountText.Text = _chatListItems.Count.ToString();
                        }
                    } else {
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChat\",\"chat_id\":" + nextId + "}");
                    }
                    LoadNextChat();
                }));
        }

        private void MoveChatToTop(long chatId) {
            var item = _chatListItems.FirstOrDefault(c => c.Id == chatId);
            if (item == null || _chatListItems.IndexOf(item) == 0) return;
            _chatListItems.Remove(item);
            _chatListItems.Insert(0, item);
        }

        private long _serverTimeOffset = 0;
        private bool _serverTimeOffsetSet = false;

        private void UpdateServerTimeOffset(long serverUnix) {
            if (_serverTimeOffsetSet) return; // устанавливаем только один раз
            long localUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _serverTimeOffset = serverUnix - localUnix;
            _serverTimeOffsetSet = true;
            Log("CLOCK OFFSET: " + _serverTimeOffset + "s (server ahead of phone)");
        }

        private long LocalUnixNow() {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _serverTimeOffset;
        }

        private string FormatLastSeen(long unixTime) {
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long diffSec = nowUnix - unixTime;
            if (diffSec < 0) diffSec = 0;
            if (diffSec < 60) return "только что";
            if (diffSec < 3600) return (diffSec / 60) + " мин. назад";
            var dtLocal = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;
            var nowLocal = DateTimeOffset.UtcNow.ToLocalTime().DateTime;
            if (dtLocal.Day == nowLocal.Day) return "сегодня в " + dtLocal.ToString("HH:mm");
            if (dtLocal.Day == nowLocal.AddDays(-1).Day) return "вчера в " + dtLocal.ToString("HH:mm");
            return dtLocal.ToString("d MMM в HH:mm");
        }

        private string FormatCallDuration(int seconds) {
            if (seconds < 60) return seconds + " сек";
            int m = seconds / 60, s = seconds % 60;
            return m + ":" + s.ToString("D2");
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
                    : mtype == "messageVideo" && (content["video"]?["is_animation"]?.ToObject<bool>() ?? false) ? "🎞 GIF"
                    : mtype == "messageVideo" ? "🎥 Видео"
                    : mtype == "messageVoiceNote" ? "🎤 Голосовое"
                    : mtype == "messageSticker" ? "😊 Стикер"
                    : mtype == "messageDocument" ? "📄 Документ"
                    : mtype == "messageAnimation" ? "🎞 GIF"
                    : mtype == "messageCall" ? ((content["is_video"]?.ToObject<bool>() ?? false) ? "📹" : "📞") + " Звонок"
                    : mtype == "messageAudio" ? "🎵 Аудио"
                    : "[" + mtype.Replace("message", "") + "]";
                item.LastMessage = text;
                long date = msg["date"]?.ToObject<long>() ?? 0;
                if (date > 0)
                    item.LastMessageTime = DateTimeOffset.FromUnixTimeSeconds(date).LocalDateTime.ToString("HH:mm");
                item.IsOutgoing = msg["is_outgoing"]?.ToObject<bool>() ?? false;
                // Статус: прочитано если OutboxReadId >= id сообщения
                long msgId = msg["id"]?.ToObject<long>() ?? 0;
                // last_read_outbox_message_id есть только в начальном chat объекте, не в updateChatLastMessage
                long readOutbox = chatOrUpdate["last_read_outbox_message_id"]?.ToObject<long>() ?? -1;
                if (readOutbox >= 0) item.OutboxReadId = readOutbox;
                item.IsRead = item.IsOutgoing && item.OutboxReadId >= msgId;
            } catch { }
        }

        // Цвета для ников (по user_id % количество цветов)
        private static readonly string[] _senderColors = {
            "#E17076", "#7EC8E3", "#A695E7", "#76C99F",
            "#F2C94C", "#F78C6C", "#67D7CC", "#FF8A65"
        };

        private string GetSenderName(JToken senderId) {
            if (senderId == null) return "";
            string sType = senderId["@type"]?.ToString();
            if (sType == "messageSenderUser") {
                long uid = senderId["user_id"]?.ToObject<long>() ?? 0;
                if (_usersDict.ContainsKey(uid)) {
                    var u = _usersDict[uid];
                    string fn = u["first_name"]?.ToString() ?? "";
                    string ln = u["last_name"]?.ToString() ?? "";
                    return (fn + " " + ln).Trim();
                }
                return "User " + uid;
            }
            if (sType == "messageSenderChat") {
                long cid = senderId["chat_id"]?.ToObject<long>() ?? 0;
                if (_chatsDict.ContainsKey(cid)) return _chatsDict[cid].Title;
                return "Chat " + cid;
            }
            return "";
        }

        private string GetSenderColor(JToken senderId) {
            if (senderId == null) return _senderColors[0];
            long id = senderId["user_id"]?.ToObject<long>()
                   ?? senderId["chat_id"]?.ToObject<long>() ?? 0;
            return _senderColors[Math.Abs((int)(id % _senderColors.Length))];
        }

        private MessageItem ParseMessage(JToken msg) {
            try {
                long msgId = (long)msg["id"];
                var content = msg["content"];
                string type = content["@type"]?.ToString();
                string txt = type == "messageText"
                    ? content["text"]?["text"]?.ToString() ?? ""
                    : content["caption"]?["text"]?.ToString() ?? "";

                bool outgoing = (bool)msg["is_outgoing"];
                var senderId = msg["sender_id"];
                var item = new MessageItem {
                    Id = msgId, Text = txt,
                    Date = DateTimeOffset.FromUnixTimeSeconds((long)msg["date"]).LocalDateTime.ToString("HH:mm"),
                    Alignment = outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Background = outgoing ? "#0088cc" : "#333333",
                    IsOutgoing = outgoing,
                    IsRead = outgoing && (msg["id"]?.ToObject<long>() ?? 0) <= _currentChatOutboxReadId,
                    SenderName = outgoing ? "" : (_currentChatIsGroup ? GetSenderName(senderId) : ""),
                    SenderColor = GetSenderColor(senderId)
                };

                var replyTo = msg["reply_to"];
                if (replyTo != null && replyTo["@type"]?.ToString() == "messageReplyToMessage") {
                    // Автор цитаты
                    var replyOrigin = replyTo["origin"];
                    if (replyOrigin != null) {
                        string oType = replyOrigin["@type"]?.ToString();
                        if (oType == "messageOriginUser") {
                            long oUid = replyOrigin["sender_user_id"]?.ToObject<long>() ?? 0;
                            if (_usersDict.ContainsKey(oUid)) {
                                var u = _usersDict[oUid];
                                item.ReplyAuthor = (u["first_name"]?.ToString() + " " + u["last_name"]?.ToString()).Trim();
                            }
                        } else if (oType == "messageOriginChat" || oType == "messageOriginChannel") {
                            long oCid = replyOrigin["sender_chat_id"]?.ToObject<long>() ?? 0;
                            if (_chatsDict.ContainsKey(oCid)) item.ReplyAuthor = _chatsDict[oCid].Title;
                        }
                    }
                    // Текст цитаты — сначала quote (выделенный фрагмент), потом content
                    string replyText = replyTo["quote"]?["text"]?.ToString();
                    if (string.IsNullOrEmpty(replyText)) {
                        var replyContent = replyTo["content"];
                        if (replyContent != null) {
                            string rType = replyContent["@type"]?.ToString();
                            replyText = rType == "messageText"
                                ? replyContent["text"]?["text"]?.ToString()
                                : rType == "messagePhoto" ? "📷 Фото"
                                : rType == "messageVideo" ? "🎥 Видео"
                                : rType == "messageDocument" ? "📄 Файл"
                                : rType == "messageAudio" ? "🎵 Аудио"
                                : rType == "messageVoiceNote" ? "🎤 Голосовое"
                                : null;
                        }
                    }
                    item.ReplyToText = string.IsNullOrEmpty(replyText) ? "…" : replyText;
                    // Если текст не получили — запрашиваем сообщение явно
                    if (string.IsNullOrEmpty(replyText)) {
                        long replyMsgId = replyTo["message_id"]?.ToObject<long>() ?? 0;
                        long replyChatId = replyTo["chat_id"]?.ToObject<long>() ?? 0;
                        if (replyChatId == 0) replyChatId = (long)msg["chat_id"];
                        if (replyMsgId != 0) {
                            _replyRequests[replyMsgId] = item;
                            TdJson.SendUtf8(_client, "{\"@type\":\"getMessage\",\"chat_id\":" + replyChatId + ",\"message_id\":" + replyMsgId + "}");
                        }
                    }
                }

                // Реакции
                var reactions = msg["interaction_info"]?["reactions"]?["reactions"] as JArray;
                if (reactions != null && reactions.Count > 0)
                    item.Reactions = BuildReactionsString(reactions);

                // Inline-кнопки
                var markup = msg["reply_markup"];
                if (markup != null && markup["@type"]?.ToString() == "replyMarkupInlineKeyboard") {
                    var rows = markup["rows"] as JArray;
                    if (rows != null) {
                        var buttonRows = new System.Collections.ObjectModel.ObservableCollection<InlineButtonRow>();
                        foreach (var row in rows) {
                            var btnRow = new InlineButtonRow();
                            foreach (var btn in row as JArray ?? new JArray()) {
                                string bType = btn["type"]?["@type"]?.ToString() ?? "";
                                btnRow.Buttons.Add(new InlineButton {
                                    Text = btn["text"]?.ToString() ?? "",
                                    CallbackData = bType == "inlineKeyboardButtonTypeCallback"
                                        ? btn["type"]?["data"]?.ToString() : null,
                                    Url = bType == "inlineKeyboardButtonTypeUrl"
                                        ? btn["type"]?["url"]?.ToString() : null,
                                });
                            }
                            if (btnRow.Buttons.Count > 0) buttonRows.Add(btnRow);
                        }
                        item.InlineButtons = buttonRows;
                    }
                }

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
                    bool isAnim = content["video"]?["is_animation"]?.ToObject<bool>() ?? false;
                    item.IsVideo = !isAnim;
                    if (isAnim) item.Text = "🎞 GIF"; // показываем лейбл если не будет превью
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
                        string tPath = thumb["local"]?["path"]?.ToString();
                        Log("VIDEO thumb id=" + tfid + " path=" + tPath);
                        bool isImgThumb = !string.IsNullOrEmpty(tPath) &&
                            (tPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                             tPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                             tPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                        if (isImgThumb) {
                            _fileToMsgId[tfid] = msgId;
                            _messagesDict[msgId] = item;
                            if (isAnim) item.Text = ""; // есть превью — убираем лейбл
                            var t = UpdateMessagePhoto(msgId, tPath);
                        } else {
                            _fileToMsgId[tfid] = msgId;
                            _messagesDict[msgId] = item;
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + tfid + ",\"priority\":10,\"synchronous\":false}");
                        }
                    }
                } else if (type == "messageAnimation") {
                    item.IsVideo = true;
                    var animFile = content["animation"]?["animation"] as JObject;
                    var animThumb = content["animation"]?["thumbnail"]?["file"] as JObject;
                    string animCaption = content["caption"]?["text"]?.ToString() ?? "";
                    item.Text = !string.IsNullOrEmpty(animCaption) ? animCaption : "🎞 GIF";
                    if (animFile != null) {
                        long afid = (long)animFile["id"];
                        _fileToMsgId[afid] = msgId;
                        _messagesDict[msgId] = item;
                        string aPath = animFile["local"]?["path"]?.ToString();
                        Log("ANIM file id=" + afid + " path=" + aPath);
                        if (!string.IsNullOrEmpty(aPath)) item.FilePath = aPath;
                        else TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + afid + ",\"priority\":10,\"synchronous\":false}");
                    }
                    if (animThumb != null) {
                        long tfid = (long)animThumb["id"];
                        string tPath = animThumb["local"]?["path"]?.ToString();
                        Log("ANIM thumb id=" + tfid + " path=" + tPath);
                        // Thumbnail может быть .mp4 — BitmapImage не умеет его декодировать
                        bool isImgThumb = !string.IsNullOrEmpty(tPath) &&
                            (tPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                             tPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                             tPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                        if (isImgThumb) {
                            _fileToMsgId[tfid] = msgId;
                            _messagesDict[msgId] = item;
                            item.Text = ""; // есть превью — убираем лейбл
                            var t = UpdateMessagePhoto(msgId, tPath);
                        } else if (string.IsNullOrEmpty(tPath)) {
                            // Скачиваем thumb только если он ещё не загружен — проверим формат потом
                            _fileToMsgId[tfid] = msgId;
                            _messagesDict[msgId] = item;
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + tfid + ",\"priority\":10,\"synchronous\":false}");
                        }
                        // иначе thumb.mp4 — игнорируем, пузырь покажет GIF-лейбл
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
                } else if (type == "messageAudio") {
                    var audio = content["audio"];
                    var audioFile = audio?["audio"] as JObject;
                    string title = audio?["title"]?.ToString() ?? "";
                    string performer = audio?["performer"]?.ToString() ?? "";
                    int dur = audio?["duration"]?.ToObject<int>() ?? 0;
                    item.IsAudio = true;
                    item.AudioTitle = !string.IsNullOrEmpty(performer) ? performer + " — " + title
                                    : !string.IsNullOrEmpty(title) ? title : "Голосовое сообщение";
                    item.AudioDuration = dur > 0 ? FormatCallDuration(dur) : "";
                    item.AudioPlayStatus = "▶";
                    if (audioFile != null) {
                        long afid = (long)audioFile["id"];
                        _fileToMsgId[afid] = msgId;
                        _messagesDict[msgId] = item;
                        string aPath = audioFile["local"]?["path"]?.ToString();
                        Log("AUDIO msg=" + msgId + " file_id=" + afid + " path=" + aPath);
                        if (!string.IsNullOrEmpty(aPath)) {
                            item.FilePath = aPath;
                            item.DownloadStatus = "ready";
                        } else {
                            item.AudioPlayStatus = "⏳";
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + afid + ",\"priority\":10,\"synchronous\":false}");
                        }
                    }
                }
                if (string.IsNullOrEmpty(item.Text) && type != "messagePhoto" && type != "messageVideo" && type != "messageAnimation" && type != "messageDocument" && type != "messageAudio") {
                    if (type == "messageCall") {
                        var callContent = content;
                        bool isVideo = callContent["is_video"]?.ToObject<bool>() ?? false;
                        string callEmoji = isVideo ? "📹" : "📞";
                        bool isOutgoing = (bool)msg["is_outgoing"];
                        string direction = isOutgoing ? "Исходящий" : "Входящий";
                        int duration = callContent["duration"]?.ToObject<int>() ?? 0;
                        string discardReason = callContent["discard_reason"]?["@type"]?.ToString() ?? "";
                        string durationStr = duration > 0 ? " · " + FormatCallDuration(duration) : "";
                        if (discardReason == "callDiscardReasonMissed")
                            item.Text = callEmoji + " Пропущенный звонок";
                        else if (discardReason == "callDiscardReasonDeclined")
                            item.Text = callEmoji + " Отклонённый звонок";
                        else
                            item.Text = callEmoji + " " + direction + " звонок" + durationStr;
                    } else if (type == "messageAudio") {
                        string title = content["audio"]?["title"]?.ToString() ?? "";
                        string performer = content["audio"]?["performer"]?.ToString() ?? "";
                        int dur = content["audio"]?["duration"]?.ToObject<int>() ?? 0;
                        string durStr = dur > 0 ? " · " + FormatCallDuration(dur) : "";
                        string label = !string.IsNullOrEmpty(performer) ? performer + " — " + title : title;
                        item.Text = "🎵 " + (string.IsNullOrEmpty(label) ? "Аудио" : label) + durStr;
                    } else {
                        item.Text = "[" + type.Replace("message", "") + "]";
                    }
                }
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
            _currentChatIsGroup = _chatsDict.ContainsKey(chat.Id) &&
                (_chatsDict[chat.Id].IsChannel == false) &&
                (chat.Id < 0); // группы и супергруппы имеют отрицательный ID
            _pendingHistoryChatId = chat.Id;
            _historyRetryCount = 0;
            _currentChatOutboxReadId = chat.OutboxReadId;
            _messageItems.Clear();
            _messagesDict.Clear();
            _fileToMsgId.Clear();
            _replyRequests.Clear();
            // Показываем панель чата с индикатором загрузки, но список сообщений ещё скрыт
            StartPanel.Visibility = Visibility.Collapsed;
            MessagesPanel.Visibility = Visibility.Visible;
            CurrentChatTitle.Text = chat.Title;
            // Показываем статус если это личный чат
            if (_usersDict.ContainsKey(_currentChatId))
                UpdateChatStatus(_usersDict[_currentChatId]["status"]);
            else if (chat.IsChannel) {
                CurrentChatStatus.Text = "Канал";
                CurrentChatStatus.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 204, 232, 255));
            } else
                CurrentChatStatus.Text = "";
            // Скрываем поле ввода для каналов
            InputGrid.Visibility = chat.IsChannel ? Visibility.Collapsed : Visibility.Visible;
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

        private void AudioSlider_ManipulationStarted(object sender, Windows.UI.Xaml.Input.ManipulationStartedRoutedEventArgs e) {
            _audioSliderDragging = true;
        }
        private void AudioSlider_ManipulationCompleted(object sender, Windows.UI.Xaml.Input.ManipulationCompletedRoutedEventArgs e) {
            _audioSliderDragging = false;
            if (_currentAudioPlayer == null) return;
            var slider = sender as Windows.UI.Xaml.Controls.Slider;
            if (slider == null) return;
            _currentAudioPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(slider.Value);
        }

        private async void AudioButton_Click(object sender, RoutedEventArgs e) {
            var btn = sender as Button;
            long msgId = (long)btn.Tag;
            if (!_messagesDict.ContainsKey(msgId)) return;
            var item = _messagesDict[msgId];
            Log("AUDIO PLAY msgId=" + msgId + " path=" + item.FilePath + " status=" + item.AudioPlayStatus);
            // Если уже играет — стоп
            if (_currentAudioMsgId == msgId && _currentAudioPlayer != null) {
                var stoppingSmtc = _currentAudioPlayer.SystemMediaTransportControls;
                stoppingSmtc.PlaybackStatus = Windows.Media.MediaPlaybackStatus.Stopped;
                _currentAudioPlayer.Pause(); _currentAudioPlayer.Source = null;
                _currentAudioPlayer = null;
                _currentAudioSource = null;
                item.AudioPlayStatus = "▶";
                _currentAudioMsgId = 0;
                ReleaseMediaSession();
                return;
            }
            // Остановить предыдущий
            if (_currentAudioPlayer != null) {
                var stoppingSmtc = _currentAudioPlayer.SystemMediaTransportControls;
                stoppingSmtc.PlaybackStatus = Windows.Media.MediaPlaybackStatus.Stopped;
                _currentAudioPlayer.Pause(); _currentAudioPlayer.Source = null;
                if (_messagesDict.ContainsKey(_currentAudioMsgId))
                    _messagesDict[_currentAudioMsgId].AudioPlayStatus = "▶";
                _currentAudioPlayer = null;
                _currentAudioSource = null;
                ReleaseMediaSession();
            }
            if (string.IsNullOrEmpty(item.FilePath)) {
                Log("AUDIO no file yet — download not ready");
                return;
            }
            try {
                var player = new Windows.Media.Playback.MediaPlayer();
                player.AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Media;
                // CreateFromUri не держит файловый хэндл — не падает после suspend/resume
                var source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(item.FilePath));
                _currentAudioSource = source;
                player.Source = source;

                // CommandManager ВКЛЮЧЁН — он регистрирует плеер в системном медиапайплайне
                // и обеспечивает появление плеера на экране блокировки. Не отключать!
                SetupPlayer(player, item, TimeSpan.Zero);

                player.Play();
                Log("AUDIO Play() called, state=" + player.PlaybackSession.PlaybackState);
                AudioPlayerHost.Children.Clear();
                _currentAudioPlayer = player;
                _currentAudioMsgId = msgId;
                item.AudioPlayStatus = "⏹";
                Log("AUDIO playing: " + item.FilePath);
                _currentAudioFilePath = item.FilePath;
                _currentAudioPosition = TimeSpan.Zero;
                await RequestMediaSessionAsync();
            } catch (Exception ex) {
                Log("AUDIO PLAY ERR: " + ex.GetType().Name + " — " + ex.Message);
            }
        }

        // Настройка SMTC и обработчиков событий плеера. Вызывается и при старте, и при восстановлении после suspend.
        private void SetupPlayer(Windows.Media.Playback.MediaPlayer player, MessageItem item, TimeSpan startPosition) {
            var smtc = player.SystemMediaTransportControls;
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsStopEnabled = false;
            smtc.IsNextEnabled = false;
            smtc.IsPreviousEnabled = false;
            smtc.DisplayUpdater.Type = Windows.Media.MediaPlaybackType.Music;
            smtc.DisplayUpdater.MusicProperties.Title = item.AudioTitle ?? "";
            smtc.DisplayUpdater.Update();
            smtc.PlaybackPositionChangeRequested += (ss, ee) => {
                player.PlaybackSession.Position = ee.RequestedPlaybackPosition;
            };
            player.PlaybackSession.PositionChanged += (session, args) => {
                smtc.UpdateTimelineProperties(new Windows.Media.SystemMediaTransportControlsTimelineProperties {
                    StartTime = TimeSpan.Zero, MinSeekTime = TimeSpan.Zero,
                    Position = session.Position,
                    MaxSeekTime = session.NaturalDuration,
                    EndTime = session.NaturalDuration
                });
            };
            player.PlaybackSession.PlaybackStateChanged += (session, args) => {
                Log("AUDIO STATE: " + session.PlaybackState);
                var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    if (session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
                        item.AudioPlayStatus = "⏹";
                    else if (session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Paused)
                        item.AudioPlayStatus = "▶";
                });
            };
            player.MediaOpened += (s, ev) => {
                var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    if (startPosition > TimeSpan.Zero)
                        player.PlaybackSession.Position = startPosition;
                    var dur = player.PlaybackSession.NaturalDuration;
                    if (dur.TotalSeconds > 0) item.AudioDurationSeconds = dur.TotalSeconds;
                    Log("AUDIO OPENED ok dur=" + dur.TotalSeconds + " pos=" + startPosition.TotalSeconds);
                });
            };
            player.MediaEnded += (s, ev) => {
                var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    Log("AUDIO ENDED");
                    item.AudioPlayStatus = "▶";
                    _currentAudioPlayer = null; _currentAudioSource = null;
                    _currentAudioMsgId = 0; _currentAudioFilePath = null;
                    ReleaseMediaSession();
                });
            };
            player.MediaFailed += (s, ev) => {
                var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    Log("AUDIO FAILED: " + ev.ErrorMessage);
                    item.AudioPlayStatus = "▶";
                    _currentAudioPlayer = null; _currentAudioSource = null;
                    _currentAudioMsgId = 0;
                    // НЕ сбрасываем _currentAudioFilePath — нужен для восстановления в Resuming
                    ReleaseMediaSession();
                });
            };
        }

        private async void MicButton_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) {
            Log("MIC PRESSED — chatId=" + _currentChatId + " isRecording=" + _isRecording);
            if (_currentChatId == 0 || _isRecording) return;
            try {
                Log("MIC init MediaCapture...");
                _mediaCapture = new Windows.Media.Capture.MediaCapture();
                await _mediaCapture.InitializeAsync(new Windows.Media.Capture.MediaCaptureInitializationSettings {
                    StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Audio
                });
                Log("MIC MediaCapture initialized");
                if (_filesFolder == null) { Log("MIC ERR _filesFolder is null!"); return; }
                string fname = "voice_" + DateTimeOffset.Now.ToUnixTimeSeconds() + ".m4a";
                _recordingFile = await _filesFolder.CreateFileAsync(fname, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                Log("MIC file created: " + _recordingFile.Path);
                var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateM4a(
                    Windows.Media.MediaProperties.AudioEncodingQuality.Medium);
                await _mediaCapture.StartRecordToStorageFileAsync(profile, _recordingFile);
                _isRecording = true;
                MicButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 50, 50));
                Log("MIC recording started");
            } catch (Exception ex) {
                Log("MIC INIT ERR: " + ex.GetType().Name + " — " + ex.Message);
                _mediaCapture?.Dispose();
                _mediaCapture = null;
            }
        }

        private async void MicButton_PointerReleased(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) {
            Log("MIC RELEASED — isRecording=" + _isRecording);
            if (!_isRecording || _mediaCapture == null) return;
            try {
                await _mediaCapture.StopRecordAsync();
                Log("MIC StopRecordAsync done");
                _isRecording = false;
                MicButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
                _mediaCapture.Dispose();
                _mediaCapture = null;
                Log("MIC file path: " + _recordingFile.Path);
                var props = await _recordingFile.Properties.GetMusicPropertiesAsync();
                int durationSec = (int)props.Duration.TotalSeconds;
                Log("MIC duration: " + durationSec + " sec");
                var req = new Newtonsoft.Json.Linq.JObject {
                    ["@type"] = "sendMessage",
                    ["chat_id"] = _currentChatId,
                    ["input_message_content"] = new Newtonsoft.Json.Linq.JObject {
                        ["@type"] = "inputMessageAudio",
                        ["audio"] = new Newtonsoft.Json.Linq.JObject {
                            ["@type"] = "inputFileLocal",
                            ["path"] = _recordingFile.Path
                        },
                        ["duration"] = durationSec,
                        ["title"] = "Голосовое сообщение",
                        ["caption"] = new Newtonsoft.Json.Linq.JObject {
                            ["@type"] = "formattedText",
                            ["text"] = ""
                        }
                    }
                };
                TdJson.SendUtf8(_client, req.ToString(Newtonsoft.Json.Formatting.None));
                Log("MIC sent audio message");
            } catch (Exception ex) {
                Log("MIC STOP ERR: " + ex.GetType().Name + " — " + ex.Message);
                _isRecording = false;
                MicButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
            }
        }

        private void ChatItem_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e) {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            var grid = sender as Grid;
            if (grid == null) return;
            var chat = grid.DataContext as ChatItem;
            if (chat == null) return;
            _pendingDeleteChatId = chat.Id;
            Log("HOLDING chatId=" + chat.Id + " title=" + chat.Title);
            Windows.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(grid);
        }

        private async void DeleteChat_Click(object sender, RoutedEventArgs e) {
            var item = sender as MenuFlyoutItem;
            // Ищем Tag через визуальное дерево — идём вверх от MenuFlyoutItem
            // Tag был установлен на Grid в ChatItem_Holding
            // Ищем чат через _chatsDict по совпадению с открытым flyout
            // Надёжнее хранить pending id отдельно
            if (_pendingDeleteChatId == 0) return;
            long chatId = _pendingDeleteChatId;
            _pendingDeleteChatId = 0;
            Log("DELETE CHAT id=" + chatId);
            // Показываем диалог подтверждения
            var dialog = new Windows.UI.Popups.MessageDialog("Удалить переписку? Это действие нельзя отменить.", "Удалить переписку");
            dialog.Commands.Add(new Windows.UI.Popups.UICommand("Удалить", async cmd => {
                var req = Newtonsoft.Json.Linq.JObject.FromObject(new {
                    type = "deleteChatHistory",
                    chat_id = chatId,
                    remove_from_chat_list = true,
                    revoke = false
                });
                req["@type"] = req["type"]; req.Remove("type");
                TdJson.SendUtf8(_client, req.ToString(Newtonsoft.Json.Formatting.None));
                Log("DELETE CHAT sent for " + chatId);
                // Убираем из списка
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    var toRemove = _chatListItems.FirstOrDefault(c => c.Id == chatId);
                    if (toRemove != null) _chatListItems.Remove(toRemove);
                    if (_chatsDict.ContainsKey(chatId)) _chatsDict.Remove(chatId);
                });
            }));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand("Отмена"));
            await dialog.ShowAsync();
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

        private MessageItem _selectedMessageForCopy = null;

        private void MessageBubble_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e) {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            var border = sender as Border;
            if (border == null) return;
            _selectedMessageForCopy = border.DataContext as MessageItem;
            FlyoutBase.ShowAttachedFlyout(border);
        }

        private async void InlineButton_Click(object sender, RoutedEventArgs e) {
            var btn = (sender as Windows.UI.Xaml.Controls.Button)?.Tag as InlineButton;
            if (btn == null) return;

            if (!string.IsNullOrEmpty(btn.Url)) {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(btn.Url));
                return;
            }
            if (!string.IsNullOrEmpty(btn.CallbackData)) {
                // Найти msgId через Tag кнопки — он хранится в Tag как long через parent
                var button = sender as Windows.UI.Xaml.Controls.Button;
                // Идём вверх по визуальному дереву до Border с DataContext = MessageItem
                DependencyObject el = button;
                MessageItem msgItem = null;
                while (el != null) {
                    if (el is FrameworkElement fe && fe.DataContext is MessageItem mi) { msgItem = mi; break; }
                    el = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(el);
                }
                if (msgItem == null) return;
                string payload = "{\"@type\":\"getCallbackQueryAnswer\","
                    + "\"chat_id\":" + _currentChatId + ","
                    + "\"message_id\":" + msgItem.Id + ","
                    + "\"payload\":{\"@type\":\"callbackQueryPayloadData\","
                    + "\"data\":\"" + btn.CallbackData + "\"}}";
                TdJson.SendUtf8(_client, payload);
            }
        }

        private void CopyMessage_Click(object sender, RoutedEventArgs e) {
            if (_selectedMessageForCopy == null) return;
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(_selectedMessageForCopy.Text ?? "");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            _selectedMessageForCopy = null;
        }

        private void MessageInput_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e) {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            FlyoutBase.ShowAttachedFlyout(MessageInput);
        }

        private async void PasteToInput_Click(object sender, RoutedEventArgs e) {
            var dp = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dp.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) {
                string text = await dp.GetTextAsync();
                int pos = MessageInput.SelectionStart;
                MessageInput.Text = MessageInput.Text.Insert(pos, text);
                MessageInput.SelectionStart = pos + text.Length;
            }
        }

        private void SendPassword_Click(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(PasswordInput.Password)) return;
            PasswordButton.IsEnabled = false;
            LoginStatus.Text = "Проверка пароля...";
            var pwd = PasswordInput.Password.Replace("\\", "\\\\").Replace("\"", "\\\"");
            TdJson.SendUtf8(_client, "{\"@type\":\"checkAuthenticationPassword\",\"password\":\"" + pwd + "\"}");
        }
    }
}
