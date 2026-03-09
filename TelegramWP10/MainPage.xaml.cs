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
        private string _dbPath = "";
        private StorageFolder _filesFolder = null; // папка файлов TDLib — читаем через WinRT

        public MainPage()
        {
            this.InitializeComponent();
            _client = TdJson.td_json_client_create();
            ChatListView.ItemsSource = _chatListItems;
            MessagesListView.ItemsSource = _messageItems;
            InitAsync();
        }

        private async void InitAsync() {
            try {
                var appFolder = await Windows.Storage.KnownFolders.MusicLibrary
                    .CreateFolderAsync("TelegramWP10", CreationCollisionOption.OpenIfExists);
                _dbPath = appFolder.Path.Replace("\\", "/") + "/td_db";
                // Создаём папку файлов заранее и сохраняем StorageFolder для чтения
                _filesFolder = await appFolder.CreateFolderAsync("td_db_files", CreationCollisionOption.OpenIfExists);
            } catch (Exception ex) {
                await ShowError("Ошибка доступа к хранилищу:\n" + ex.Message + "\n\nТип: " + ex.GetType().Name);
                return;
            }
            Task.Run(() => LongPolling());
        }

        private async Task ShowError(string message) {
            var dialog = new Windows.UI.Popups.MessageDialog(message, "Ошибка");
            await dialog.ShowAsync();
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
                        LoginStatus.Text = "Введите пароль двухфакторной аутентификации";
                    if (s == "authorizationStateReady") {
                        LoginPanel.Visibility = Visibility.Collapsed;
                        ChatListView.Visibility = Visibility.Visible;
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"offset_order\":\"9223372036854775807\",\"offset_chat_id\":0,\"limit\":30}");
                    }
                    if (s == "authorizationStateLoggingOut" || s == "authorizationStateClosed") {
                        LoginPanel.Visibility = Visibility.Visible;
                        LoginStatus.Text = "Выход из аккаунта...";
                    }
                    break;

                case "error":
                    LoginStatus.Text = "Ошибка: " + update["message"]?.ToString();
                    PhoneButton.IsEnabled = true;
                    CodeButton.IsEnabled = true;
                    break;

                case "updateNewChat":
                    var c = update["chat"]; long id = (long)c["id"];
                    if (!_chatsDict.ContainsKey(id))
                        _chatsDict[id] = new ChatItem { Id = id, Title = c["title"]?.ToString() };
                    var ph = c["photo"]?["small"];
                    if (ph != null) {
                        _fileToChatId[(long)ph["id"]] = id;
                        bool done = ph["local"]?["is_completed"]?.ToObject<bool>() ?? false;
                        ProcessFile((long)ph["id"], ph["local"]?["path"]?.ToString(), done);
                    }
                    break;

                case "updateFile":
                    var f = update["file"];
                    if (f != null && (f["local"]?["is_completed"]?.ToObject<bool>() ?? false)) {
                        long fid = (long)f["id"];
                        string fpath = f["local"]["path"]?.ToString();
                        if (_fileToChatId.ContainsKey(fid)) { var t = UpdateAvatar(_fileToChatId[fid], fpath); }
                        if (_fileToMsgId.ContainsKey(fid)) { var t = UpdateMessagePhoto(_fileToMsgId[fid], fpath); }
                    }
                    break;

                case "chats":
                    foreach (var cId in update["chat_ids"]) {
                        long cid = (long)cId;
                        if (_chatsDict.ContainsKey(cid) && !_chatListItems.Contains(_chatsDict[cid]))
                            _chatListItems.Add(_chatsDict[cid]);
                    }
                    break;

                case "messages":
                    // Баг 2 fix: TDLib не возвращает chat_id в ответе getChatHistory —
                    // проверяем только если поле реально есть и не равно 0
                    long msgChatId = update["chat_id"]?.ToObject<long>() ?? _currentChatId;
                    if (msgChatId != _currentChatId) break;
                    _messageItems.Clear();
                    var msgs = update["messages"] as JArray;
                    if (msgs != null) {
                        // Сообщения приходят от новых к старым — вставляем в обратном порядке
                        for (int i = msgs.Count - 1; i >= 0; i--) {
                            var item = ParseMessage(msgs[i]);
                            if (item != null) _messageItems.Add(item);
                        }
                    }
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
                    Id = msgId,
                    Text = txt,
                    Date = DateTimeOffset.FromUnixTimeSeconds((long)msg["date"]).LocalDateTime.ToString("HH:mm"),
                    Alignment = (bool)msg["is_outgoing"] ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Background = (bool)msg["is_outgoing"] ? "#0088cc" : "#333333"
                };

                // Баг 4 fix: в новом TDLib reply_to имеет @type "messageReplyToMessage"
                var replyTo = msg["reply_to"];
                if (replyTo != null && replyTo["@type"]?.ToString() == "messageReplyToMessage")
                    item.ReplyToText = "Ответ на сообщение";

                if (type == "messagePhoto") {
                    // Баг 3 fix: в новом TDLib поле называется "file", не "photo" внутри photoSize
                    var sizes = content["photo"]?["sizes"] as JArray;
                    JToken fileToken = null;
                    if (sizes != null && sizes.Count > 0) {
                        // Берём наибольший размер
                        var largest = sizes[sizes.Count - 1];
                        fileToken = largest["photo"] ?? largest["file"]; // поддерживаем оба варианта
                    }
                    if (fileToken != null) {
                        long phFileId = (long)fileToken["id"];
                        _fileToMsgId[phFileId] = msgId;
                        _messagesDict[msgId] = item;
                        bool phReady = fileToken["local"]?["is_completed"]?.ToObject<bool>() ?? false;
                        ProcessFile(phFileId, fileToken["local"]?["path"]?.ToString(), phReady);
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
                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + fId + ",\"priority\":10,\"synchronous\":false}");
            }
        }

        // Баг 1 fix: читаем файл через StorageFile.GetFileFromPathAsync —
        // работает т.к. _filesFolder создан через WinRT и приложение имеет к нему доступ
        private async Task UpdateAvatar(long chatId, string path) {
            try {
                if (string.IsNullOrEmpty(path)) return;
                var file = await StorageFile.GetFileFromPathAsync(path);
                var bitmap = new BitmapImage();
                using (var stream = await file.OpenReadAsync())
                    await bitmap.SetSourceAsync(stream);
                if (_chatsDict.ContainsKey(chatId))
                    _chatsDict[chatId].Photo = bitmap;
            } catch { }
        }

        private async Task UpdateMessagePhoto(long msgId, string path) {
            try {
                if (string.IsNullOrEmpty(path)) return;
                var file = await StorageFile.GetFileFromPathAsync(path);
                var bitmap = new BitmapImage();
                using (var stream = await file.OpenReadAsync())
                    await bitmap.SetSourceAsync(stream);
                if (_messagesDict.ContainsKey(msgId))
                    _messagesDict[msgId].AttachedPhoto = bitmap;
            } catch { }
        }

        private void ChatListView_ItemClick(object sender, ItemClickEventArgs e) {
            var chat = (ChatItem)e.ClickedItem;
            _currentChatId = chat.Id;
            CurrentChatTitle.Text = chat.Title;
            _messageItems.Clear();
            _messagesDict.Clear();
            _fileToMsgId.Clear();
            StartPanel.Visibility = Visibility.Collapsed;
            MessagesPanel.Visibility = Visibility.Visible;
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

        private void BackButton_Click(object sender, RoutedEventArgs e) {
            _currentChatId = 0;
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
