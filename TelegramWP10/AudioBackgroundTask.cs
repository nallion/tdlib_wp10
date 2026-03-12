using Windows.ApplicationModel.Background;
using Windows.Media;
using Windows.Media.Playback;

namespace TelegramWP10 {
    /// <summary>
    /// Фоновая задача для воспроизведения аудио.
    /// Регистрируется в манифесте как windows.backgroundTasks / audio.
    /// Это позволяет MediaPlayer продолжать играть после суспенда приложения.
    /// </summary>
    public sealed class AudioBackgroundTask : IBackgroundTask {
        private BackgroundTaskDeferral _deferral;

        public void Run(IBackgroundTaskInstance taskInstance) {
            _deferral = taskInstance.GetDeferral();
            // Задача держит деферрал пока плеер активен.
            // BackgroundMediaPlayer.Current управляется из MainPage.
            taskInstance.Canceled += (sender, reason) => {
                _deferral.Complete();
            };
        }
    }
}
