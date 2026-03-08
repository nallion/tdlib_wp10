using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using System.Diagnostics;

public sealed partial class MainPage : Page
{
    private IntPtr _client;
    private bool _isListening = true;

    // Данные вашего приложения (получите на my.telegram.org)
    private const int ApiId = 1234567; 
    private const string ApiHash = "your_api_hash";

    public MainPage()
    {
        this.InitializeComponent();
        _client = TdJson.td_json_client_create();
        Task.Run(() => LongPolling());
        
        // Начинаем инициализацию параметров
        SendParameters();
    }

    private void SendParameters()
    {
        string localPath = Windows.Storage.ApplicationData.Current.LocalFolder.Path.Replace("\\", "/");
        string json = $@"{{
            ""@type"": ""setTdlibParameters"",
            ""use_test_dc"": false,
            ""database_directory"": ""{localPath}/tdlib"",
            ""files_directory"": ""{localPath}/files"",
            ""use_file_database"": true,
            ""api_id"": {ApiId},
            ""api_hash"": ""{ApiHash}"",
            ""system_language_code"": ""ru"",
            ""device_model"": ""Lumia"",
            ""system_version"": ""Windows 10 Mobile"",
            ""application_version"": ""1.0""
        }}";
        TdJson.td_json_client_send(_client, json);
    }

    private async void SendCode_Click(object sender, RoutedEventArgs e)
    {
        string phone = PhoneInput.Text;
        string json = $"{{\"@type\": \"setAuthenticationPhoneNumber\", \"phone_number\": \"{phone}\"}}";
        TdJson.td_json_client_send(_client, json);
        StatusText.Text = "Отправка запроса...";
    }

    private void LongPolling()
    {
        while (_isListening)
        {
            IntPtr responsePtr = TdJson.td_json_client_receive(_client, 1.0);
            if (responsePtr != IntPtr.Zero)
            {
                string response = TdJson.IntPtrToStringUtf8(responsePtr);
                Debug.WriteLine(response);

                // Обработка состояний (в идеале использовать JSON парсер)
                if (response.Contains("authorizationStateWaitEncryptionKey"))
                {
                    TdJson.td_json_client_send(_client, "{\"@type\": \"checkDatabaseEncryptionKey\"}");
                }
                
                // Обновляем статус в UI потоке
                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    if (response.Contains("authorizationStateWaitCode"))
                        StatusText.Text = "СМС отправлено! Введите код (реализуйте checkAuthenticationCode)";
                    else if (response.Contains("error"))
                        StatusText.Text = "Ошибка: " + response;
                });
            }
        }
    }
}
