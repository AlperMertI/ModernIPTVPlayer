using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ModernIPTVPlayer.Helpers;
using Windows.UI;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Manages the hero background lifecycle: crossfade transitions, slideshow rotation,
    /// ambience color extraction, and Ken Burns effect. Extracted from MediaInfoPage.Background.cs.
    /// </summary>
    internal sealed class BackgroundManager : IDisposable
    {
        #region Fields

        private readonly IBackgroundView _view;
        private readonly Compositor _compositor;
        private readonly DispatcherQueue _dispatcher;
        private bool _disposed;

        // State
        private string? _currentHeroUrl;
        private bool _isFirstImageApplied;
        private bool _isHeroTransitionInProgress;
        private CancellationTokenSource? _heroCts;

        // Slideshow
        private DispatcherTimer? _slideshowTimer;
        private string? _slideshowId;
        private readonly List<string> _backdropUrls = new();
        private readonly HashSet<string> _backdropKeys = new();
        private int _currentBackdropIndex;
        private readonly List<BackdropEntry> _validatedBackdrops = new();
        private string? _lastVisualSignature;

        // Ambience
        private readonly SemaphoreSlim _ambienceLock = new(1, 1);
        private AmbienceState _ambienceState;
        private string? _lastAmbienceUrl;
        private string? _lastAmbienceSignature;
        private int _ambienceNavigationEpoch;

        #endregion

        #region Constructor

        public BackgroundManager(IBackgroundView view, Compositor compositor, DispatcherQueue dispatcher)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        #endregion

        #region Hero Background

        /// <summary>
        /// Sets the hero background from an ImageSource (instant handoff).
        /// </summary>
        public void SetHero(ImageSource source, string reason)
        {
            if (_disposed || source == null) return;
            EnsureInitialized();

            var outgoing = GetActiveHero();
            if (outgoing.Source == source && outgoing.Opacity > 0.9)
            {
                _ = ExtractAndApplyAmbienceAsync(outgoing, $"seed-sync {reason}");
                return;
            }

            PerformHeroCrossfade(source, reason, url: null);
        }

        /// <summary>
        /// Sets the hero background from a URL with crossfade transition.
        /// </summary>
        public void SetHero(string imageUrl, string reason)
        {
            if (_disposed || string.IsNullOrWhiteSpace(imageUrl)) return;
            EnsureInitialized();

            if (IsAlreadyShowing(imageUrl))
            {
                _ = ExtractAndApplyAmbienceAsync(GetActiveHero(), $"seed-skipped {reason}");
                return;
            }

            PerformHeroCrossfade(ImageHelper.GetImage(imageUrl), reason, imageUrl);
        }

        /// <summary>
        /// Resets all background state for navigation to a new item.
        /// </summary>
        public void Reset()
        {
            if (_disposed) return;

            _heroCts?.Cancel();
            _heroCts?.Dispose();
            _heroCts = null;

            _backdropUrls.Clear();
            _validatedBackdrops.Clear();
            _backdropKeys.Clear();
            StopSlideshow();

            Interlocked.Increment(ref _ambienceNavigationEpoch);

            _view.SetHeroImageSource(null);
            _view.SetHeroImage2Source(null);
            _view.SetHeroImageOpacity(0);
            _view.SetHeroImage2Opacity(0);

            _currentHeroUrl = null;
            _isFirstImageApplied = false;

            _view.SetHeroShimmerVisibility(Visibility.Visible);
            _view.SetHeroShimmerOpacity(0.15f);
        }

        #endregion

        #region Slideshow

        /// <summary>
        /// Starts a background slideshow from a list of backdrop URLs.
        /// </summary>
        public void StartSlideshow(IEnumerable<string> urls)
        {
            if (_disposed) return;

            var urlList = urls?.ToList();
            if (urlList == null || urlList.Count == 0) return;

            string currentId = $"{_view.ItemTitle}";
            if (!string.IsNullOrEmpty(_view.ItemImdbId))
                currentId += $"_{_view.ItemImdbId.Replace("imdb_id:", "")}";

            if (_slideshowId == currentId)
            {
                foreach (var url in urlList) AddBackdrop(url);
                if (_backdropUrls.Count >= 2 && (_slideshowTimer == null || !_slideshowTimer.IsEnabled))
                    InitializeSlideshowTimer();
                return;
            }

            _slideshowId = currentId;
            StopSlideshow();
            _backdropKeys.Clear();
            _validatedBackdrops.Clear();
            _backdropUrls.Clear();
            _currentBackdropIndex = 0;

            foreach (var url in urlList) AddBackdrop(url);

            // Immediate upgrade: swap poster for first high-quality backdrop
            if (_isFirstImageApplied && !IsHighQuality(_currentHeroUrl) && _backdropUrls.Count > 0)
            {
                string firstBackdrop = _backdropUrls[0];
                if (NormalizeUrl(firstBackdrop) != NormalizeUrl(_currentHeroUrl))
                    _ = PerformHeroCrossfadeAsync(firstBackdrop, 1.8);
            }

            if (_backdropUrls.Count >= 2 && (_slideshowTimer == null || !_slideshowTimer.IsEnabled))
                InitializeSlideshowTimer();
        }

        /// <summary>
        /// Returns the currently visible backdrop URL.
        /// </summary>
        public string? GetCurrentBackdrop()
        {
            var active = GetActiveHero();
            return (active.Source as BitmapImage)?.UriSource?.ToString();
        }

        #endregion

        #region Crossfade Engine

        private void PerformHeroCrossfade(ImageSource source, string reason, string? url)
        {
            try
            {
                var incoming = GetInactiveHero();
                var outgoing = GetActiveHero();

                if (!_isFirstImageApplied)
                {
                    _isFirstImageApplied = true;
                    _currentHeroUrl = url;

                    bool isReady = ImageHelper.IsCached(url) || (source is not BitmapImage bi) || (bi.PixelWidth > 0);
                    incoming.Source = source;

                    if (isReady)
                    {
                        _view.SetInactiveHeroOpacity(1);
                        _ = ExtractAndApplyAmbienceAsync(incoming, $"hero-first {reason}");
                        _view.SetHeroShimmerVisibility(Visibility.Collapsed);
                    }
                    else
                    {
                        _view.SetInactiveHeroOpacity(0);
                        WireFirstImageHandlers(incoming, reason);
                    }

                    StartKenBurns(incoming);
                    return;
                }

                // Hijack blank state
                if (_view.GetActiveHeroOpacity() < 0.1)
                {
                    _currentHeroUrl = url;
                    _ = PerformHeroCrossfadeAsync(url, reason.Contains("prime") ? 0.4 : 1.2);
                    return;
                }

                _ = PerformHeroCrossfadeAsync(url, reason.Contains("prime") ? 0.4 : 1.8);
                StartKenBurns(incoming);
            }
            catch (Exception ex)
            {
                PageTelemetry.LogError("HeroCrossfade", ex);
            }
        }

        private async Task PerformHeroCrossfadeAsync(string? imageUrl, double durationSeconds = 1.8, Func<Microsoft.UI.Xaml.Controls.Image, Task<bool>>? onOpenedAsync = null)
        {
            if (_disposed || string.IsNullOrEmpty(imageUrl)) return;

            _heroCts?.Cancel();
            _heroCts?.Dispose();
            _heroCts = new CancellationTokenSource();
            var ct = _heroCts.Token;

            var incoming = GetInactiveHero();
            var outgoing = GetActiveHero();

            if (IsAlreadyShowing(imageUrl)) return;

            try
            {
                _isHeroTransitionInProgress = true;
                _currentHeroUrl = imageUrl;

                var visualIncoming = ElementCompositionPreview.GetElementVisual(incoming);
                var visualOutgoing = ElementCompositionPreview.GetElementVisual(outgoing);

                visualIncoming.Opacity = 0;

                RoutedEventHandler? openedHandler = null;
                ExceptionRoutedEventHandler? failedHandler = null;
                bool isResolved = false;

                Action cleanup = () =>
                {
                    incoming.ImageOpened -= openedHandler;
                    incoming.ImageFailed -= failedHandler;
                };

                var tcs = new TaskCompletionSource<bool>();

                openedHandler = async (s, e) =>
                {
                    if (isResolved || ct.IsCancellationRequested) return;
                    isResolved = true;

                    bool shouldProceed = true;
                    if (onOpenedAsync != null) shouldProceed = await onOpenedAsync(incoming);

                    cleanup();

                    if (ct.IsCancellationRequested || !shouldProceed)
                    {
                        _isHeroTransitionInProgress = false;
                        tcs.TrySetResult(false);
                        return;
                    }

                    _view.SetHeroShimmerVisibility(Visibility.Collapsed);
                    visualIncoming.Opacity = 0;
                    StartKenBurns(incoming);

                    var startIn = (float)_view.GetInactiveHeroOpacity();
                    var startOut = (float)_view.GetActiveHeroOpacity();

                    var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                    fadeIn.InsertKeyFrame(0f, startIn);
                    fadeIn.InsertKeyFrame(1f, 1f);
                    fadeIn.Duration = TimeSpan.FromSeconds(durationSeconds);

                    var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                    fadeOut.InsertKeyFrame(0f, startOut);
                    fadeOut.InsertKeyFrame(1f, 0f);
                    fadeOut.Duration = TimeSpan.FromSeconds(durationSeconds);

                    visualIncoming.StartAnimation("Opacity", fadeIn);
                    visualOutgoing.StartAnimation("Opacity", fadeOut);

                    _ = Task.Delay(TimeSpan.FromSeconds(durationSeconds) + TimeSpan.FromMilliseconds(80), ct)
                        .ContinueWith(t =>
                        {
                            if (t.IsCanceled) return;
                            _dispatcher.TryEnqueue(() =>
                            {
                                _isHeroTransitionInProgress = false;
                                _view.SetInactiveHeroOpacity(1);
                                _view.SetActiveHeroOpacity(0);
                                _view.SetOutgoingHeroSource(null);
                            });
                        }, TaskScheduler.Default);

                    tcs.TrySetResult(true);
                };

                failedHandler = (s, e) =>
                {
                    if (isResolved || ct.IsCancellationRequested) return;
                    isResolved = true;
                    cleanup();
                    _isHeroTransitionInProgress = false;
                    PageTelemetry.LogWarn("HeroCrossfade", $"Failed for {imageUrl}: {e.ErrorMessage}");
                    tcs.TrySetResult(false);
                };

                incoming.ImageOpened += openedHandler;
                incoming.ImageFailed += failedHandler;
                incoming.Source = new BitmapImage(new Uri(imageUrl));

                // Timeout guard
                _ = Task.Delay(10000, ct).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    if (!isResolved)
                    {
                        isResolved = true;
                        _dispatcher.TryEnqueue(() =>
                        {
                            cleanup();
                            _isHeroTransitionInProgress = false;
                        });
                    }
                });

                await tcs.Task;
            }
            catch (Exception ex)
            {
                _isHeroTransitionInProgress = false;
                PageTelemetry.LogError("HeroCrossfadeAsync", ex);
            }
        }

        #endregion

        #region Ken Burns Effect

        private void StartKenBurns(Microsoft.UI.Xaml.Controls.Image target)
        {
            if (_disposed || _compositor == null || target == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(target);
            var centerExpr = _compositor.CreateExpressionAnimation("Vector3(this.Target.Size.X * 0.5f, this.Target.Size.Y * 0.5f, 0)");
            visual.StartAnimation("CenterPoint", centerExpr);

            var scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(0f, new Vector3(1.0f, 1.0f, 1.0f));
            scaleAnim.InsertKeyFrame(1f, new Vector3(1.08f, 1.08f, 1.0f));
            scaleAnim.Duration = TimeSpan.FromSeconds(25);
            scaleAnim.IterationBehavior = AnimationIterationBehavior.Forever;
            scaleAnim.Direction = AnimationDirection.Alternate;

            visual.StartAnimation("Scale", scaleAnim);
        }

        #endregion

        #region Ambience

        private async Task ExtractAndApplyAmbienceAsync(Microsoft.UI.Xaml.Controls.Image sourceImage, string sourceLabel)
        {
            if (_disposed) return;

            var currentUrl = (sourceImage.Source as BitmapImage)?.UriSource?.ToString();
            bool isValidated = sourceLabel.Contains("validated");

            int startEpoch = Volatile.Read(ref _ambienceNavigationEpoch);
            string currentSignature = BuildAmbienceSignature(currentUrl, sourceLabel);

            if (!string.IsNullOrEmpty(currentSignature) && _lastAmbienceSignature == currentSignature) return;

            await _ambienceLock.WaitAsync();
            try
            {
                if (startEpoch != Volatile.Read(ref _ambienceNavigationEpoch)) return;
                if (_lastAmbienceSignature == currentSignature) return;

                if (sourceImage.Source is BitmapImage rtb)
                {
                    if (startEpoch != Volatile.Read(ref _ambienceNavigationEpoch)) return;

                    var colors = await ImageHelper.ExtractColorsAsync(rtb);
                    if (rtb.PixelWidth == 0 || startEpoch != Volatile.Read(ref _ambienceNavigationEpoch)) return;

                    var areaColor = ImageHelper.GenerateAreaBackground(colors.Primary);

                    _lastAmbienceUrl = currentUrl;
                    _lastAmbienceSignature = currentSignature;
                    if (isValidated) _ambienceState = AmbienceState.Stable;
                    else if (_ambienceState == AmbienceState.None) _ambienceState = AmbienceState.Provisional;

                    ApplyAmbience(colors.Primary, areaColor, sourceLabel);
                }
            }
            catch (Exception ex)
            {
                PageTelemetry.LogError("ExtractAmbience", ex);
            }
            finally
            {
                _ambienceLock.Release();
            }
        }

        private void ApplyAmbience(Color primary, Color areaBackground, string sourceLabel)
        {
            if (_disposed) return;

            _primaryColorHex = $"#{primary.A:X2}{primary.R:X2}{primary.G:X2}{primary.B:X2}";

            try
            {
                var btnTint = Color.FromArgb(50, primary.R, primary.G, primary.B);
                var mixedPanelTint = Color.FromArgb(180, (byte)(primary.R * 0.2), (byte)(primary.G * 0.2), (byte)(primary.B * 0.2));

                double areaL = (0.2126 * areaBackground.R + 0.7152 * areaBackground.G + 0.0722 * areaBackground.B) / 255.0;
                int maxV = Math.Max(areaBackground.R, Math.Max(areaBackground.G, areaBackground.B));
                int minV = Math.Min(areaBackground.R, Math.Min(areaBackground.G, areaBackground.B));
                int vibrancy = maxV - minV;

                Color headerColor, descriptionColor;

                if (vibrancy < 30)
                {
                    double whiteLc = Math.Abs(ImageHelper.GetContrastAPCA(Colors.White, areaBackground));
                    double darkLc = Math.Abs(ImageHelper.GetContrastAPCA(Color.FromArgb(255, 20, 20, 22), areaBackground));
                    bool useDarkText = darkLc > (whiteLc + 15) && darkLc > 50;

                    headerColor = useDarkText ? Color.FromArgb(255, 20, 20, 22) : Colors.White;
                    descriptionColor = useDarkText ? Color.FromArgb(255, 55, 55, 60) : Color.FromArgb(255, 224, 224, 224);
                }
                else
                {
                    headerColor = ImageHelper.GetContrastSafeColor(primary, areaBackground, 92);
                    descriptionColor = ImageHelper.GetContrastSafeColor(headerColor, areaBackground, 72);
                }

                double rawLc = Math.Abs(ImageHelper.GetContrastAPCA(headerColor, areaBackground));
                double vibrancyBonus = Math.Clamp((vibrancy - 40) / 4.0, 0, 25);
                double lc = rawLc + vibrancyBonus;

                _view.SetGradientOpacity("LocalInfoGradient", (float)Math.Clamp(1.0 - (lc / 130.0), 0.15, 0.95), 650);
                _view.SetGradientOpacity("ExtraReadabilityGradient", (float)Math.Clamp(0.8 - (lc / 130.0), 0.10, 0.75), 650);
                _view.SetGradientOpacity("BottomReadabilityGradient", (float)Math.Clamp(1.0 - (lc / 130.0), 0.15, 0.95), 650);

                _view.SetTitleColor(headerColor);
                _view.SetOverviewColor(descriptionColor);
            }
            catch (Exception ex)
            {
                PageTelemetry.LogError("ApplyAmbience", ex);
            }
        }

        #endregion

        #region Slideshow Internals

        private void AddBackdrop(string url)
        {
            if (string.IsNullOrEmpty(url) || ImageHelper.IsPlaceholder(url)) return;

            string lowerUrl = url.ToLowerInvariant();
            if (lowerUrl.Contains("poster") || lowerUrl.Contains("/small/") || lowerUrl.Contains("w600_and_h900")) return;

            string key = GetNormalizedImageKey(url);
            if (_backdropKeys.Contains(key)) return;
            if (!string.IsNullOrEmpty(_currentHeroUrl) && GetNormalizedImageKey(_currentHeroUrl) == key) return;

            _backdropKeys.Add(key);
            _backdropUrls.Add(url);

            if (_backdropUrls.Count == 2 && (_slideshowTimer == null || !_slideshowTimer.IsEnabled))
                InitializeSlideshowTimer();
        }

        private void InitializeSlideshowTimer()
        {
            if (_backdropUrls.Count <= 1) return;

            _slideshowTimer?.Stop();
            _slideshowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };

            _slideshowTimer.Tick += async (s, e) =>
            {
                if (_disposed || _backdropUrls.Count <= 1 || _isHeroTransitionInProgress) return;

                _currentBackdropIndex = (_currentBackdropIndex + 1) % _backdropUrls.Count;
                string nextImgUrl = _backdropUrls[_currentBackdropIndex];

                await PerformHeroCrossfadeAsync(nextImgUrl, 2.0, async (incoming) =>
                {
                    string signature = await CalculateVisualSignatureAsync(incoming);
                    if (signature == null) return true;

                    var existing = _validatedBackdrops.FirstOrDefault(v => IsSignatureSimilar(signature, v.Signature));

                    if (existing != null)
                    {
                        if (existing.Url == nextImgUrl)
                        {
                            _lastVisualSignature = signature;
                            return true;
                        }
                        else
                        {
                            _backdropUrls.RemoveAt(_currentBackdropIndex);
                            _backdropKeys.Remove(GetNormalizedImageKey(nextImgUrl));
                            _currentBackdropIndex = (_currentBackdropIndex - 1 + _backdropUrls.Count) % _backdropUrls.Count;
                            return false;
                        }
                    }

                    _validatedBackdrops.Add(new BackdropEntry { Url = nextImgUrl, Signature = signature });
                    _lastVisualSignature = signature;
                    _ = ExtractAndApplyAmbienceAsync(incoming, "slideshow validated");
                    return true;
                });
            };

            _slideshowTimer.Start();
        }

        private void StopSlideshow()
        {
            _slideshowTimer?.Stop();
            _slideshowTimer = null;
            _slideshowId = null;
        }

        private static string GetNormalizedImageKey(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            try
            {
                string lowerUrl = url.ToLowerInvariant();

                if (lowerUrl.Contains("metahub.space"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(lowerUrl, @"(tt\d+)");
                    if (match.Success) return $"metahub_{match.Value}";
                }

                if (lowerUrl.Contains("tmdb.org"))
                {
                    int lastSlash = lowerUrl.LastIndexOf('/');
                    if (lastSlash >= 0)
                    {
                        string filename = lowerUrl.Substring(lastSlash + 1);
                        int dot = filename.LastIndexOf('.');
                        if (dot >= 0) filename = filename.Substring(0, dot);
                        return filename;
                    }
                }

                var key = url.Replace("https://", "").Replace("http://", "");
                int queryIdx = key.IndexOf('?');
                if (queryIdx > 0) key = key.Substring(0, queryIdx);
                return key.TrimEnd('/');
            }
            catch { return url; }
        }

        private static async Task<string> CalculateVisualSignatureAsync(Microsoft.UI.Xaml.Controls.Image img)
        {
            if (img.Source is not BitmapImage bi) return null;

            var colors = await ImageHelper.ExtractColorsAsync(bi);
            float ratio = (bi.PixelWidth > 0 && bi.PixelHeight > 0) ? (float)bi.PixelWidth / bi.PixelHeight : 1.0f;

            return $"{colors.Primary.R:X2}{colors.Primary.G:X2}{colors.Primary.B:X2}_" +
                   $"{colors.Secondary.R:X2}{colors.Secondary.G:X2}{colors.Secondary.B:X2}_" +
                   $"{ratio:F2}";
        }

        private static bool IsSignatureSimilar(string sig1, string sig2)
        {
            if (string.IsNullOrEmpty(sig1) || string.IsNullOrEmpty(sig2)) return false;
            if (sig1 == sig2) return true;

            var parts1 = sig1.Split('_');
            var parts2 = sig2.Split('_');
            if (parts1.Length < 3 || parts2.Length < 3) return false;

            return parts1[0] == parts2[0] && parts1[1] == parts2[1];
        }

        #endregion

        #region Helpers

        private void WireFirstImageHandlers(Microsoft.UI.Xaml.Controls.Image img, string reason)
        {
            bool resolved = false;

            RoutedEventHandler openedHandler = null;
            ExceptionRoutedEventHandler failedHandler = null;

            openedHandler = (s, e) =>
            {
                if (resolved) return;
                resolved = true;
                img.ImageOpened -= openedHandler;
                img.ImageFailed -= failedHandler;

                _dispatcher.TryEnqueue(() =>
                {
                    _view.SetInactiveHeroOpacity(1);
                    _view.SetHeroShimmerVisibility(Visibility.Collapsed);
                    _ = ExtractAndApplyAmbienceAsync(img, $"first-image-opened {reason}");
                });
            };

            failedHandler = (s, e) =>
            {
                if (resolved) return;
                resolved = true;
                img.ImageOpened -= openedHandler;
                img.ImageFailed -= failedHandler;

                PageTelemetry.LogWarn("FirstImage", $"Failed to load: {e.ErrorMessage}");
                _dispatcher.TryEnqueue(() => _view.SetHeroShimmerVisibility(Visibility.Collapsed));
            };

            img.ImageOpened += openedHandler;
            img.ImageFailed += failedHandler;
        }

        private static string NormalizeUrl(string url) => url?.Replace("https://", "http://")?.TrimEnd('/')?.ToLowerInvariant();

        private bool IsAlreadyShowing(string? url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            string normTarget = NormalizeUrl(url);

            if (_view.GetActiveHeroOpacity() > 0.8 && NormalizeUrl(_view.GetActiveHeroUrl()) == normTarget) return true;
            if (_view.GetInactiveHeroOpacity() > 0.8 && NormalizeUrl(_view.GetInactiveHeroUrl()) == normTarget) return true;

            return false;
        }

        private static bool IsHighQuality(string? url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            string lower = url.ToLowerInvariant();
            return !lower.Contains("poster") && !lower.Contains("/small/") && !lower.Contains("_sx") && !lower.Contains("_sy") && !lower.Contains("w600_and_h900");
        }

        private Microsoft.UI.Xaml.Controls.Image GetActiveHero() =>
            _view.GetActiveHeroOpacity() >= _view.GetInactiveHeroOpacity() ? _view.HeroImage : _view.HeroImage2;

        private Microsoft.UI.Xaml.Controls.Image GetInactiveHero() =>
            _view.GetActiveHeroOpacity() < _view.GetInactiveHeroOpacity() ? _view.HeroImage : _view.HeroImage2;

        private void EnsureInitialized()
        {
            _view.SetHeroImage2Opacity(0);
            var v2 = ElementCompositionPreview.GetElementVisual(_view.HeroImage2);
            v2.Opacity = 0f;
        }

        private static string BuildAmbienceSignature(string? url, string sourceLabel) =>
            $"{url ?? "<null>"}|{NormalizeAmbienceSourceLabel(sourceLabel)}";

        private static string NormalizeAmbienceSourceLabel(string sourceLabel)
        {
            if (string.IsNullOrEmpty(sourceLabel)) return "default";
            if (sourceLabel.Contains("validated")) return "validated";
            if (sourceLabel.Contains("image opened")) return "opened";
            if (sourceLabel.Contains("prime")) return "prime";
            return sourceLabel;
        }

        private string _primaryColorHex = "#FF00BFA5";

        /// <summary>
        /// Returns the current extracted primary color as a hex string.
        /// </summary>
        public string PrimaryColorHex => _primaryColorHex;

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _heroCts?.Cancel();
            _heroCts?.Dispose();
            _ambienceLock.Dispose();
            StopSlideshow();
        }

        #endregion

        #region Nested Types

        private enum AmbienceState { None, Provisional, Stable }

        private sealed class BackdropEntry
        {
            public string Url { get; set; } = string.Empty;
            public string Signature { get; set; } = string.Empty;
        }

        #endregion
    }
}
