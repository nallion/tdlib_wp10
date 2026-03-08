using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace TelegramWP10 {
    public sealed partial class MainPage : Page {
        private IntPtr _client;
        public MainPage() {
            this.InitializeComponent();
            _client = TdJson.td_json_client_create();
            Task.Run(() => LongPolling());
            SendParameters();
        }
        private void SendParameters() {
            string path = Windows.Storage.ApplicationData.Current.LocalFolder.Path.Replace("\\", "/");
            string json = "{\"@type\":\"setTdlibParameters\",\"use_test_dc\":false,\"database_directory\":\"" + path + "/db\",\"files_directory\":\"" + path + "/files\",\"api_id\":12345,\"api_hash\":\"YOUR_HASH\",\"system_language_code\":\"en\",\"device_model\":\"Lumia\",\"system_version\":\"WP10\",\"application_version\":\"1.0\"}";
            TdJson.td_json_client_send(_client, json);
        }
        private void SendCode_Click(object sender, RoutedEventArgs e) {
            string json = "{\"@type\":\"setAuthenticationPhoneNumber\",\"phone_number\":\"" + PhoneInput.Text + "\"}";
            TdJson.td_json_client_send(_client, json);
            StatusText.Text = "Sending...";
        }
        private void LongPolling() {
            while (true) {
                IntPtr resPtr = TdJson.td_json_client_receive(_client, 1.0);
                if (resPtr != IntPtr.Zero) {
                    string res = TdJson.IntPtrToStringUtf8(resPtr);
                    var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                        if (res.Contains("authorizationStateWaitEncryptionKey"))
                            TdJson.td_json_client_send(_client, "{\"@type\":\"checkDatabaseEncryptionKey\"}");
                        if (res.Contains("authorizationStateWaitCode")) StatusText.Text = "Code Sent!";
                    });
                }
            }
        }
    }
}
