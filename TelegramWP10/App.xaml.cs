using System;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace TelegramWP10 {
    sealed partial class App : Application {
        public App() {
            this.InitializeComponent();
            this.EnteredBackground += App_EnteredBackground;
        }

        private async void App_EnteredBackground(object sender, Windows.ApplicationModel.EnteredBackgroundEventArgs e) {
            // Держим процесс живым пока MediaPlayer активен
            var deferral = e.GetDeferral();
            try {
                await System.Threading.Tasks.Task.Run(async () => {
                    // Даём системе время зарегистрировать MediaPlayer как фоновый источник
                    await System.Threading.Tasks.Task.Delay(2000);
                });
            } finally {
                deferral.Complete();
            }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e) {
            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null) {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }
            if (rootFrame.Content == null) rootFrame.Navigate(typeof(MainPage), e.Arguments);
            Window.Current.Activate();
        }
    }
}
