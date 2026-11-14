using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace appmaui.blazor.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// Enforces strict single-instance window behavior on Windows so double-clicking YouLink
    /// brings the active window to the foreground instead of spawning a second window.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        private const string SingleInstanceMutexName = @"Local\YouLink_AppWindow_Mutex_v1";
        private const string ActivateWindowEventName = @"Local\YouLink_ActivateWindow_Event_v1";
        private const int SwShow = 5;
        private const int SwRestore = 9;

        private static Mutex? _singleInstanceMutex;
        private static EventWaitHandle? _activateWindowEvent;
        private static int _hasAcquiredMutex;

        /// <summary>
        /// Initializes the singleton application object. This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            if (!TryAcquireSingleInstance())
            {
                ActivateExistingYouLinkWindow();
                Environment.Exit(0);
                return;
            }

            StartActivationSignalListener();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseSingleInstanceHandles();

            base.OnLaunched(args);
        }

        private static bool TryAcquireSingleInstance()
        {
            if (_hasAcquiredMutex == 1)
                return true;

            _singleInstanceMutex = new Mutex(initiallyOwned: false, name: SingleInstanceMutexName);

            bool acquired = false;
            try
            {
                acquired = _singleInstanceMutex.WaitOne(0, exitContext: false);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            // Se outro processo já segura o mutex, verifica primeiro se ele já tem janela ativa
            // (resposta imediata em duplo clique) ou aguarda brevemente caso esteja encerrando para autoatualização.
            if (!acquired && !HasExistingYouLinkWindow())
            {
                try
                {
                    acquired = _singleInstanceMutex.WaitOne(TimeSpan.FromMilliseconds(1200), exitContext: false);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
            }

            if (acquired)
            {
                Interlocked.Exchange(ref _hasAcquiredMutex, 1);
                return true;
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }

        private static void StartActivationSignalListener()
        {
            try
            {
                _activateWindowEvent = new EventWaitHandle(
                    initialState: false,
                    mode: EventResetMode.AutoReset,
                    name: ActivateWindowEventName);

                var listenerThread = new Thread(() =>
                {
                    while (_activateWindowEvent is not null)
                    {
                        try
                        {
                            if (!_activateWindowEvent.WaitOne())
                                break;

                            BringOwnWindowToFront();
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }
                        catch
                        {
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "YouLinkSingleInstanceActivator"
                };

                listenerThread.Start();
            }
            catch
            {
            }
        }

        private static void BringOwnWindowToFront()
        {
            try
            {
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
                        if (mauiWindow?.Handler?.PlatformView is Microsoft.UI.Xaml.Window winUiWindow)
                        {
                            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(winUiWindow);
                            if (hwnd != IntPtr.Zero)
                            {
                                ShowWindow(hwnd, IsIconic(hwnd) ? SwRestore : SwShow);
                                SetForegroundWindow(hwnd);
                            }

                            winUiWindow.Activate();
                        }
                    }
                    catch
                    {
                    }
                });
            }
            catch
            {
            }
        }

        private static bool HasExistingYouLinkWindow() =>
            FindExistingYouLinkWindow(out _, out _) != IntPtr.Zero;

        private static void ActivateExistingYouLinkWindow()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateWindowEventName, out var ev))
                {
                    using (ev)
                    {
                        ev.Set();
                    }
                }
            }
            catch
            {
            }

            var hwnd = FindExistingYouLinkWindow(out var targetPid, out _);
            if (hwnd == IntPtr.Zero)
                return;

            if (targetPid != 0)
            {
                AllowSetForegroundWindow(targetPid);
            }

            ShowWindow(hwnd, IsIconic(hwnd) ? SwRestore : SwShow);
            SetForegroundWindow(hwnd);
        }

        private static IntPtr FindExistingYouLinkWindow(out uint targetPid, out string? foundTitle)
        {
            uint currentPid = (uint)Environment.ProcessId;
            IntPtr matchedHwnd = IntPtr.Zero;
            uint matchedPid = 0;
            string? matchedTitle = null;

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindow(hwnd))
                    return true;

                if (!IsWindowVisible(hwnd) && !IsIconic(hwnd))
                    return true;

                if (GetWindow(hwnd, GwOwner) != IntPtr.Zero)
                    return true;

                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0 || pid == currentPid)
                    return true;

                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    var procName = proc.ProcessName;
                    if (!string.Equals(procName, "appmaui.blazor", StringComparison.OrdinalIgnoreCase))
                        return true;

                    matchedHwnd = hwnd;
                    matchedPid = pid;
                    matchedTitle = proc.MainWindowTitle;
                    return false;
                }
                catch
                {
                    return true;
                }
            }, IntPtr.Zero);

            targetPid = matchedPid;
            foundTitle = matchedTitle;
            return matchedHwnd;
        }

        private static void ReleaseSingleInstanceHandles()
        {
            try
            {
                if (Interlocked.Exchange(ref _hasAcquiredMutex, 0) == 1)
                {
                    _singleInstanceMutex?.ReleaseMutex();
                }
            }
            catch
            {
            }

            try
            {
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
            }
            catch
            {
            }

            try
            {
                _activateWindowEvent?.Dispose();
                _activateWindowEvent = null;
            }
            catch
            {
            }
        }

        private const uint GwOwner = 4;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);
    }
}
