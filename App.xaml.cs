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
                // ÖNEMLİ: e.Exception.ToString() bize tam yığın izini (Stack Trace) verir.
                // Bu, hatanın tam olarak hangi satırda oluştuğunu gösterir.
                string errorMessage = $@"
=================================================================================
[UNHANDLED EXCEPTION] - {DateTime.Now}
=================================================================================
Hata Mesajı: {e.Message}

Hata Tipi: {e.Exception?.GetType().FullName ?? "Bilinmiyor"}

Stack Trace:
{e.Exception?.ToString() ?? "Stack trace bilgisi yok."}
=================================================================================";

                // Hatayı Visual Studio'nun "Output" (Çıktı) penceresine yazdır
                System.Diagnostics.Debug.WriteLine(errorMessage);

                // Hata ayıklayıcı (debugger) bağlıysa, logu yazdıktan sonra dursun.
                if (global::System.Diagnostics.Debugger.IsAttached)
                {
                    global::System.Diagnostics.Debugger.Break();
                }
            };
        }

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
