using appmaui.blazor.Services;

namespace appmaui.blazor
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new MainPage()) { Title = "YouLink" };
            window.Activated += (_, _) => TriggerUpdateCheck(window);
            window.Resumed += (_, _) => TriggerUpdateCheck(window);
            return window;
        }

        private static void TriggerUpdateCheck(Window window)
        {
            try
            {
                var services = window.Handler?.MauiContext?.Services
                    ?? Current?.Handler?.MauiContext?.Services
                    ?? IPlatformApplication.Current?.Services;
                var updateService = services?.GetService<AppUpdateService>();
                if (updateService is not null)
                {
                    _ = updateService.CheckForUpdatesOnResumeAsync();
                }
            }
            catch
            {
            }
        }
    }
}
