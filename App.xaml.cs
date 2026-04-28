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
using System.Runtime.ExceptionServices;
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
        public static event Action<LoginParams?> LoginChanged;
        
        private static LoginParams? _currentLogin;
        public static LoginParams? CurrentLogin 
        { 
            get => _currentLogin; 
            set 
            {
                if (_currentLogin != value)
                {
                    string? oldPid = _currentLogin?.PlaylistId;
                    _currentLogin = value;
                    string? newPid = value?.PlaylistId;
                    if (!string.Equals(oldPid, newPid, StringComparison.Ordinal))
                    {
                        MediaLibraryStateService.Instance.Invalidate();
                        if (!string.IsNullOrEmpty(oldPid))
                            ContentCacheService.Instance.InvalidateRamSessionsForPlaylist(oldPid);
                    }
                    AppLogger.Info($"[App] CurrentLogin changed to: {value?.PlaylistName ?? "null"}");
                    LoginChanged?.Invoke(value);
                }
            }
        }
        internal static MpvWinUI.MpvPlayer? HandoffPlayer = null;
        public static Dictionary<string, object>? LastPlayerIntent { get; set; }

        [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        // #region agent log
        private static readonly object _ndjsonLock = new object();
        private static readonly string _ndjsonPath = @"C:\Users\ASUS\Documents\ModernIPTVPlayer\debug-c378c9.log";
        private static Exception? _lastFirstChance;
        private static int _firstChanceCount;

        internal static void DebugNdjson(string location, string message, IDictionary<string, object?>? data, string? hypothesisId)
        {
            try
            {
                var sb = new System.Text.StringBuilder(256);
                sb.Append('{');
                sb.Append("\"sessionId\":\"c378c9\",");
                sb.Append("\"timestamp\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Append(',');
                sb.Append("\"tid\":").Append(Environment.CurrentManagedThreadId).Append(',');
                sb.Append("\"location\":\"").Append(JsonEscape(location)).Append("\",");
                sb.Append("\"message\":\"").Append(JsonEscape(message)).Append("\"");
                if (hypothesisId != null)
                {
                    sb.Append(",\"hypothesisId\":\"").Append(JsonEscape(hypothesisId)).Append("\"");
                }
                if (data != null && data.Count > 0)
                {
                    sb.Append(",\"data\":{");
                    bool first = true;
                    foreach (var kv in data)
                    {
                        if (!first) sb.Append(','); first = false;
                        sb.Append('\"').Append(JsonEscape(kv.Key)).Append("\":");
                        if (kv.Value == null) sb.Append("null");
                        else if (kv.Value is int i) sb.Append(i);
                        else if (kv.Value is long l) sb.Append(l);
                        else if (kv.Value is bool b) sb.Append(b ? "true" : "false");
                        else 
                        {
                            string valStr = "";
                            try { valStr = kv.Value?.ToString() ?? ""; } catch { valStr = "[Unprintable]"; }
                            sb.Append('\"').Append(JsonEscape(valStr)).Append('\"');
                        }
                    }
                    sb.Append('}');
                }
                sb.Append("}\n");
                lock (_ndjsonLock)
                {
                    File.AppendAllText(_ndjsonPath, sb.ToString());
                }
            }
            catch { /* instrumentation must never throw */ }
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
        {
            // capture EVERY exception up to cap — cast-related ones are often hidden inside try/catch
            var ex = e.Exception;
            if (ex == null) return;
            _lastFirstChance = ex;
            int n = System.Threading.Interlocked.Increment(ref _firstChanceCount);
            if (n > 50) return; // cap — widened filter
            var type = ex.GetType().FullName ?? "";
            DebugNdjson("App.xaml.cs:FirstChance",
                "FirstChanceException captured",
                new Dictionary<string, object?>
                {
                    ["n"] = n,
                    ["type"] = type,
                    ["hresult"] = ex.HResult,
                    ["message"] = ex.Message,
                    ["stack"] = ex.StackTrace,
                    ["targetSite"] = ex.TargetSite?.ToString()
                },
                "all");
        }
        // #endregion

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            // #region agent log
            DebugNdjson("App.xaml.cs:ctor", "App ctor entering", null, "boot");
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
            // #endregion
            this.InitializeComponent();
            // #region agent log
            DebugNdjson("App.xaml.cs:ctor", "InitializeComponent done", null, "boot");
            // #endregion

            // 2. Setup Global Exception Handlers
            
            // UI Thread Exceptions (WinUI 3)
            UnhandledException += (sender, e) =>
            {
                // #region agent log
                DebugNdjson("App.xaml.cs:UnhandledException",
                    "WinUI UnhandledException raised",
                    new Dictionary<string, object?>
                    {
                        ["type"] = e.Exception?.GetType().FullName,
                        ["message"] = e.Exception?.Message,
                        ["hresult"] = e.Exception?.HResult,
                        ["stack"] = e.Exception?.StackTrace,
                        ["inner"] = e.Exception?.InnerException?.ToString(),
                        ["captured_first_chance"] = _lastFirstChance?.ToString()
                    },
                    "crash");
                // #endregion
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
            // XAML parse error kontrolü
            string errorType = ex?.GetType().Name ?? "Unknown";
            bool isXamlError = errorType.Contains("Xaml") || 
                               (ex?.Message?.Contains("attribute value") == true) ||
                               (ex?.Message?.Contains("Unknown") == true);
            
            // Detaylı log mesajı oluştur
            string detailedLog = $"=== FATAL EXCEPTION ===\n";
            detailedLog += $"Source: {source}\n";
            detailedLog += $"Error Type: {errorType}\n";
            detailedLog += $"Is XAML Error: {isXamlError}\n";
            detailedLog += $"Message: {ex?.Message}\n";
            detailedLog += $"StackTrace:\n{ex?.StackTrace ?? "NO EXCEPTION STACK TRACE"}\n";
            detailedLog += $"Environment StackTrace:\n{Environment.StackTrace}\n";
            
            // InnerException zincirini tamamen çözümle
            Exception? currentInner = ex?.InnerException;
            int innerLevel = 1;
            while (currentInner != null)
            {
                detailedLog += $"\n--- INNER EXCEPTION (Level {innerLevel}) ---\n";
                detailedLog += $"Type: {currentInner.GetType().FullName}\n";
                detailedLog += $"Message: {currentInner.Message}\n";
                detailedLog += $"StackTrace:\n{currentInner.StackTrace ?? "NO STACK TRACE"}\n";
                currentInner = currentInner.InnerException;
                innerLevel++;
            }
            
            // Sistem bilgileri
            detailedLog += $"\n--- SYSTEM INFO ---\n";
            detailedLog += $"OS: {Environment.OSVersion}\n";
            detailedLog += $".NET Runtime: {Environment.Version}\n";
            detailedLog += $"Processors: {Environment.ProcessorCount}\n";
            detailedLog += $"64-bit OS: {Environment.Is64BitOperatingSystem}\n";
            
            AppLogger.Critical(detailedLog, ex);
            Trace.Flush();
            
            // Kullanıcıya gösterilecek mesaj
            string errorMessage = $"Hata: {ex?.Message}\nTür: {ex?.GetType().Name}";
            if (ex?.InnerException != null)
            {
                errorMessage += $"\nİç Hata: {ex.InnerException.Message}";
            }
            if (isXamlError)
            {
                errorMessage += "\n\n[NOT: XAML parse hatası tespit edildi]";
            }
            
            // Show alert even if window is not ready
            MessageBox(IntPtr.Zero, 
                $"Uygulama beklenmedik bir hata nedeniyle çökebilir.\n\n{errorMessage}\n\nDetaylar log dosyasına kaydedildi.", 
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

        [LibraryImport("kernel32.dll", EntryPoint = "SetDllDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial bool SetDllDirectory(string lpPathName);

        [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr LoadLibrary(string lpFileName);

        [LibraryImport("kernel32.dll")]
        private static partial uint GetLastError();

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try {
                // #region agent log
                DebugNdjson("App.xaml.cs:OnLaunched", "OnLaunched begin", null, "boot");
                // #endregion
                AppLogger.Info("[App] OnLaunched: Creating MainWindow...");
                MainWindow = new MainWindow();
                AppLogger.Info("[App] OnLaunched: MainWindow created.");

                // Enable System-Wide Premium Audio (Cinematic Clicks)
                // #region agent log
                DebugNdjson("App.xaml.cs:OnLaunched", "setting ElementSoundPlayer", null, "H-Sound");
                // #endregion
                ElementSoundPlayer.State = ElementSoundPlayerState.On;
                ElementSoundPlayer.SpatialAudioMode = ElementSpatialAudioMode.On;
                // #region agent log
                DebugNdjson("App.xaml.cs:OnLaunched", "ElementSoundPlayer ok", null, "H-Sound");
                // #endregion

                AppLogger.Info("[App] OnLaunched: Activating MainWindow...");
                MainWindow.Activate();
                AppLogger.Info("[App] OnLaunched: MainWindow activated.");
                // #region agent log
                DebugNdjson("App.xaml.cs:OnLaunched", "OnLaunched returned normally", null, "boot");
                // #endregion
            } catch (Exception ex) {
                HandleFatalException(ex, "App.OnLaunched");
            }
        }

    }
}
