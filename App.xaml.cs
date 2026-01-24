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

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ModernIPTVPlayer
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        public static LoginParams? CurrentLogin { get; set; }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int MessageBox(IntPtr hWnd, String text, String caption, uint type);

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();

            // ESKİ KODUNUZU (Sadece Debugger.Break() yapan) YORUMA ALIN VEYA SİLİN:
            // UnhandledException += (sender, e) =>
            // {
            //     if (global::System.Diagnostics.Debugger.IsAttached) global::System.Diagnostics.Debugger.Break();
            // };

            // YENİ, DETAYLI LOGLAMA YAPAN KODU EKLEYİN:
            UnhandledException += (sender, e) =>
            {
                string errorMessage = $@"
=================================================================================
[UNHANDLED EXCEPTION] - {DateTime.Now}
=================================================================================
Hata Mesajı: {e.Message}

Hata Tipi: {e.Exception?.GetType().FullName ?? "Bilinmiyor"}

Stack Trace:
{e.Exception?.ToString() ?? "Stack trace bilgisi yok."}
=================================================================================";

                // Hatayı hem konsola hem de bir dosyaya yazdır
                System.Diagnostics.Debug.WriteLine(errorMessage);

                try 
                {
                    var logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ModernIPTV_Crash.log");
                    System.IO.File.WriteAllText(logPath, errorMessage);
                }
                catch { }

                // KULLANICIYA GÖSTER: Native MessageBox (çünkü WinUI penceresi henüz hazır olmayabilir)
                MessageBox(IntPtr.Zero, errorMessage, "KRİTİK HATA (Modern IPTV Player)", 0x10);

                // Hata ayıklayıcı (debugger) bağlıysa, dur
                if (global::System.Diagnostics.Debugger.IsAttached)
                {
                    global::System.Diagnostics.Debugger.Break();
                }
            };

            // DLL LOADING LOGIC: libmpv-2.dll ve bağımlılıklarını açıkça yükle
            try
            {
                string baseDir = AppContext.BaseDirectory;
                SetDllDirectory(baseDir); // Bağımlılık araması için dizini ayarla

                string mpvPath = System.IO.Path.Combine(baseDir, "libmpv-2.dll");
                string pthreadPath = System.IO.Path.Combine(baseDir, "libwinpthread-1.dll");
                string unwindPath = System.IO.Path.Combine(baseDir, "libunwind.dll");
                
                string debugInfo = $"BaseDir: {baseDir}\n";
                if (File.Exists(pthreadPath)) {
                    var h1 = LoadLibrary(pthreadPath);
                    debugInfo += $"libwinpthread-1.dll: {(h1 != IntPtr.Zero ? "OK" : "FAIL")}\n";
                }
                if (File.Exists(unwindPath)) {
                    var h2 = LoadLibrary(unwindPath);
                    debugInfo += $"libunwind.dll: {(h2 != IntPtr.Zero ? "OK" : "FAIL")}\n";
                }

                // Ana DLL'i yükle
                if (File.Exists(mpvPath))
                {
                    var hMpv = LoadLibrary(mpvPath);
                    if (hMpv == IntPtr.Zero)
                    {
                        uint lastError = GetLastError();
                        debugInfo += $"libmpv-2.dll: FAIL (Error: {lastError})\n";
                        MessageBox(IntPtr.Zero, debugInfo, "DLL Yükleme Hatası", 0x10);
                    }
                    else
                    {
                        debugInfo += $"libmpv-2.dll: SUCCESS (Handle: {hMpv})\n";
                        // Başarılıysa kullanıcıyı yormayalım, sadece logla
                        Debug.WriteLine(debugInfo);
                    }
                }
                else
                {
                    debugInfo += $"libmpv-2.dll: NOT FOUND at {mpvPath}\n";
                    MessageBox(IntPtr.Zero, debugInfo, "DLL Bulunamadı", 0x10);
                }
            }
            catch (Exception ex)
            {
                MessageBox(IntPtr.Zero, $"Kritik Hata: {ex.Message}", "DLL Load Exception", 0x10);
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
            _window = new MainWindow();
            _window.Activate();
        }
    }
}
