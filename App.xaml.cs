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
        public static string LastMediaInfoAction { get; set; } = "None";
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
        private static Exception? _lastHighSignalFirstChance;
        private static int _firstChanceCount;

        internal static Dictionary<string, object?> CreateCrashContext(string? scope = null)
        {
            var data = new Dictionary<string, object?>
            {
                ["scope"] = scope,
                ["lastMediaInfoAction"] = LastMediaInfoAction,
                ["baseDirectory"] = AppContext.BaseDirectory,
                ["isDynamicCodeSupported"] = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
                ["isDebuggerAttached"] = Debugger.IsAttached
            };

            try
            {
                var package = Package.Current;
                data["packageFullName"] = package.Id.FullName;
                data["packageVersion"] = $"{package.Id.Version.Major}.{package.Id.Version.Minor}.{package.Id.Version.Build}.{package.Id.Version.Revision}";
                data["packageInstalledLocation"] = package.InstalledLocation?.Path;
            }
            catch (Exception ex)
            {
                data["packageContextError"] = $"{ex.GetType().FullName} 0x{ex.HResult:X8}: {ex.Message}";
            }

            return data;
        }

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

        // #region agent log
        private static readonly object _debugSessionNdjsonLock = new object();
        private static readonly string _debugSessionNdjsonPath = @"C:\Users\ASUS\Documents\ModernIPTVPlayer\debug-df5b0b.log";

        internal static void DebugSessionNdjson(string location, string message, IDictionary<string, object?>? data, string hypothesisId, string runId = "pre-fix")
        {
            try
            {
                var sb = new System.Text.StringBuilder(256);
                sb.Append('{');
                sb.Append("\"sessionId\":\"df5b0b\",");
                sb.Append("\"runId\":\"").Append(JsonEscape(runId)).Append("\",");
                sb.Append("\"hypothesisId\":\"").Append(JsonEscape(hypothesisId)).Append("\",");
                sb.Append("\"timestamp\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Append(',');
                sb.Append("\"tid\":").Append(Environment.CurrentManagedThreadId).Append(',');
                sb.Append("\"location\":\"").Append(JsonEscape(location)).Append("\",");
                sb.Append("\"message\":\"").Append(JsonEscape(message)).Append("\"");
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
                        else if (kv.Value is double d) sb.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                lock (_debugSessionNdjsonLock)
                {
                    File.AppendAllText(_debugSessionNdjsonPath, sb.ToString());
                }
            }
            catch { /* instrumentation must never throw */ }
        }
        // #endregion

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
            int n = System.Threading.Interlocked.Increment(ref _firstChanceCount);
            var type = ex.GetType().FullName ?? "";
            bool isCancellation = ex is OperationCanceledException;
            bool isHighSignal =
                ex is COMException ||
                ex is InvalidCastException ||
                type.StartsWith("MessagePack.", StringComparison.Ordinal) ||
                ex.Message.Contains("Package ", StringComparison.OrdinalIgnoreCase) ||
                ex.HResult == unchecked((int)0x80004002) ||
                ex.HResult == unchecked((int)0x80070490);

            if (!isCancellation || isHighSignal)
            {
                _lastFirstChance = ex;
            }

            if (isHighSignal)
            {
                _lastHighSignalFirstChance = ex;
                // #region agent log
                DebugSessionNdjson("App.xaml.cs:FirstChance",
                    "High-signal first chance exception captured",
                    new Dictionary<string, object?>
                    {
                        ["n"] = n,
                        ["type"] = type,
                        ["hresult"] = ex.HResult,
                        ["message"] = ex.Message,
                        ["stack"] = ex.StackTrace,
                        ["environmentStack"] = Environment.StackTrace,
                        ["lastMediaInfoAction"] = LastMediaInfoAction
                    },
                    "H1-H5-H9");
                // #endregion
                if (ex is InvalidCastException || ex.HResult == unchecked((int)0x80004002))
                {
                    Debug.WriteLine($"[FIRST_CHANCE] InvalidCastException 0x80004002: {ex.Message}");
                    Debug.WriteLine($"[FIRST_CHANCE] StackTrace: {ex.StackTrace}");
                }
            }

            if (n > 50 && !isHighSignal) return;
            DebugNdjson("App.xaml.cs:FirstChance",
                "FirstChanceException captured",
                new Dictionary<string, object?>
                {
                    ["n"] = n,
                    ["highSignal"] = isHighSignal,
                    ["type"] = type,
                    ["hresult"] = ex.HResult,
                    ["message"] = ex.Message,
                    ["stack"] = ex.StackTrace,
                    ["lastMediaInfoAction"] = LastMediaInfoAction
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
            DebugNdjson("App.xaml.cs:ctor", "Crash context", CreateCrashContext("App.ctor"), "boot");
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
                // [COREMESSAGING_WORKAROUND] Suppress the known transient InvalidCastException
                // from WinUI internal CoreMessaging QI failure (0x80004002) during the first
                // layout of a freshly navigated page, and any cascading COMException.
                if (e.Exception is InvalidCastException ||
                    (e.Exception is COMException ce && 
                     (ce.HResult == unchecked((int)0x80004002) || ce.HResult == unchecked((int)0x8000FFFF))))
                {
                    // #region agent log
                    DebugSessionNdjson("App.xaml.cs:UnhandledException",
                        "Known CoreMessaging-related WinUI exception suppressed",
                        new Dictionary<string, object?>
                        {
                            ["type"] = e.Exception.GetType().FullName,
                            ["hresult"] = e.Exception.HResult,
                            ["message"] = e.Exception.Message,
                            ["stack"] = e.Exception.StackTrace
                        },
                        "H1");
                    // #endregion
                    e.Handled = true;
                    return;
                }
                // #region agent log
                DebugSessionNdjson("App.xaml.cs:UnhandledException",
                    "WinUI UnhandledException reached fatal path",
                    new Dictionary<string, object?>
                    {
                        ["type"] = e.Exception.GetType().FullName,
                        ["hresult"] = e.Exception.HResult,
                        ["message"] = e.Exception.Message,
                        ["stack"] = e.Exception.StackTrace
                    },
                    "H5");
                DebugNdjson("App.xaml.cs:UnhandledException",
                    "WinUI UnhandledException raised",
                    new Dictionary<string, object?>
                    {
                        ["type"] = e.Exception.GetType().FullName,
                        ["hresult"] = e.Exception.HResult,
                        ["message"] = e.Exception.Message,
                        ["stack"] = e.Exception.StackTrace,
                        ["context"] = string.Join("; ", CreateCrashContext("WinUI.UnhandledException").Select(kv => $"{kv.Key}={kv.Value}"))
                    },
                    "crash");
                // #endregion
                HandleFatalException(e.Exception, "WinUI UnhandledException");
            };

            // Non-UI Thread Exceptions
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                // #region agent log
                DebugSessionNdjson("App.xaml.cs:AppDomainUnhandledException",
                    "AppDomain unhandled exception reached fatal path",
                    new Dictionary<string, object?>
                    {
                        ["exceptionObject"] = e.ExceptionObject?.ToString(),
                        ["isTerminating"] = e.IsTerminating
                    },
                    "H5");
                // #endregion
                DebugNdjson("App.xaml.cs:AppDomainUnhandledException",
                    "AppDomain UnhandledException raised",
                    new Dictionary<string, object?>
                    {
                        ["exceptionObject"] = e.ExceptionObject?.ToString(),
                        ["isTerminating"] = e.IsTerminating
                    },
                    "crash");

                HandleFatalException(e.ExceptionObject as Exception, "AppDomain UnhandledException");
            };

            // Async Task Exceptions
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                DebugNdjson("App.xaml.cs:UnobservedTaskException",
                    "UnobservedTaskException raised",
                    new Dictionary<string, object?>
                    {
                        ["exception"] = e.Exception.ToString(),
                        ["observed"] = e.Observed
                    },
                    "crash");

                HandleFatalException(e.Exception, "UnobservedTaskException");
                e.SetObserved(); // Prevent crash
            };

            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                DebugNdjson("App.xaml.cs:ProcessExit", "ProcessExit raised", null, "crash");

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
            DebugNdjson("App.xaml.cs:HandleFatalException",
                "HandleFatalException enter",
                new Dictionary<string, object?>
                {
                    ["source"] = source,
                    ["type"] = ex?.GetType().FullName,
                    ["hresult"] = ex?.HResult,
                    ["message"] = ex?.Message,
                    ["stack"] = ex?.StackTrace,
                    ["inner"] = ex?.InnerException?.ToString(),
                    ["last_action"] = LastMediaInfoAction,
                    ["context"] = string.Join("; ", CreateCrashContext(source).Select(kv => $"{kv.Key}={kv.Value}"))
                },
                "crash");

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
            detailedLog += $"64-bit OS: {Environment.Is64BitOperatingSystem}\\n";
            detailedLog += $"Last MediaInfo Action: {LastMediaInfoAction}\\n";
            
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
            
            if (!string.Equals(source, "WinUI UnhandledException", StringComparison.Ordinal))
            {
                MessageBox(IntPtr.Zero,
                    $"Uygulama beklenmedik bir hata nedeniyle çökebilir.\n\n{errorMessage}\n\nDetaylar log dosyasına kaydedildi.",
                    "Kritik Hata (Modern IPTV Player)",
                    0x10);
            }

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
                // [DIAG] Log Microsoft.UI.Xaml.dll base address for crash analysis
                try
                {
                    foreach (System.Diagnostics.ProcessModule mod in System.Diagnostics.Process.GetCurrentProcess().Modules)
                    {
                        if (mod.ModuleName != null && mod.ModuleName.StartsWith("Microsoft.ui.xaml", StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.WriteLine($"[DIAG] Microsoft.UI.Xaml.dll base: 0x{mod.BaseAddress.ToString("X16")} | Size: 0x{mod.ModuleMemorySize:X}");
                            break;
                        }
                    }
                }
                catch { }
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

        private static string ResolveModuleName(IntPtr address)
        {
            try
            {
                IntPtr hModule;
                if (NativeMethods.GetModuleHandleEx(
                    NativeMethods.GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | NativeMethods.GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                    address, out hModule) != 0 && hModule != IntPtr.Zero)
                {
                    var sb = new System.Text.StringBuilder(260);
                    if (NativeMethods.GetModuleFileName(hModule, sb, sb.Capacity) > 0)
                    {
                        return System.IO.Path.GetFileName(sb.ToString());
                    }
                }
            }
            catch { }
            return "?";
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("kernel32.dll")]
            internal static extern ushort RtlCaptureStackBackTrace(
                uint framesToSkip,
                uint framesToCapture,
                [System.Runtime.InteropServices.Out] IntPtr[] backTrace,
                out uint backTraceHash);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
            internal static extern int GetModuleFileName(
                IntPtr hModule,
                System.Text.StringBuilder filename,
                int size);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            internal static extern int GetModuleHandleEx(
                uint dwFlags,
                IntPtr lpAddress,
                out IntPtr phModule);

            internal const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004;
            internal const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002;
        }
    }
}
