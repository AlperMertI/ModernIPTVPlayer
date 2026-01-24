using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MpvWinUI; 
using System;
using System.Globalization; 
using Windows.Media.Core;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Linq;
using System.Net.Http; // Added for Diagnostic Check
using System.Reflection;
using Windows.Foundation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;

namespace ModernIPTVPlayer
{
    public record PlayerNavigationArgs(string Url, string Title);

    public sealed partial class PlayerPage : Page
    {
        private MpvPlayer? _mpvPlayer;
        private bool _useMpvPlayer = true;
        private string _streamUrl = string.Empty;
        private bool _isPageLoaded = false;
        private string? _navigationError = null;
        private DispatcherTimer? _statsTimer;
        private Microsoft.UI.Xaml.DispatcherTimer _seekDebounceTimer; // Timer for cumulative seek
        private int _pendingSeekSeconds = 0; // Accumulated seconds to seek
        private bool _isDragging = false;
        private bool _isBehind = false;
        private DateTime _lastSeekTime = DateTime.MinValue;
        private bool _isFullScreen = false;
        private bool _isDraggingSpeed = false;
        private double _dragStartY = 0;
        private double _dragStartOffset = 0;
        private string _lastAppliedSpeed = "1.0";
        private bool _isSnapping = false;
        private bool _blockNextClick = false;
        private double _lastVisualUpdateOffset = -1;
        private double _lastDragOffset = -1;
        private bool _isStaticMetadataFetched = false;
        private string _cachedResolution = "-";
        private string _cachedFps = "-";
        private string _cachedCodec = "-";
        private string _cachedAudio = "-";
        private string _cachedColorspace = "-";
        private string _cachedHdr = "-";
        private int _nativeMonitorFps = 0;
        
        // Auto-Hide Logic
        private DispatcherTimer? _cursorTimer;
        private bool _controlsHidden = false;

