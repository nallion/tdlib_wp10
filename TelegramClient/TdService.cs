using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TelegramClient.Services
{
    // ── Базовый ViewModel ──────────────────────────────────────────────────
    public class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    // ── Модели данных ──────────────────────────────────────────────────────
    public class ChatItem : ViewModelBase
    {
        public long   Id          { get; set; }
        public string Title       { get; set; }
        public string LastMessage { get; set; }
        public string AvatarLetter => Title?.Length > 0 ? Title[0].ToString().ToUpper() : "?";
    }

    public class MessageItem : ViewModelBase
    {
        public long   Id        { get; set; }
        public string Text      { get; set; }
        public bool   IsOutgoing { get; set; }
        public string Time      { get; set; }
    }

    // ── Сервис TDLib ───────────────────────────────────────────────────────
    public class TdService : ViewModelBase
    {
        private static TdService _instance;
        public  static TdService Instance => _instance ?? (_instance = new TdService());

        private TdClient _client;
        private int _requestId = 1;

        // Состояние авторизации
        public enum AuthState { WaitPhone, WaitCode, WaitPassword, Ready, Closed }
        private AuthState _state = AuthState.WaitPhone;
        public  AuthState State { get => _state; private set => Set(ref _state, value); }

        // Коллекции
        public ObservableCollection<ChatItem>    Chats    { get; } = new ObservableCollection<ChatItem>();
        public ObservableCollection<MessageItem> Messages { get; } = new ObservableCollection<MessageItem>();

        public long CurrentChatId { get; private set; }

        // События
        public event Action<string> AuthorizationStateChanged;
        public event Action<string> Error;

        private TdService() { }

        // ── Инициализация ──────────────────────────────────────────────────
        public void Initialize(int apiId, string apiHash)
        {
            _client = new TdClient();
            _client.UpdateReceived += OnUpdate;
            _client.StartReceiving();

            // Устанавливаем параметры TDLib
            Send(new JObject
            {
                ["@type"]              = "setTdlibParameters",
                ["use_message_database"] = true,
                ["use_secret_chats"]   = false,
                ["api_id"]             = apiId,
                ["api_hash"]           = apiHash,
                ["system_language_code"] = "ru",
                ["device_model"]       = "Windows Phone",
                ["application_version"] = "1.0",
                ["database_directory"] = "tdlib_db",
                ["files_directory"]    = "tdlib_files"
            });
        }

        // ── Авторизация ────────────────────────────────────────────────────
        public void SetPhone(string phone)
        {
            Send(new JObject
            {
                ["@type"]        = "setAuthenticationPhoneNumber",
                ["phone_number"] = phone
            });
        }

        public void SetCode(string code)
        {
            Send(new JObject
            {
                ["@type"] = "checkAuthenticationCode",
                ["code"]  = code
            });
        }

        public void SetPassword(string password)
        {
            Send(new JObject
            {
                ["@type"]    = "checkAuthenticationPassword",
                ["password"] = password
            });
        }

        // ── Чаты ───────────────────────────────────────────────────────────
        public void LoadChats()
        {
            Send(new JObject
            {
                ["@type"] = "getChats",
                ["limit"] = 50
            });
        }

        public void OpenChat(long chatId)
        {
            CurrentChatId = chatId;
            Messages.Clear();
            Send(new JObject
            {
                ["@type"]   = "getChatHistory",
                ["chat_id"] = chatId,
                ["limit"]   = 50,
                ["from_message_id"] = 0,
                ["offset"]  = 0
            });
        }

        public void SendMessage(string text)
        {
            if (CurrentChatId == 0 || string.IsNullOrEmpty(text)) return;
            Send(new JObject
            {
                ["@type"]   = "sendMessage",
                ["chat_id"] = CurrentChatId,
                ["input_message_content"] = new JObject
                {
                    ["@type"] = "inputMessageText",
                    ["text"]  = new JObject
                    {
                        ["@type"] = "formattedText",
                        ["text"]  = text
                    }
                }
            });
        }

        // ── Обработка апдейтов ─────────────────────────────────────────────
        private async void OnUpdate(string json)
        {
            JObject obj;
            try { obj = JObject.Parse(json); }
            catch { return; }

            var type = obj["@type"]?.ToString();
            if (type == null) return;

            await RunOnUI(async () =>
            {
                switch (type)
                {
                    case "updateAuthorizationState":
                        HandleAuthState(obj["authorization_state"] as JObject);
                        break;

                    case "updateNewChat":
                        HandleNewChat(obj["chat"] as JObject);
                        break;

                    case "updateChatLastMessage":
                        HandleChatLastMessage(obj);
                        break;

                    case "messages":
                        HandleMessages(obj);
                        break;

                    case "updateNewMessage":
                        HandleNewMessage(obj["message"] as JObject);
                        break;

                    case "error":
                        Error?.Invoke(obj["message"]?.ToString());
                        break;
                }
            });
        }

        private void HandleAuthState(JObject state)
        {
            if (state == null) return;
            var t = state["@type"]?.ToString();
            switch (t)
            {
                case "authorizationStateWaitPhoneNumber": State = AuthState.WaitPhone;    break;
                case "authorizationStateWaitCode":        State = AuthState.WaitCode;     break;
                case "authorizationStateWaitPassword":    State = AuthState.WaitPassword; break;
                case "authorizationStateReady":
                    State = AuthState.Ready;
                    LoadChats();
                    break;
                case "authorizationStateClosed":          State = AuthState.Closed;       break;
            }
            AuthorizationStateChanged?.Invoke(t);
        }

        private void HandleNewChat(JObject chat)
        {
            if (chat == null) return;
            var id    = chat["id"]?.Value<long>() ?? 0;
            var title = chat["title"]?.ToString() ?? "Без названия";
            var last  = chat["last_message"]?["content"]?["text"]?["text"]?.ToString() ?? "";

            // Обновляем или добавляем
            foreach (var c in Chats)
                if (c.Id == id) { c.LastMessage = last; return; }

            Chats.Add(new ChatItem { Id = id, Title = title, LastMessage = last });
        }

        private void HandleChatLastMessage(JObject obj)
        {
            var id   = obj["chat_id"]?.Value<long>() ?? 0;
            var last = obj["last_message"]?["content"]?["text"]?["text"]?.ToString() ?? "";
            foreach (var c in Chats)
                if (c.Id == id) { c.LastMessage = last; break; }
        }

        private void HandleMessages(JObject obj)
        {
            var msgs = obj["messages"] as JArray;
            if (msgs == null) return;
            // Сообщения приходят от новых к старым — реверсируем
            for (int i = msgs.Count - 1; i >= 0; i--)
                HandleNewMessage(msgs[i] as JObject);
        }

        private void HandleNewMessage(JObject msg)
        {
            if (msg == null) return;
            var chatId = msg["chat_id"]?.Value<long>() ?? 0;
            if (chatId != CurrentChatId) return;

            var id   = msg["id"]?.Value<long>() ?? 0;
            var text = msg["content"]?["text"]?["text"]?.ToString() ?? "[медиа]";
            var out_ = msg["is_outgoing"]?.Value<bool>() ?? false;
            var date = msg["date"]?.Value<long>() ?? 0;
            var time = DateTimeOffset.FromUnixTimeSeconds(date).LocalDateTime.ToString("HH:mm");

            Messages.Add(new MessageItem { Id = id, Text = text, IsOutgoing = out_, Time = time });
        }

        // ── Утилиты ────────────────────────────────────────────────────────
        private void Send(JObject obj)
        {
            obj["@extra"] = _requestId++;
            _client?.Send(obj.ToString(Formatting.None));
        }

        private static async Task RunOnUI(Action action)
        {
            await CoreApplication.MainView.CoreWindow.Dispatcher
                .RunAsync(CoreDispatcherPriority.Normal, () => action());
        }
    }
}
