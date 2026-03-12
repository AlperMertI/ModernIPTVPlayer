using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using System.Runtime.InteropServices;
using System.Diagnostics;
using ModernIPTVPlayer.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ModernIPTVPlayer
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }
        public static LoginParams? CurrentLogin { get; set; }
        public static MpvWinUI.MpvPlayer? HandoffPlayer = null;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int MessageBox(IntPtr hWnd, String text, String caption, uint type);

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();

            // 1. Initialize Logging as early as possible
            try
            {
                var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                var logDir = System.IO.Path.Combine(localFolder, "logs");
                var logPath = System.IO.Path.Combine(logDir, "app.log");
                
                // Capture all Trace/Debug output to file
                Trace.Listeners.Add(new ModernIPTVPlayer.Services.FileLoggerListener(logPath));
                AppLogger.Info("--- App Starting ---");
                AppLogger.Info($"Log Path: {logPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FATAL] Could not initialize logger: {ex.Message}");
            }

            // 2. Setup Global Exception Handlers
            
            // UI Thread Exceptions (WinUI 3)
            UnhandledException += (sender, e) =>
            {
                e.Handled = true; // Try to prevent total crash if possible, but still log
                HandleFatalException(e.Exception, "WinUI UnhandledException");
            };

            // Non-UI Thread Exceptions
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                HandleFatalException(e.ExceptionObject as Exception, "AppDomain UnhandledException");
            };

            // Async Task Exceptions
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                HandleFatalException(e.Exception, "UnobservedTaskException");
                e.SetObserved(); // Prevent crash
            };
            // 3. Load Dependencies
            try
            {
                HandleDllLoading();
            }
            catch (Exception ex)
            {
                HandleFatalException(ex, "App Constructor (DLL Load)");
            }
        }

        private void HandleFatalException(Exception? ex, string source)
        {
            AppLogger.Critical($"FATAL EXCEPTION from {source}", ex);
            Trace.Flush(); // Force write to file

            string errorMessage = ex?.ToString() ?? "Bilinmeyen kritik hata.";
            
            // Show alert even if window is not ready
            MessageBox(IntPtr.Zero, 
                $"Uygulama beklenmedik bir hata nedeniyle çökebilir.\n\nKaynak: {source}\n\nHata: {ex?.Message}\n\nDetaylar log dosyasına kaydedildi.", 
                "Kritik Hata (Modern IPTV Player)", 
                0x10);

            if (global::System.Diagnostics.Debugger.IsAttached)
            {
                global::System.Diagnostics.Debugger.Break();
            }
        }

        private void HandleDllLoading()
        {
            // DLL LOADING LOGIC: libmpv-2.dll ve bağımlılıklarını açıkça yükle
            try
            {
                string baseDir = AppContext.BaseDirectory;
                AppLogger.Info($"Base Directory: {baseDir}");

                SetDllDirectory(baseDir); 

                string mpvPath = System.IO.Path.Combine(baseDir, "libmpv-2.dll");
                string pthreadPath = System.IO.Path.Combine(baseDir, "libwinpthread-1.dll");
                string unwindPath = System.IO.Path.Combine(baseDir, "libunwind.dll");
                
                if (File.Exists(pthreadPath)) {
                    var h1 = LoadLibrary(pthreadPath);
                    AppLogger.Info($"libwinpthread-1.dll: {(h1 != IntPtr.Zero ? "OK" : "FAIL")}");
                }
                if (File.Exists(unwindPath)) {
                    var h2 = LoadLibrary(unwindPath);
                    AppLogger.Info($"libunwind.dll: {(h2 != IntPtr.Zero ? "OK" : "FAIL")}");
                }

                if (File.Exists(mpvPath))
                {
                    var hMpv = LoadLibrary(mpvPath);
                    if (hMpv == IntPtr.Zero)
                    {
                        uint lastError = GetLastError();
                        AppLogger.Critical($"libmpv-2.dll: FAIL (Error: {lastError})");
                        MessageBox(IntPtr.Zero, $"libmpv-2.dll yüklenemedi. Hata Kodu: {lastError}", "DLL Yükleme Hatası", 0x10);
                    }
                    else
                    {
                        AppLogger.Info($"libmpv-2.dll: SUCCESS (Handle: {hMpv})");
                    }
                }
                else
                {
                    AppLogger.Warn($"libmpv-2.dll: NOT FOUND at {mpvPath}");
                    MessageBox(IntPtr.Zero, "libmpv-2.dll dosyası bulunamadı. Lütfen kurulumu kontrol edin.", "DLL Bulunamadı", 0x10);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("DLL loading exception", ex);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll")]
        private static extern uint GetLastError();

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            MainWindow = new MainWindow();
            
            // Enable System-Wide Premium Audio (Cinematic Clicks)
            ElementSoundPlayer.State = ElementSoundPlayerState.On;
            ElementSoundPlayer.SpatialAudioMode = ElementSpatialAudioMode.On;

            MainWindow.Activate();
        }
    }
}