        public PlayerPage()
        {
            this.InitializeComponent();
            this.Loaded += PlayerPage_Loaded;
            // Hide default back button since we have a custom one and pane is closed
            // But navigation service back requests still need handling in MainWindow

            _statsTimer = new DispatcherTimer();
            _statsTimer.Interval = TimeSpan.FromMilliseconds(500);

            _cursorTimer = new DispatcherTimer();
            _cursorTimer.Interval = TimeSpan.FromSeconds(3);
            _cursorTimer.Tick += CursorTimer_Tick;
            _statsTimer.Tick += StatsTimer_Tick;
            
            _seekDebounceTimer = new Microsoft.UI.Xaml.DispatcherTimer();
            _seekDebounceTimer.Interval = TimeSpan.FromMilliseconds(500); // Wait 500ms after last click
            _seekDebounceTimer.Tick += SeekDebounceTimer_Tick;

            // Register speed dragging handlers globally on SpeedOverlay to ensure capture even if buttons swallow events
            SpeedOverlay.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SpeedOverlay_PointerPressed), true);
            SpeedOverlay.AddHandler(PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SpeedOverlay_PointerMoved), true);
            SpeedOverlay.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SpeedOverlay_PointerReleased), true);
            SpeedOverlay.AddHandler(PointerExitedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SpeedOverlay_PointerReleased), true);
            SpeedOverlay.AddHandler(PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SpeedOverlay_PointerReleased), true);
            SpeedOverlay.AddHandler(PointerCanceledEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SpeedOverlay_PointerReleased), true);
        }



        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private async void StatsTimer_Tick(object? sender, object e)
        {
            if (_mpvPlayer == null || !_isPageLoaded) return;

            try 
            {
                // Sync Play/Pause State
                bool isPaused = await _mpvPlayer.GetPropertyBoolAsync("pause");
                PlayPauseIcon.Glyph = isPaused ? "\uE768" : "\uE769";

                // ---------- SEEKBAR & TIME LOGIC ----------
                string durationStr = await _mpvPlayer.GetPropertyAsync("duration");
                string positionStr = await _mpvPlayer.GetPropertyAsync("time-pos");
                
                double.TryParse(durationStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double duration);
                double.TryParse(positionStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double position);

                var seekable = await _mpvPlayer.GetPropertyAsync("seekable");
                // Fix: Default to isSeekable = true unless explicitly "no". 
                // This prevents "Live" UI from appearing during loading/buffering.
                bool isSeekable = (seekable != "no");

                // Disable Seek controls for linear streams (Live) to prevent freezing
                RewindButton.IsEnabled = isSeekable;
                FastForwardButton.IsEnabled = isSeekable;
                SeekSlider.IsEnabled = isSeekable;
                RewindButton.Opacity = isSeekable ? 1.0 : 0.5;
                FastForwardButton.Opacity = isSeekable ? 1.0 : 0.5;

                // Removed redundant "Live Detection Logic" block (lines 87-102) to avoid flickering
                // and rely on the robust "isLikelyLive" check below.
                
                string fileFormat = await _mpvPlayer.GetPropertyAsync("file-format");
                
                bool isMpegTs = (fileFormat == "mpegts");

                // Heuristic: Live if explicitly not seekable, 
                // OR mpegts (IPTV) with short duration (e.g. < 10 mins treated as rolling buffer).
                bool isLikelyLive = (!isSeekable) 
                                    || (isMpegTs && duration > 0 && duration < 600); 

                if (!isLikelyLive)
                {
                    // VOD MODE
                    LiveButton.Visibility = Visibility.Collapsed;
                    TimeTextBlock.Visibility = Visibility.Visible;
                    SeekSlider.Visibility = Visibility.Visible;
                    SeekSlider.IsEnabled = true;

                    // Only update slider if user is completely hands-off
                    if (!_isDragging && (DateTime.Now - _lastSeekTime).TotalSeconds > 1.5)
                    {
                        SeekSlider.Maximum = duration;
                        SeekSlider.Value = position;
                    }
                    
                    TimeSpan tPos = TimeSpan.FromSeconds(position);
                    TimeSpan tDur = TimeSpan.FromSeconds(duration);
                    TimeTextBlock.Text = tDur.TotalHours >= 1 
                        ? $"{tPos:hh\\:mm\\:ss} / {tDur:hh\\:mm\\:ss}"
                        : $"{tPos:mm\\:ss} / {tDur:mm\\:ss}";
                }
                else
                {
                    // LIVE MODE
                    LiveButton.Visibility = Visibility.Visible;
                    TimeTextBlock.Visibility = Visibility.Collapsed;
                    SeekSlider.Visibility = Visibility.Collapsed;
                    
                    if (isPaused || _isBehind)
                    {
                        LiveIndicator.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
                        LiveText.Text = "CANLI (GERİDE)";
                    }
                    else
                    {
                        LiveIndicator.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
                        LiveText.Text = "CANLI";
                    }
                }

                // ---------- STATS UPDATES (Conditional & Cached) ----------
                // ---------- STATS UPDATES (Conditional & Cached) ----------
                
                // 1. Static Metadata (Fetched once automatically on start)
                if (!_isStaticMetadataFetched)
                {
                    // Resolution consolidation
                    var wSize = await _mpvPlayer.GetPropertyAsync("video-params/w");
                    var hSize = await _mpvPlayer.GetPropertyAsync("video-params/h");
                    // Only proceed if we actually have valid metadata (wSize not empty)
                    if (!string.IsNullOrEmpty(wSize) && wSize != "N/A")
                    {
                        if (string.IsNullOrEmpty(wSize) || wSize == "N/A") {
                            wSize = await _mpvPlayer.GetPropertyAsync("width");
                            hSize = await _mpvPlayer.GetPropertyAsync("height");
                        }
                        _cachedResolution = (!string.IsNullOrEmpty(wSize) && wSize != "N/A") ? $"{wSize}x{hSize}" : "-";

                        // FPS consolidation
                        var fpsValStr = await _mpvPlayer.GetPropertyAsync("estimated-fps");
                        if (string.IsNullOrEmpty(fpsValStr) || fpsValStr == "N/A") fpsValStr = await _mpvPlayer.GetPropertyAsync("container-fps");
                        if (double.TryParse(fpsValStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double fv))
                        {
                            _cachedFps = $"{fv:F2} fps";
                            if (_nativeMonitorFps > 0)
                            {
                                 _cachedFps += $" / {_nativeMonitorFps}Hz";
                            }
                        }
                        else
                        {
                            _cachedFps = "- fps";
                        }

                        // Codec (Video & Audio)
                        string rawCodec = await _mpvPlayer.GetPropertyAsync("video-codec");
                        _cachedCodec = GetShortCodecName(rawCodec);

                        string audioCodec = await _mpvPlayer.GetPropertyAsync("audio-codec");
                        string audioChannels = await _mpvPlayer.GetPropertyAsync("audio-params/hr-channels");
                        if (string.IsNullOrEmpty(audioChannels) || audioChannels == "N/A")
                            audioChannels = await _mpvPlayer.GetPropertyAsync("audio-out-params/channels");
                        
                        _cachedAudio = $"{GetShortCodecName(audioCodec).ToUpper()} ({audioChannels})";
                        
                        _cachedColorspace = await _mpvPlayer.GetPropertyAsync("video-params/colormatrix");
                        if (string.IsNullOrEmpty(_cachedColorspace) || _cachedColorspace == "N/A") _cachedColorspace = "-";

                        // HDR / Tone Mapping
                        string hdrStatus = await _mpvPlayer.GetPropertyAsync("video-out-params/sig-peak");
                        string gamma = await _mpvPlayer.GetPropertyAsync("video-out-params/gamma");
                        if (!string.IsNullOrEmpty(hdrStatus) && hdrStatus != "N/A" && double.TryParse(hdrStatus, NumberStyles.Any, CultureInfo.InvariantCulture, out double peak) && peak > 1.0)
                            _cachedHdr = $"HDR ({peak:F1} nits)";
                        else
                            _cachedHdr = $"SDR ({gamma})";

                        // Update Stats Overlay Texts (even if hidden, ready for show)
                        TxtResolution.Text = _cachedResolution;
                        TxtFps.Text = _cachedFps;
                        TxtCodec.Text = _cachedCodec.ToUpper();
                        TxtAudioCodec.Text = _cachedAudio;
                        TxtColorspace.Text = _cachedColorspace;
                        TxtHdr.Text = _cachedHdr;

                        _isStaticMetadataFetched = true;
                        
                        // Show Info Pills (Once)
                        ShowInfoPills();
                    }
                }

                if (StatsOverlay.Visibility == Visibility.Visible)
                {
                    // 2. Dynamic Metadata (Always poll when visible)
                    var bitrate = await _mpvPlayer.GetPropertyAsync("video-bitrate");
                    TxtBitrate.Text = FormatBitrate(bitrate);
                    
                    // Improved speed detection: Real-time network activity
                    // Probe showed 'cache-speed' is the working property for this environment
                    long speedVal = await GetPropertyLongSafe("cache-speed");
                    TxtSpeed.Text = FormatSpeedLong(speedVal);

                    var hwdec = await _mpvPlayer.GetPropertyAsync("hwdec-current");
                    TxtHardware.Text = (hwdec != "no" && !string.IsNullOrEmpty(hwdec)) ? hwdec.ToUpper() : "SOFTWARE";

                    // Drops & AV Sync
                    try 
                    {
                        // Some MPV versions use frame-drop-count directly for total drops
                        long decDrops = await GetPropertyLongSafe("frame-drop-count");
                        if (decDrops < 0) decDrops = await GetPropertyLongSafe("vo-drop-frame-count");
                        if (decDrops < 0) decDrops = await GetPropertyLongSafe("decoder-frame-drop-count");
                        
                        string avSync = await _mpvPlayer.GetPropertyAsync("avsync");
                        string buffDur = await _mpvPlayer.GetPropertyAsync("demuxer-cache-duration");

                        // Drops
                        TxtDroppedDecoder.Text = decDrops >= 0 ? $"{decDrops}" : "0";

                        // AV Sync
                        if (double.TryParse(avSync, NumberStyles.Any, CultureInfo.InvariantCulture, out double avVal))
                        {
                             TxtAvSync.Text = $"{avVal * 1000:F1} ms"; // Show in ms
                             TxtAvSync.Foreground = (Math.Abs(avVal) > 0.1) 
                                 ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange) 
                                 : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                        }
                        else
                        {
                             TxtAvSync.Text = "-";
                        }

                        // Buffer
                        if (double.TryParse(buffDur, NumberStyles.Any, CultureInfo.InvariantCulture, out double buffVal))
                        {
                             TxtBuffer.Text = $"{buffVal:F1}s";
                        }
                        else
                        {
                             TxtBuffer.Text = "0s";
                        }
                    }
                    catch (Exception)
                    {
                        TxtDroppedDecoder.Text = "-";
                        TxtAvSync.Text = "-";
                        TxtBuffer.Text = "-";
                    }
                }
            }
            catch { /* Ignore errors during polling */ }
        }


        private async Task<long> GetPropertyLongSafe(string name)
        {
            try
            {
                return await _mpvPlayer.GetPropertyLongAsync(name);
            }
            catch
            {
                return -1;
            }
        }

        private string FormatBitrate(string? bitrate)
        {
            if (string.IsNullOrEmpty(bitrate) || bitrate == "N/A") return "Calculating...";
            if (double.TryParse(bitrate, NumberStyles.Any, CultureInfo.InvariantCulture, out double brVal))
            {
                if (brVal > 1000000) return $"{brVal / 1000000:F1} Mbps";
                return $"{brVal / 1000:F0} kbps";
            }
            return "-";
        }

        private string FormatSpeedLong(long sVal)
        {
            if (sVal <= 0) return "0 KB/s";
            double mbps = (sVal * 8.0) / 1000000.0;
            if (sVal > 1024 * 1024) 
                return $"{(double)sVal / (1024 * 1024):F2} MB/s ({mbps:F1} Mbps)";
            return $"{(double)sVal / 1024:F0} KB/s ({mbps:F2} Mbps)";
        }

        private void ShowInfoPills()
        {
            if (InfoPillsStack == null) return;

            PillResolution.Text = _cachedResolution;
            PillFps.Text = _cachedFps;
            PillCodec.Text = _cachedCodec;

            if (InfoPillsStack.Visibility != Visibility.Visible)
            {
                 InfoPillsStack.Visibility = Visibility.Visible;
                 ShowInfoPillsAnim.Begin();
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is string url)
            {
                _streamUrl = url;
                VideoTitleText.Text = ""; // No title provided
            }
            else if (e.Parameter is PlayerNavigationArgs args)
            {
                _streamUrl = args.Url;
                VideoTitleText.Text = args.Title;
            }
            else
            {
                _navigationError = "Geçersiz yayın URL'si alındı.";
            }

            _navigationError = null;
        }
        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            // 1. Stop timer IMMEDIATELY
            _statsTimer?.Stop();
            StopCursorTimer();
            _seekDebounceTimer?.Stop();
            _isPageLoaded = false;

            if (_isFullScreen)
            {
                MainWindow.Current.SetFullScreen(false);
                _isFullScreen = false;
            }

            base.OnNavigatedFrom(e);

            // 2. Cleanup MPV carefully
            if (_mpvPlayer is not null)
            {
                try
                {
                    // If OnNavigatedFrom is called, the page is unloading.
                    // We must ensure we don't block the UI thread too long, 
                    // but we must also dispose MPV.
                    await _mpvPlayer.CleanupAsync();
                }
                catch (Exception) 
                {
                    // Swallow cleanup errors to prevent crash on exit
                }
                _mpvPlayer = null;
            }
            else
            {
                MediaFoundationPlayer.MediaPlayer.Pause();
                MediaFoundationPlayer.Source = null;
            }
        }

        private async Task ShowMessageDialog(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new ScrollViewer { Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap } },
                CloseButtonText = "Kapat",
                PrimaryButtonText = "Kopyala",
                XamlRoot = this.XamlRoot
            };

            dialog.PrimaryButtonClick += (s, e) =>
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(content);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                e.Cancel = true; 
                dialog.PrimaryButtonText = "Kopyalandı!";
            };

            await dialog.ShowAsync();
        }

        // Helper to extract ALL cookies from a CookieContainer (ignoring Domain restrictions)
        private static System.Net.CookieCollection GetAllCookies(System.Net.CookieContainer container)
        {
            var allCookies = new System.Net.CookieCollection();
            var domainTableField = container.GetType().GetRuntimeFields().FirstOrDefault(x => x.Name == "m_domainTable" || x.Name == "_domainTable");
            var domains = domainTableField?.GetValue(container) as System.Collections.IDictionary;

            if (domains != null)
            {
                foreach (var val in domains.Values)
                {
                    var type = val.GetType();
                    var flagsField = type.GetRuntimeFields().FirstOrDefault(x => x.Name == "m_list" || x.Name == "_list");
                    var cookieList = flagsField?.GetValue(val) as System.Collections.IDictionary;

                    if (cookieList != null)
                    {
                        foreach (System.Net.CookieCollection col in cookieList.Values)
                        {
                            allCookies.Add(col);
                        }
                    }
                }
            }
            return allCookies;
        }

        private async void PlayerPage_Loaded(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"PlayerPage_Loaded Triggered for {_streamUrl}");
            if (_isPageLoaded) return;
            _isPageLoaded = true;
            _isStaticMetadataFetched = false; // Reset cache for new video
            _cachedResolution = "-";
            _cachedFps = "-";
            _cachedCodec = "-";
            _cachedColorspace = "-";

            StartCursorTimer();

            if (!string.IsNullOrEmpty(_navigationError))
            {
                await ShowMessageDialog("Hata", _navigationError);
                _navigationError = null;
                return;
            }

            _mpvPlayer = this.VideoPlayer;

            if (_useMpvPlayer)
            {
                VideoPlayer.Visibility = Visibility.Visible;
                MediaFoundationPlayer.Visibility = Visibility.Collapsed;
                
                // Ensure native player is completely stopped
                MediaFoundationPlayer.Source = null;
                try { MediaFoundationPlayer.MediaPlayer?.Pause(); } catch {}

                try
                {
                    // 0. PRE-FLIGHT CHECK
                    ShowOsd("Bağlantı Kontrol Ediliyor...");
                    var checkResult = await CheckStreamUrlAsync(_streamUrl);

                    if (!checkResult.Success)
                    {
                        await ShowMessageDialog("Yayın Hatası", checkResult.ErrorMsg);
                        if (Frame.CanGoBack) Frame.GoBack();
                        return;
                    }

                    // Update URL with the cleaned version (e.g. port 80 removed)
                    _streamUrl = checkResult.Url;

                    // 1. MUST INITIALIZE PLAYER before setting any properties!
                    await VideoPlayer.InitializePlayerAsync();

                    // Standard Browser User-Agent
                    string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                    
                    // REFERER STRATEGY: 
                    // HttpClient (which works) DOES NOT send a Referer.
                    // MPV was sending specific referer which caused failure. 
                    // Disabling Referer to match HttpClient behavior.
                    /* 
                    string referer = "";
                    if (Uri.TryCreate(finalStreamUrl, UriKind.Absolute, out var uri))
                    {
                        referer = $"{uri.Scheme}://{uri.Authority}/";
                    }
                    */

                    // COOKIE SHARING FROM HTTPCLIENT TO MPV
                    string cookieHeader = "";
                    try
                    {
                        var targetUri = new Uri(_streamUrl);
                        // 1. Try strict URI matching first
                        var cookies = HttpHelper.CookieContainer.GetCookies(targetUri);
                        
                        // 2. If valid cookies found, use them. If not, Dump ALL cookies.
                        if (cookies.Count == 0)
                        {
                            System.Diagnostics.Debug.WriteLine("[MPV] GetCookies(uri) returned 0. Trying Reflection...");
                            cookies = GetAllCookies(HttpHelper.CookieContainer);
                        }

                        System.Diagnostics.Debug.WriteLine($"[MPV] Found {cookies.Count} cookies in container.");

                        foreach (System.Net.Cookie c in cookies)
                        {
                            // Filter logic: If we have many cookies (unlikely in this app), maybe filter?
                            // For now, send key ones.
                            cookieHeader += $"{c.Name}={c.Value}; ";
                        }
                    } 
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MPV] Cookie Error: {ex.Message}");
                    }

                    // Headers
                    // Removing Sec-Fetch-Mode as it might trigger strict server checks if unused
                    // Adding Accept-Language to match HttpClient exactly
                    string headers = $"Accept: */*\nConnection: keep-alive\nAccept-Language: en-US,en;q=0.9\n"; 
                    
                    if (!string.IsNullOrEmpty(cookieHeader))
                    {
                         headers += $"Cookie: {cookieHeader}\n";
                         System.Diagnostics.Debug.WriteLine($"[MPV] Added Cookies: {cookieHeader}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[MPV] No Cookies found for {_streamUrl}");
                    }

                    // Apply Properties
                    await _mpvPlayer.SetPropertyAsync("user-agent", userAgent);
                    
                    // await _mpvPlayer.SetPropertyAsync("referrer", referer); // DISABLED
                    await _mpvPlayer.SetPropertyAsync("http-header-fields", headers);

                    // ----------------------------------------------------------------------------------
                    // PERFORMANCE TUNING: HARDWARE ACCELERATION
                    // ----------------------------------------------------------------------------------
                    // Note: Explicitly setting 'vo' is removed as it interferes with MpvWinUI's render API.
                    await _mpvPlayer.SetPropertyAsync("hwdec", "auto-safe");   // Hardware decoding priority
                    
                    // CRITICAL: Ensure we actually LOAD the file now that headers are set.
                    await _mpvPlayer.OpenAsync(_streamUrl);
                    
                    // Force these properties again just in case
                    // await _mpvPlayer.SetPropertyAsync("ytdl", "no"); // Removed: ytdl not available in this MPV build

                    // ----------------------------------------------------------------------------------
                    // OPTİMİZASYONLAR: Ses ve Altyazı Takılmalarını Önleme & Akıllı RAM Yönetimi
                    // ----------------------------------------------------------------------------------
                    
                    await _mpvPlayer.SetPropertyAsync("cache", "yes");
                    await _mpvPlayer.SetPropertyAsync("cache-pause", "no");
                    // hr-seek can be slow on network streams, disabling for speed test
                    await _mpvPlayer.SetPropertyAsync("hr-seek", "no"); 
                    // Reduce cache size to prevent massive rebuffering on seek
                    await _mpvPlayer.SetPropertyAsync("demuxer-max-bytes", "150M");
                    await _mpvPlayer.SetPropertyAsync("demuxer-max-back-bytes", "50M");



                    // 3. Genel Performans Profili:
                    // Bazı ağır decode işlemlerini ve renk düzeltmelerini atlayarak
                    // seek ve track switch hızını ciddi oranda artırır.
                    await _mpvPlayer.SetPropertyAsync("profile", "fast");

                    // 4. Threading ve Rendering:
                    // 6. Network Seek Optimization:
                    // "mkv-subtitle-preroll" disabled to prevent backward seeking on sub load
                    await _mpvPlayer.SetPropertyAsync("demuxer-mkv-subtitle-preroll", "no");

                    // 6. Network Seek Optimizasyonu:
                    // "mkv-subtitle-preroll" videoyu geriye sarmaya çalıştığı için kapatıldı.
                    // Bu, 10 saniyelik "geriye sarma" donmasını ÇÖZER.
                    await _mpvPlayer.SetPropertyAsync("demuxer-mkv-subtitle-preroll", "no");

                    // "subs-with-video" özelliği bazı libmpv sürümlerinde runtime property olarak 
                    // desteklenmediği için kaldırıldı.

                    // Daha ileriye dönük önbellekleme (20 saniye)
                    await _mpvPlayer.SetPropertyAsync("demuxer-readahead-secs", "20");
                    // ----------------------------------------------------------------------------------
                    
                    // Altyazı gecikmesi ve stil sorunları için ek ayarlar
                    await _mpvPlayer.SetPropertyAsync("sub-ass-shaper", "simple");
                    await _mpvPlayer.SetPropertyAsync("sub-scale-with-window", "yes");
                    await _mpvPlayer.SetPropertyAsync("sub-use-margins", "no");

                    // Detect Physical Refresh Rate (Native override for MPV)
                    try
                    {
                         // Use GetForegroundWindow as a robust fallback since we are the active app
                         var ptr = GetForegroundWindow();
                         var monitor = MonitorFromWindow(ptr, MONITOR_DEFAULTTONEAREST);
                            
                         var devMode = new DEVMODE();
                         devMode.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf(typeof(DEVMODE));
                         if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref devMode))
                         {
                             if (devMode.dmDisplayFrequency > 0)
                             {
                                 System.Diagnostics.Debug.WriteLine($"[PlayerPage] Native Display Frequency: {devMode.dmDisplayFrequency}Hz");
                                 _mpvPlayer?.SetDisplayFps(devMode.dmDisplayFrequency);
                             }
                         }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PlayerPage] Failed to get native refresh rate: {ex.Message}");
                    }

                    await _mpvPlayer.OpenAsync(_streamUrl);
                    _statsTimer?.Start();
                }
                catch (Exception ex)
                {
                    await ShowMessageDialog("MPV Oynatıcı Hatası", $"MPV başlatılamadı. \n\nHata: {ex.Message}");
                }
            }
            else
            {
                VideoPlayer.Visibility = Visibility.Collapsed;
                MediaFoundationPlayer.Visibility = Visibility.Visible;
                try
                {
                    MediaFoundationPlayer.Source = MediaSource.CreateFromUri(new Uri(_streamUrl));
                }
                catch (Exception ex)
                {
                    await ShowMessageDialog("Oynatıcı Hatası", $"Video yüklenemedi. \n\nHata: {ex.Message}");
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);
        private const int ENUM_CURRENT_SETTINGS = -1;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct DEVMODE
        {
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        private void SeekSlider_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isDragging = true;
        }

        private async void SeekSlider_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            e.Handled = true;
            if (_mpvPlayer == null) return;
            _isDragging = false;
            _lastSeekTime = DateTime.Now;
            var val = SeekSlider.Value;
            await _mpvPlayer.ExecuteCommandAsync("seek", val.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
        }

        private void SeekSlider_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
             e.Handled = true;
             if (_mpvPlayer != null && SeekSlider.ActualWidth > 0)
             {
                 var pos = e.GetPosition(SeekSlider);
                 var ratio = pos.X / SeekSlider.ActualWidth;
                 SeekSlider.Value = ratio * SeekSlider.Maximum;
             }
        }

        private void InteractiveControl_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            e.Handled = true;
            _isGestureTarget = false;
        }

        private void InteractiveControl_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private async void LiveButton_Click(object sender, RoutedEventArgs e)
        {
             if (_mpvPlayer == null) return;

             // Check if seekable
             var seekable = await _mpvPlayer.GetPropertyAsync("seekable");
             
             if (seekable == "no")
             {
                 ShowOsd("YAYIN YENİDEN YÜKLENİYOR (LIVE)...");
                 await _mpvPlayer.OpenAsync(_streamUrl);
                 await _mpvPlayer.SetPropertyAsync("pause", "no"); // Force play
                 
                 // Force UI update
                 LiveIndicator.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
                 LiveText.Text = "CANLI";
                 return;
             }

             ShowOsd("CANLI YAYINA GİDİLİYOR...");
             
             // Resume just in case
             await _mpvPlayer.SetPropertyAsync("pause", "no");
             _isBehind = false;

             // Force explicit seek to end using Percent (Cleanest for HLS Live)
             ShowOsd("SEEKING TO LIVE...");
             await LogStatus("PRE-LIVE-SEEK");
             await _mpvPlayer.ExecuteCommandAsync("seek", "100", "absolute-percent+exact");
             await LogStatus("POST-LIVE-SEEK");

             // Force UI update
             LiveIndicator.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
             LiveText.Text = "CANLI";
        }

        private void ControlBar_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true; // Prevent triggering Page_DoubleTapped (Fullscreen toggle)
        }



        private async Task LogStatus(string context)
        {
             if (_mpvPlayer == null) return;
             var cacheDur = await _mpvPlayer.GetPropertyAsync("demuxer-cache-duration");
             var paused = await _mpvPlayer.GetPropertyAsync("core-idle"); // is core waiting?
             var buffering = await _mpvPlayer.GetPropertyAsync("paused-for-cache");
             var time = await _mpvPlayer.GetPropertyAsync("time-pos");
             
             var log = $"[{context}] Time:{time} Cache:{cacheDur} Idle:{paused} Buffering:{buffering}";
             System.Diagnostics.Debug.WriteLine(log);
        }

        private async void RewindButton_Click(object sender, RoutedEventArgs e)
        {
             if (_mpvPlayer == null || !RewindButton.IsEnabled) return;
             
             _pendingSeekSeconds -= 10;
             ShowOsd($"{(_pendingSeekSeconds > 0 ? "+" : "")}{_pendingSeekSeconds} SANİYE");
             
             _seekDebounceTimer.Stop(); // Reset timer
             _seekDebounceTimer.Start();
        }

        private async void FastForwardButton_Click(object sender, RoutedEventArgs e)
        {
             if (_mpvPlayer == null || !FastForwardButton.IsEnabled) return;
             
             _pendingSeekSeconds += 30;
             ShowOsd($"{(_pendingSeekSeconds > 0 ? "+" : "")}{_pendingSeekSeconds} SANİYE");
             
             _seekDebounceTimer.Stop(); // Reset timer
             _seekDebounceTimer.Start();
        }
        
        private async void SeekDebounceTimer_Tick(object? sender, object e)
        {
             _seekDebounceTimer.Stop();
             if (_pendingSeekSeconds == 0 || _mpvPlayer == null) return;

             try
             {
                 int seekVal = _pendingSeekSeconds;
                 _pendingSeekSeconds = 0; // Reset pending immediately

                 if (seekVal < 0) _isBehind = true;

                 ShowOsd($"GİDİLİYOR: {(seekVal > 0 ? "+" : "")}{seekVal}sn");
                 await LogStatus($"PRE-DEBOUNCE-SEEK({seekVal})");
                 await _mpvPlayer.ExecuteCommandAsync("seek", seekVal.ToString(), "relative");
                 await LogStatus($"POST-DEBOUNCE-SEEK({seekVal})");
             }
             catch { }
        }

        private void SpeedFlyout_Opening(object sender, object e)
        {
        }

        private void ToggleFullScreen()
        {
            _isFullScreen = !_isFullScreen;
            MainWindow.Current.SetFullScreen(_isFullScreen);
            FullScreenIcon.Glyph = _isFullScreen ? "\uE1D8" : "\uE1D9"; // E1D8=BackToWindow, E1D9=FullScreen
            ToolTipService.SetToolTip(FullScreenButton, _isFullScreen ? "Tam Ekrandan Çık" : "Tam Ekran");
        }

        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullScreen();
        }





        private async void CloseSpeedOverlay()
        {
            if (SpeedOverlay.Visibility == Visibility.Visible)
            {
                HideSpeedOverlayAnim.Begin();
                await Task.Delay(200); // Wait for anim
                SpeedOverlay.Visibility = Visibility.Collapsed;
                // Resume StatsTimer
                _statsTimer?.Start();
            }
        }

        private async void CloseVolumeOverlay()
        {
            if (VolumeOverlay.Visibility == Visibility.Visible)
            {
                HideVolumeOverlayAnim.Begin();
                await Task.Delay(200);
                VolumeOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private async void CloseTracksOverlay()
        {
            if (TracksOverlay.Visibility == Visibility.Visible)
            {
                HideTracksOverlayAnim.Begin();
                await Task.Delay(200);
                TracksOverlay.Visibility = Visibility.Collapsed;
            }
        }

        // ---------- SPEED PICKER LOGIC ----------
        private bool _ignoreSpeedApply = false; // Prevents applying speed during setup




        private async void SpeedMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_blockNextClick)
            {
                _blockNextClick = false;
                return;
            }
            
            // Support both MenuFlyoutItem and Button
            string? tag = null;
            if (sender is MenuFlyoutItem mItem) tag = mItem.Tag?.ToString();
            else if (sender is Button btn) tag = btn.Tag?.ToString();

            if (_mpvPlayer == null || tag == null) return;
            
            try
            {
                string speedVal = tag ?? "1.0";
                
                // Direct Click Selection
                // Scroll to the item and apply
                await _mpvPlayer.SetPropertyAsync("speed", speedVal);
                ShowOsd($"Hız: {speedVal}x");
                
                // Find index to scroll purely for visual sync
                int index = GetSpeedIndex(speedVal);
                if (index >= 0)
                {
                    _ignoreSpeedApply = true;
                    SpeedScrollViewer.ChangeView(null, index * 36.0, null);
                    // Resume StatsTimer because we effectively "chose" 
                    _statsTimer?.Start();
                    // Reset flag after animation
                    await Task.Delay(300);
                    _ignoreSpeedApply = false;
                }
            }
            catch { _statsTimer?.Start(); }
        }

        private Point _dragStartPoint;
        private bool _isDraggingSpeedActive = false;

        private void SpeedOverlay_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_mpvPlayer == null || SpeedOverlay.Visibility != Visibility.Visible) return;
            
            _isDraggingSpeedActive = false;
            _dragStartPoint = e.GetCurrentPoint(SpeedOverlay).Position;
            _dragStartOffset = SpeedScrollViewer.VerticalOffset;
        }

        private void SpeedOverlay_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (SpeedOverlay.Visibility != Visibility.Visible) return;

            var ptr = e.GetCurrentPoint(SpeedOverlay);
            var currentPoint = ptr.Position;

            if (!_isDraggingSpeedActive)
            {
                // ONLY start dragging if left button is actually pressed down
                if (ptr.Properties.IsLeftButtonPressed && Math.Abs(currentPoint.Y - _dragStartPoint.Y) > 8)
                {
                    _isDraggingSpeedActive = true;
                    SpeedOverlay.CapturePointer(e.Pointer);
                    e.Handled = true;
                }
            }
            else
            {
                // If button is released but Capture was lost or something, stop here
                if (!ptr.Properties.IsLeftButtonPressed)
                {
                    SpeedOverlay_PointerReleased(sender, e);
                    return;
                }

                e.Handled = true;
                double delta = _dragStartPoint.Y - currentPoint.Y;
                double newOffset = _dragStartOffset + delta;
                
                // Throttle: Only update if offset has changed by at least 0.5 pixels
                if (Math.Abs(newOffset - _lastDragOffset) > 0.5)
                {
                    _lastDragOffset = newOffset;
                    SpeedScrollViewer.ChangeView(null, newOffset, null, true);
                }
            }
        }

        private void SpeedOverlay_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isDraggingSpeedActive)
            {
                _isDraggingSpeedActive = false;
                _blockNextClick = true; // Prevents the button Click event that follows release
                SpeedOverlay.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
                
                // Trigger a final snap check manually since ViewChanged might not fire 
                // for the very last movement if it was small
                DispatcherQueue.TryEnqueue(() => { 
                    SpeedScrollViewer_ViewChanged(SpeedScrollViewer, new ScrollViewerViewChangedEventArgs());
                });
            }
        }

        private Button? GetClosestSpeedItem(out double targetOffset)
        {
            targetOffset = 0;
            if (SpeedListStack == null || SpeedScrollViewer == null) return null;

            double currentOffset = SpeedScrollViewer.VerticalOffset;
            int index = (int)Math.Round(currentOffset / 36.0);
            
            // Clamp index to valid children
            index = Math.Max(0, Math.Min(index, SpeedListStack.Children.Count - 1));
            
            targetOffset = index * 36.0;
            return SpeedListStack.Children[index] as Button;
        }

        private void SpeedScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            // Note: Visual updates are now handled by Composition API on the GPU.
            // We only need to check for snapping/application here.
            
            // Handle the end of a scroll or snap operation
            bool isEnd = e == null || !e.IsIntermediate;

            if (isEnd && !_isDraggingSpeedActive)
            {
                 var closest = GetClosestSpeedItem(out double snapOffset);
                 double currentOffset = SpeedScrollViewer.VerticalOffset;

                 if (Math.Abs(snapOffset - currentOffset) > 0.5) 
                 {
                     // Not yet aligned, start or continue snapping
                     if (!_isSnapping)
                     {
                         _isSnapping = true;
                         SpeedScrollViewer.ChangeView(null, snapOffset, null, false);
                     }
                 }
                 else
                 {
                     // Aligned at snap point
                     _isSnapping = false;
                     if (!_ignoreSpeedApply)
                     {
                         ApplyCenteredSpeed();
                     }
                 }
            }
        }

        private async void SpeedButton_Click(object sender, RoutedEventArgs e)
        {
            if (SpeedOverlay.Visibility == Visibility.Visible)
            {
                 CloseSpeedOverlay();
            }
            else
            {
                // Ensure other overlays are closed
                CloseVolumeOverlay();
                CloseTracksOverlay();

                // 1. Prepare Overlay but keep Hidden potentially? 
                //    Or just accept 1 frame glitch if Layout not ready.
                //    Better: Set Visibility, Force Layout Update or simply wait for next tick properly.
                
                SpeedOverlay.Visibility = Visibility.Visible; // Must be visible for ScrollViewer to have extent
                SpeedOverlay.Opacity = 0; // Hide visually until scrolled
                
                // Initialize Scroll Position based on current MPV speed
                if (_mpvPlayer != null)
                {
                    try 
                    {
                        var currentSpeed = await _mpvPlayer.GetPropertyAsync("speed");
                        if (string.IsNullOrEmpty(currentSpeed)) currentSpeed = "1.0";
                        
                        // Parse double
                        if (Double.TryParse(currentSpeed, NumberStyles.Any, CultureInfo.InvariantCulture, out double sVal))
                        {
                            // Map to nearest tag
                            // 2.0, 1.5, 1.25, 1.0, 0.75, 0.5
                            string targetTag = "1.0";
                            double minDiff = double.MaxValue;
                            
                            // Iterate children to find closest tag match dynamically if possible, or use hardcoded map
                            // Hardcoded map is faster for now
                            double[] speeds = { 2.0, 1.5, 1.25, 1.0, 0.75, 0.5 };
                            foreach(var s in speeds) {
                                double diff = Math.Abs(sVal - s);
                                if (diff < minDiff) { minDiff = diff; targetTag = s.ToString("0.0#", CultureInfo.InvariantCulture); }
                            }
                            
                            int index = GetSpeedIndex(targetTag);
                            _lastAppliedSpeed = targetTag;
                            _ignoreSpeedApply = true;
                            
                            // Reset optimization flags
                            _lastVisualUpdateOffset = -1;
                            _lastDragOffset = -1;
                            
                            // Suspend background timer to give 100% CPU/UI-thread to interaction
                            _statsTimer?.Stop();

                            SpeedOverlay.UpdateLayout(); // Ensure layout is ready
                            
                            // Use Dispatcher to ensure Visibility change is processed and ScrollViewer has extent
                            DispatcherQueue.TryEnqueue(async () => {
                                 await Task.Delay(50); // Small buffer for layout
                                 SpeedScrollViewer.ChangeView(null, index * 36.0, null, true);
                                 UpdateSpeedPickerVisuals(); 

                                 SpeedOverlay.Opacity = 1; 
                                 ShowSpeedOverlayAnim.Begin();

                                 await Task.Delay(150); 
                                 _ignoreSpeedApply = false;
                            });
                        }
                        else
                        {
                            // Fallback if parse fails
                             SpeedOverlay.Opacity = 1; 
                             ShowSpeedOverlayAnim.Begin();
                        }
                    } 
                    catch 
                    {
                         SpeedOverlay.Opacity = 1; 
                         ShowSpeedOverlayAnim.Begin();
                    }
                }
                else
                {
                     SpeedOverlay.Opacity = 1; 
                     ShowSpeedOverlayAnim.Begin();
                }
            }
        }

        private void SpeedListStack_Loaded(object sender, RoutedEventArgs e)
        {
            if (SpeedListStack == null || SpeedScrollViewer == null) return;

            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            var scrollerPropertySet = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(SpeedScrollViewer);
            
            // Distance formula: abs(buttonCenterY + scrollTranslation.Y - viewportCenter)
            // Scroll translation Y is negative in WinUI 3 manipulation property set
            
            double currentY = 57; // Padding top
            foreach (var child in SpeedListStack.Children)
            {
                if (child is Button btn)
                {
                    var visual = ElementCompositionPreview.GetElementVisual(btn);
                    // Use fixed sizes based on XAML for better performance 
                    visual.Size = new Vector2(80, 36);
                    visual.CenterPoint = new Vector3(40, 18, 0);

                    double btnCenterY = currentY + 18;
                    
                    // Scale Animation
                    var scaleExp = compositor.CreateExpressionAnimation(
                        "1.1 - clamp((abs(btnCenterY + scroller.Translation.Y - 75) - 18) / 36, 0, 1) * 0.25");
                    scaleExp.SetScalarParameter("btnCenterY", (float)btnCenterY);
                    scaleExp.SetReferenceParameter("scroller", scrollerPropertySet);
                    visual.StartAnimation("Scale.X", scaleExp);
                    visual.StartAnimation("Scale.Y", scaleExp);

                    // Opacity Animation
                    var opacityExp = compositor.CreateExpressionAnimation(
                        "1.0 - clamp((abs(btnCenterY + scroller.Translation.Y - 75) - 18) / 36, 0, 1) * 0.75");
                    opacityExp.SetScalarParameter("btnCenterY", (float)btnCenterY);
                    opacityExp.SetReferenceParameter("scroller", scrollerPropertySet);
                    visual.StartAnimation("Opacity", opacityExp);

                    currentY += btn.ActualHeight;
                }
            }
        }

        private void UpdateSpeedPickerVisuals(bool force = false)
        {
            // Composition API now handles Scale and Opacity on the GPU.
            // This method is kept for any UI-thread specific updates if needed in future (e.g. FontWeight)
        }

        private int GetSpeedIndex(string speed)
        {
             // Robust search
             int i = 0;
             foreach(var child in SpeedListStack.Children)
             {
                  if (child is Button btn) 
                  {
                      string? t = btn.Tag?.ToString();
                      if (t == speed) return i;
                     
                     // Try loose match (e.g. "1" matches "1.0")
                     if (double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out double tVal) &&
                         double.TryParse(speed, NumberStyles.Any, CultureInfo.InvariantCulture, out double sVal))
                     {
                         if (Math.Abs(tVal - sVal) < 0.01) return i;
                     }
                 }
                 i++;
             }
             return 3; // Default to 1.0 (Index 3)
        }


        private async void ApplyCenteredSpeed()
        {
             // Apply only if valid
             if (SpeedListStack == null || _mpvPlayer == null || _ignoreSpeedApply) return;
             
             var closestBtn = GetClosestSpeedItem(out _);

             if (closestBtn != null && closestBtn.Tag != null)
             {
                 string speedVal = closestBtn.Tag.ToString() ?? "1.0";
                if (speedVal != _lastAppliedSpeed)
                {
                    _lastAppliedSpeed = speedVal;
                    await _mpvPlayer.SetPropertyAsync("speed", speedVal);
                    ShowOsd($"Hız: {speedVal}x");
                }
             }
        }






        private string[] _aspectRatios = { "-1", "16:9", "4:3", "2.35:1", "fill" };
        private string[] _aspectNames = { "Otomatik", "16:9", "4:3", "Sinema", "Ekranı Kapla" };
        private int _currentAspectIndex = 0;

        private async void AspectRatioButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mpvPlayer == null) return;

            _currentAspectIndex = (_currentAspectIndex + 1) % _aspectRatios.Length;
            string newAspect = _aspectRatios[_currentAspectIndex];
            string aspectName = _aspectNames[_currentAspectIndex];

            if (newAspect == "fill")
            {
                 // Stretch to fill (No crop, no black bars, distorts image)
                 await _mpvPlayer.SetPropertyAsync("keepaspect", "no");
            }
            else
            {
                 // Standard Aspect Ratio override
                 await _mpvPlayer.SetPropertyAsync("keepaspect", "yes");
                 await _mpvPlayer.SetPropertyAsync("panscan", "0.0"); 
                 await _mpvPlayer.SetPropertyAsync("video-aspect-override", newAspect);
            }
            
            ShowOsd($"Görüntü: {aspectName}");
        }

        // ---------- VOLUME CONTROL ----------
        private async void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_mpvPlayer == null) return;

            double val = e.NewValue;


            // Auto-unmute if dragging
            await _mpvPlayer.SetPropertyAsync("mute", "no");
            await _mpvPlayer.SetPropertyAsync("volume", val.ToString(CultureInfo.InvariantCulture));

            UpdateVolumeIcon(val, false);
        }

        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mpvPlayer == null) return;

            // Simple toggle logic
            // We assume Current State based on Icon for speed, or fetch property
            // Fetching property is safer.
            string muteState = await _mpvPlayer.GetPropertyAsync("mute");
            bool isMuted = (muteState == "yes");

            if (isMuted)
            {
                // Unmute
                await _mpvPlayer.SetPropertyAsync("mute", "no");
                UpdateVolumeIcon(VolumeSlider.Value, false);
            }
            else
            {
                // Mute
                await _mpvPlayer.SetPropertyAsync("mute", "yes");
                UpdateVolumeIcon(VolumeSlider.Value, true);
            }
        }

        private void UpdateVolumeIcon(double volume, bool isMuted)
        {
            // Main Button: Status Indicator (What is the current state?)
            if (isMuted || volume == 0)
            {
                VolumeIcon.Glyph = "\uE74F"; // Mute Cross
            }
            else if (volume < 33)
            {
                VolumeIcon.Glyph = "\uE993"; // Low
            }
            else if (volume < 66)
            {
                VolumeIcon.Glyph = "\uE994"; // Mid
            }
            else
            {
                VolumeIcon.Glyph = "\uE995"; // High
            }

            // Overlay Mute Button: Action Indicator (What will happen when clicked?)
            // If Muted -> Show Speaker (Unmute)
            // If Sound -> Show Cross (Mute)
            if (isMuted || volume == 0)
            {
                 MuteIcon.Glyph = "\uE767"; // Volume Icon (Speaker) -> Unmute
            }
            else
            {
                 MuteIcon.Glyph = "\uE74F"; // Mute Icon (Cross) -> Mute
            }
        }


        private void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (VolumeOverlay.Visibility == Visibility.Visible)
            {
                CloseVolumeOverlay();
            }
            else
            {
                // Ensure other overlays are closed
                CloseTracksOverlay();
                CloseSpeedOverlay();

                VolumeOverlay.Visibility = Visibility.Visible;
                ShowVolumeOverlayAnim.Begin();
            }
        }
        // ------------------------------------

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mpvPlayer == null) return;
            try
            {
                // Toggle Pause
                bool isPaused = await _mpvPlayer.GetPropertyBoolAsync("pause");
                bool newState = !isPaused;
                await _mpvPlayer.SetPropertyAsync("pause", newState ? "yes" : "no");
                
                // Update Icon: If paused(true) -> Show Play Icon, If playing(false) -> Show Pause Icon
                PlayPauseIcon.Glyph = newState ? "\uE768" : "\uE769";
            }
            catch { }
        }

        // ---------- GESTURE & POINTER LOGIC ----------
        private Point _pointerDownPosition;
        private bool _isPointerDragging;
        private double _originalVolume;
        private double _originalBrightness;
        private long _lastDoubleTapTime = 0;
        private const int DoubleTapThreshold = 300; // ms

        // Brightness: simulated via overlay opacity (0.0 to 0.7)
        // 0.0 = 100% brightness (Transparent overlay)
        // 0.7 = 30% brightness (Dark overlay)
        private double _currentBrightness = 1.0; 

        // Flag to tracking if the current pointer sequence belongs to a gesture
        private bool _isGestureTarget = false;

        private void MainGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var pos = e.GetCurrentPoint(MainGrid).Position;
            _pointerDownPosition = pos;
            _isPointerDragging = false;
            _isGestureTarget = true;

            // 1. Exclude Bottom Control Bar Area (Height ~110)
            // This prevents gestures when using buttons/sliders at the bottom
            if (pos.Y > MainGrid.ActualHeight - 110)
            {
                _isGestureTarget = false;
                return;
            }

            // 2. Filter out interactive controls (Robust Name & Type check)
            if (e.OriginalSource is FrameworkElement fe)
            {
                var parent = fe;
                while (parent != null && parent != MainGrid) 
                {
                    // Check by Type
                    if (parent is Button || parent is Slider || parent is Microsoft.UI.Xaml.Controls.Primitives.Thumb || parent is ToggleSwitch)
                    {
                        _isGestureTarget = false;
                        return;
                    }
                    
                    // Check by Name (Extra safety for specific controls)
                    if (parent.Name == "VolumeSlider" || parent.Name == "SeekSlider" || parent.Name == "VolumeOverlay" || parent.Name == "TracksOverlay" || parent.Name == "SpeedOverlay")
                    {
                         _isGestureTarget = false;
                         return;
                    }

                    parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent) as FrameworkElement;
                }
            }

            // 3. Click Outside Logic - Close Overlays if clicking on empty space
            bool overlayWasOpen = false;
            if (VolumeOverlay.Visibility == Visibility.Visible) { CloseVolumeOverlay(); overlayWasOpen = true; }
            if (TracksOverlay.Visibility == Visibility.Visible) { CloseTracksOverlay(); overlayWasOpen = true; }
            if (SpeedOverlay.Visibility == Visibility.Visible) { CloseSpeedOverlay(); overlayWasOpen = true; }
            
            if (overlayWasOpen)
            {
                 _isGestureTarget = false; // Don't start a gesture if we just closed a menu
                 return;
            }
            
            // Capture initial values for drag delta
            if (_mpvPlayer != null && _isGestureTarget)
            {
                 // Volume is 0-100
                 _originalVolume = VolumeSlider.Value;
                 // Brightness is internal
                 _originalBrightness = _currentBrightness;
            }
        }

        private void MainGrid_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // ALWAYS reset timer on any movement to show controls
            ResetCursorTimer();

            if (!e.Pointer.IsInContact || !_isGestureTarget) return;

            var currentPoint = e.GetCurrentPoint(MainGrid).Position;
            
            var deltaY = _pointerDownPosition.Y - currentPoint.Y; // Up is positive
            
            if (Math.Abs(deltaY) > 20)
            {
                _isPointerDragging = true;
                double scaledDelta = deltaY / 5.0; // Sensitivity factor

                // Determine Zone: Left (Brightness) vs Right (Volume)
                bool isRightSide = _pointerDownPosition.X > MainGrid.ActualWidth / 2;

                if (isRightSide)
                {
                    // Update Volume
                    double newVol = Math.Clamp(_originalVolume + scaledDelta, 0, 100);
                    _mpvPlayer?.SetPropertyAsync("volume", newVol.ToString(CultureInfo.InvariantCulture));
                    VolumeSlider.Value = newVol; // Update UI logic
                    ShowOsd($"SES: {(int)newVol}");
                }
                else
                {
                    // Update Brightness (0.0 to 1.0)
                    // Drag Up (+delta) -> Increase Brightness (Decrease Opacity)
                    // scaledDelta is roughly -50 to +50
                    double change = scaledDelta / 100.0; 
                    double newBright = Math.Clamp(_originalBrightness + change, 0.1, 1.0);
                    
                    SetBrightness(newBright);
                    ShowOsd($"PARLAKLIK: {(int)(newBright * 100)}");
                }
            }
        }

        private void SetBrightness(double brightness)
        {
            _currentBrightness = brightness;
            // brightness 1.0 -> Opacity 0.0
            // brightness 0.1 -> Opacity 0.7 (max darkness)
            double maxDarkness = 0.7;
            double opacity = (1.0 - brightness) * (maxDarkness / 0.9);
            BrightnessOverlay.Opacity = Math.Clamp(opacity, 0, maxDarkness);
        }

        private void MainGrid_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
             _isPointerDragging = false;
             ResetCursorTimer();
        }

        private void MainGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            HideControls();
        }

        // ---------- CURSOR & CONTROLS AUTO-HIDE LOGIC ----------





        private void StartCursorTimer()
        {
            ResetCursorTimer();
        }

        private void StopCursorTimer()
        {
            _cursorTimer?.Stop();
            SetControlsVisibility(Visibility.Visible);
        }

        private void ResetCursorTimer()
        {
            if (_cursorTimer == null) return;
            _cursorTimer.Stop();
            _cursorTimer.Start();
            
            if (_controlsHidden)
            {
                SetControlsVisibility(Visibility.Visible);
            }
        }

        private void CursorTimer_Tick(object? sender, object e)
        {
             HideControls();
        }

        private void HideControls()
        {
            if (!_controlsHidden)
            {
                SetControlsVisibility(Visibility.Collapsed);
            }
        }

        private void SetControlsVisibility(Visibility visibility)
        {
            if (ControlsBorder == null || BackButton == null) return;

            if (ControlsBorder.Visibility != visibility)
            {
                ControlsBorder.Visibility = visibility;
                BackButton.Visibility = visibility;
                VideoTitleText.Visibility = visibility;
                FullScreenButton.Visibility = visibility;

                if (visibility == Visibility.Visible)
                {
                    if (InfoPillsStack.Visibility != Visibility.Visible)
                    {
                        InfoPillsStack.Visibility = Visibility.Visible;
                        ShowInfoPillsAnim.Begin();
                    }
                }

                if (visibility == Visibility.Collapsed)
                {
                    CloseVolumeOverlay();
                    CloseTracksOverlay();
                    CloseSpeedOverlay();
                    if (StatsOverlay.Visibility == Visibility.Visible)
                    {
                         // Keep stats overlay visible
                    }
                    if (InfoPillsStack.Visibility == Visibility.Visible)
                    {
                         InfoPillsStack.Visibility = Visibility.Collapsed;
                    }
                }

                _controlsHidden = (visibility == Visibility.Collapsed);
            }
        }



        private string GetShortCodecName(string codec)
        {
            if (string.IsNullOrEmpty(codec) || codec == "N/A" || codec == "-") return "-";
            codec = codec.ToLower().Trim();

            if (codec.Contains("h264") || codec.Contains("avc")) return "H.264";
            if (codec.Contains("h265") || codec.Contains("hevc")) return "HEVC";
            if (codec.Contains("vp9")) return "VP9";
            if (codec.Contains("av1")) return "AV1";
            if (codec.Contains("mpeg2")) return "MPEG2";
            if (codec.Contains("mpeg4")) return "MPEG4";
            if (codec.Contains("aac")) return "AAC";
            if (codec.Contains("mp3")) return "MP3";
            
            // Return uppercase if no match found but it's short, else truncate
            if (codec.Length > 8) return codec.Substring(0, 8).ToUpper() + "..";
            return codec.ToUpper();
        }

        private void MainGrid_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(MainGrid).Properties;
            int delta = props.MouseWheelDelta;
            var pos = e.GetCurrentPoint(MainGrid).Position;
            
            bool isRightSide = pos.X > MainGrid.ActualWidth / 2;

            if (isRightSide)
            {
                // Volume
                double currentVol = VolumeSlider.Value;
                double change = delta > 0 ? 5 : -5;
                double newVol = Math.Clamp(currentVol + change, 0, 100);
                 VolumeSlider.Value = newVol; // Triggers property update
                 ShowOsd($"SES: {(int)newVol}");
            }
            else
            {
                // Brightness
                 double change = delta > 0 ? 0.05 : -0.05;
                 double newBright = Math.Clamp(_currentBrightness + change, 0.1, 1.0);
                 SetBrightness(newBright);
                 ShowOsd($"PARLAKLIK: {(int)(newBright * 100)}");
            }
        }

        // ---------- DOUBLE TAP LOGIC ----------
        private async void Page_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {


            var pos = e.GetPosition(MainGrid);
            double width = MainGrid.ActualWidth;
            
            // Zones:
            // 0 - 30% : Left (Rewind)
            // 30 - 70% : Center (Play/Pause or Fullscreen - User preference, defaulting to Fullscreen toggle here)
            // 70 - 100%: Right (Seek Forward)

            if (pos.X < width * 0.30)
            {
                // Seek Back 10s
                if (!RewindButton.IsEnabled || _mpvPlayer == null) return; // Not seekable
                 ShowOsd("-10 SN");
                 await _mpvPlayer.ExecuteCommandAsync("seek", "-10", "relative");
            }
            else if (pos.X > width * 0.70)
            {
                // Seek Fwd 30s
                if (!FastForwardButton.IsEnabled || _mpvPlayer == null) return;
                 ShowOsd("+30 SN");
                 await _mpvPlayer.ExecuteCommandAsync("seek", "30", "relative");
            }
            else
            {
                // Center -> Toggle Fullscreen
                 ToggleFullScreen();
            }
        }

        // ---------- ANIMATION HELPERS ----------
        private void AnimateButtonPress(Button btn)
        {
            // Simple scale animation could go here, 
            // but VisualStates are improved in XAML default styles usually.
            // For now, relies on standard button feedback.
        }

        // ---------- TRACKS TOGGLE LOGIC ----------


        private System.Threading.CancellationTokenSource? _osdCts;
        private void ShowOsd(string text)
        {
            _osdCts?.Cancel();
            _osdCts = new System.Threading.CancellationTokenSource();
            
            OsdText.Text = text;
            OsdOverlay.Visibility = Visibility.Visible;
            
            var token = _osdCts.Token;
            Task.Delay(2000, token).ContinueWith(t => 
            {
                 if (!t.IsCanceled)
                 {
                     DispatcherQueue.TryEnqueue(() => 
                     {
                         OsdOverlay.Visibility = Visibility.Collapsed;

                     });
                 }
            });
        }

        private void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (StatsOverlay.Visibility == Visibility.Collapsed)
            {
                StatsOverlay.Visibility = Visibility.Visible;
                // Force an immediate fetch of static metadata if not already done
                _isStaticMetadataFetched = false; 
                _isStaticMetadataFetched = false; 
                StatsTimer_Tick(this, new object());
            }
            else
            {
                StatsOverlay.Visibility = Visibility.Collapsed;
            }
        }

        // ---------- UNIFIED TRACKS OVERLAY LOGIC ----------

        private class TrackItem
        {
            public long Id { get; set; }
            public string Text { get; set; } = "";
            public bool IsSelected { get; set; }
            public string Type { get; set; } = "";
            public bool IsNone { get; set; }

            public override string ToString() => Text; // Fallback
        }

        private bool _isPopulatingTracks = false;





        private void CloseTracksButton_Click(object sender, RoutedEventArgs e)
        {
            HideTracksOverlayAnim.Begin();
            HideTracksOverlayAnim.Completed += (s, args) =>
            {
                TracksOverlay.Visibility = Visibility.Collapsed;
            };
        }



         private async void TracksButton_Click(object sender, RoutedEventArgs e)
         {
              if (TracksOverlay.Visibility == Visibility.Visible)
              {
                  HideTracksOverlayAnim.Begin();
                  await Task.Delay(200);
                  TracksOverlay.Visibility = Visibility.Collapsed;
              }
              else
              {
                  // Close others
                  CloseVolumeOverlay();
                  CloseSpeedOverlay();
                  
                  TracksOverlay.Visibility = Visibility.Visible;
                  ShowTracksOverlayAnim.Begin();
                  
                  if (!_isPopulatingTracks)
                  {
                      _isPopulatingTracks = true;
                      await PopulateTracksForOverlay();
                      _isPopulatingTracks = false;
                  }
              }
         }


        private async Task PopulateTracksForOverlay()
        {
            if (_mpvPlayer == null) return;

            var audioTracks = new List<TrackItem>();
            var subTracks = new List<TrackItem>();
            
            _isPopulatingTracks = true; // Block events

            // Add "None" option for subtitles
            subTracks.Add(new TrackItem { Text = "Kapalı (Altyazı Yok)", IsNone = true, Type = "sub" });

            try
            {
                long trackCount = await _mpvPlayer.GetPropertyLongAsync("track-list/count");

                for (int i = 0; i < (int)trackCount; i++)
                {
                    string type = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/type");
                    long id = await _mpvPlayer.GetPropertyLongAsync($"track-list/{i}/id");
                    bool selected = await _mpvPlayer.GetPropertyBoolAsync($"track-list/{i}/selected");
                    string title = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/title");
                    string lang = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/lang");
                    
                    if (title == "N/A") title = "";
                    if (lang == "N/A") lang = "";

                    string displayText = "";

                    if (type == "audio")
                    {
                        // Audio: [Lang] Title (Codec Channels)
                        string codec = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/codec-name");
                        string channels = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/audio-channels");
                        
                        string details = "";
                        if (!string.IsNullOrEmpty(codec) && codec != "N/A") details += codec.ToUpper();
                        if (!string.IsNullOrEmpty(channels) && channels != "N/A") details += $" {channels}ch";
                        
                        string langStr = !string.IsNullOrEmpty(lang) ? lang.ToUpper() : $"Track {id}";
                        string titleStr = !string.IsNullOrEmpty(title) ? title : "";
                        
                        displayText = $"{langStr} {titleStr}".Trim();
                        if (!string.IsNullOrEmpty(details)) displayText += $" ({details.Trim()})";
                    }
                    else if (type == "sub")
                    {
                        // Subtitle: [Lang] Title
                        string langStr = !string.IsNullOrEmpty(lang) ? lang.ToUpper() : $"Track {id}";
                        string titleStr = !string.IsNullOrEmpty(title) ? title : "";
                        displayText = $"{langStr} - {titleStr}".Trim(' ', '-');
                        if (string.IsNullOrEmpty(displayText)) displayText = $"Track {id}";
                    }
                    else continue;

                    var item = new TrackItem { Id = id, Text = displayText, IsSelected = selected, Type = type };
                    
                    if (type == "audio") audioTracks.Add(item);
                    else if (type == "sub") subTracks.Add(item);
                }

                // Check "None" selection state for subs
                if (!subTracks.Any(t => t.IsSelected && !t.IsNone))
                {
                     string subId = await _mpvPlayer.GetPropertyAsync("sid");
                     if (subId == "no") subTracks[0].IsSelected = true;
                }

                AudioListView.ItemsSource = audioTracks;
                SubtitleListView.ItemsSource = subTracks;

                // Scroll to selected
                var selectedAudio = audioTracks.FirstOrDefault(t => t.IsSelected);
                if (selectedAudio != null) AudioListView.SelectedItem = selectedAudio;

                var selectedSub = subTracks.FirstOrDefault(t => t.IsSelected);
                if (selectedSub != null) SubtitleListView.SelectedItem = selectedSub;
            }
            catch { }
            finally
            {
                _isPopulatingTracks = false;
            }
        }

        private async void AudioListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isPopulatingTracks || _mpvPlayer == null || AudioListView.SelectedItem is not TrackItem item) return;
            
            // Re-selection guard already mostly handled by _isPopulatingTracks but...
            // User complained it "re-selects". 
            // If the item IS selected MPV property, we might not want to re-set. 
            // But usually explicit click means "switch".
            
            await _mpvPlayer.SetPropertyAsync("aid", item.Id.ToString());
            ShowOsd($"Ses: {item.Text}");
        }

        private async void SubtitleListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
             if (_isPopulatingTracks || _mpvPlayer == null || SubtitleListView.SelectedItem is not TrackItem item) return;

             // ... REST OF LOGIC
            if (item.IsNone)
            {
                await _mpvPlayer.SetPropertyAsync("sid", "no");
                ShowOsd("Altyazı Kapalı");
            }
            else
            {
                await _mpvPlayer.SetPropertyAsync("sid", item.Id.ToString());
                await _mpvPlayer.SetPropertyAsync("sub-visibility", "yes");
                ShowOsd($"Altyazı: {item.Text}");
            }
        }



        private async Task<(bool Success, string Url, string ErrorMsg)> CheckStreamUrlAsync(string url)
        {
            try
            {
                // Basic cleanup: some servers dislike explicit :80
                var finalUrl = url.Replace(":80/", "/");

                using var request = new HttpRequestMessage(HttpMethod.Get, finalUrl);
                // Use a small range to avoid triggering "Download" limits if possible, 
                // but some servers hate Range. Let's try standard request headers only first.
                
                var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
                HttpResponseMessage response;

                try 
                {
                    // Use ResponseHeadersRead to avoid downloading the whole file
                    response = await HttpHelper.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                }
                catch (Exception ex)
                {
                    return (false, finalUrl, $"Sunucuya bağlanılamadı: {ex.Message}");
                }

                var contentType = response.Content.Headers.ContentType?.MediaType;
                
                // 1. Check for Server Errors
                if (!response.IsSuccessStatusCode)
                {
                     // Specifically handle 458 or 403
                     if ((int)response.StatusCode == 458 || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                         return (false, finalUrl, "Erişim Reddedildi veya Bağlantı Sınırı Aşıldı (458/403).");
                     
                     if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                         return (false, finalUrl, "Dosya Bulunamadı (404).");

                     return (false, finalUrl, $"Sunucu Hatası: {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                // 2. Check for "Webpage instead of Video" (The main issue)
                if (contentType != null && contentType.StartsWith("text/") && !contentType.Contains("mpegurl") && !contentType.Contains("xml"))
                {
                     return (false, finalUrl, "Yayın kaynağı geçerli bir video dosyası değil (Web Sayfası döndü). Link kırık veya süresi dolmuş olabilir.");
                }
                
                // Success
                return (true, finalUrl, string.Empty);
            }
            catch (Exception ex)
            {
                // If checking fails entirely, we might still let MPV try as a last resort, 
                // but usually it's better to fail gracefully.
                return (false, url, $"Bilinmeyen Hata: {ex.Message}");
            }
        }
    }
}
