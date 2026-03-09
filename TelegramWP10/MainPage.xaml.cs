using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Newtonsoft.Json.Linq;
using System.Linq;

namespace TelegramWP10
{
    public sealed partial class MainPage : Page
    {
        private IntPtr _client;
        private ObservableCollection<ChatItem> _chatListItems = new ObservableCollection<ChatItem>();
        private Dictionary<long, ChatItem> _chatsDict = new Dictionary<long, ChatItem>();
        private Dictionary<int, long> _fileToChatId = new Dictionary<int, long>();

        public MainPage()
        {
            this.InitializeComponent();
            _client = TdJson.td_json_client_create();
            ChatListView.ItemsSource = _chatListItems;
            Task.Run(() => LongPolling());
            SendParameters();
        }

        private void SendParameters()
        {
            string path = Windows.Storage.ApplicationData.Current.LocalFolder.Path.Replace("\\", "/");
            string json = "{\"@type\":\"setTdlibParameters\",\"use_test_dc\":false,\"database_directory\":\"" + path + "/db\",\"files_directory\":\"" + path + "/files\",\"api_id\":ВАШ_ID,\"api_hash\":\"ВАШ_HASH\",\"system_language_code\":\"ru\",\"device_model\":\"Lumia\",\"system_version\":\"WP10\",\"application_version\":\"1.0\"}";
            TdJson.td_json_client_send(_client, json);
        }

        private void LongPolling()
        {
            while (true)
            {
                IntPtr resPtr = TdJson.td_json_client_receive(_client, 1.0);
                if (resPtr != IntPtr.Zero)
                {
                    string json = TdJson.IntPtrToStringUtf8(resPtr);
                    var update = JObject.Parse(json);
                    string type = update["@type"]?.ToString();

                    var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                        HandleUpdate(type, update);
                    });
                }
            }
        }

        private void HandleUpdate(string type, JObject update)
        {
            switch (type)
            {
                case "updateAuthorizationState":
                    var state = update["authorization_state"]["@type"].ToString();
                    if (state == "authorizationStateWaitCode")
                    {
                        CodeInput.Visibility = Visibility.Visible;
                        CodeButton.Visibility = Visibility.Visible;
                        StatusText.Text = "Введите код:";
                    }
                    else if (state == "authorizationStateReady")
                    {
                        LoginPanel.Visibility = Visibility.Collapsed;
                        ChatListView.Visibility = Visibility.Visible;
                        StatusText.Text = "Чаты:";
                        TdJson.td_json_client_send(_client, "{\"@type\":\"getChats\",\"offset_order\":\"9223372036854775807\",\"offset_chat_id\":0,\"limit\":20}");
                    }
                    break;

                case "updateNewChat":
                    var chat = update["chat"];
                    long id = (long)chat["id"];
                    string title = chat["title"].ToString();
                    
                    var item = new ChatItem { Id = id, Title = title };
                    if (!_chatsDict.ContainsKey(id)) {
                        _chatsDict[id] = item;
                        _chatListItems.Add(item);
                    }

                    // Если есть фото — качаем
                    if (chat["photo"]?["small"] != null) {
                        int fId = (int)chat["photo"]["small"]["id"];
                        _fileToChatId[fId] = id;
                        TdJson.td_json_client_send(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + fId + ",\"priority\":1}");
                    }
                    break;

                case "updateFile":
                    var file = update["file"];
                    int fileId = (int)file["id"];
                    if (file["local"]["is_completed"].Value<bool>() && _fileToChatId.ContainsKey(fileId)) {
                        long cId = _fileToChatId[fileId];
                        _chatsDict[cId].PhotoPath = file["local"]["path"].ToString();
                    }
                    break;
            }
        }

        private void SendPhone_Click(object sender, RoutedEventArgs e) =>
            TdJson.td_json_client_send(_client, "{\"@type\":\"setAuthenticationPhoneNumber\",\"phone_number\":\"" + PhoneInput.Text + "\"}");

        private void SendCode_Click(object sender, RoutedEventArgs e) =>
            TdJson.td_json_client_send(_client, "{\"@type\":\"checkAuthenticationCode\",\"code\":\"" + CodeInput.Text + "\"}");
    }
}
