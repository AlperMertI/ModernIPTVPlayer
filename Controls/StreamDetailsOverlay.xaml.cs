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
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using ModernIPTVPlayer.Services;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class StreamDetailsOverlay : UserControl
    {
        private CancellationTokenSource _cts;
        private readonly DispatcherQueue _dispatcher;
        private Compositor _compositor;
        private CompositionLinearGradientBrush _shimmerBrush;
        private readonly string[] _allFields = new[] { "Res", "VideoCodec", "Fps", "Bitrate", "ColorSpace", "Scan", "Chroma", "Range", "Audio", "Langs", "Container", "Protocol", "Mime", "Server", "Encoder" };

        public event EventHandler<ProbeResult> AnalysisUpdated;

        public StreamDetailsOverlay()
        {
            this.InitializeComponent();
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            SetupCompositionShimmer();
        }

        private void SetupCompositionShimmer()
        {
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _shimmerBrush = _compositor.CreateLinearGradientBrush();
            
            // Set ExtendMode to Wrap to allow continuous sliding
            _shimmerBrush.ExtendMode = CompositionGradientExtendMode.Wrap;

            // PREMIUM LOOK: Diagonal angle for a more professional feel
            _shimmerBrush.StartPoint = new Vector2(-1, -1);
            _shimmerBrush.EndPoint = new Vector2(1, 1);

            // SOFTENED GRADIENT: Multiple stops for a smoother "glow" falloff
            _shimmerBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.0f, ColorHelper.FromArgb(0, 255, 255, 255)));
            _shimmerBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.45f, ColorHelper.FromArgb(30, 255, 255, 255)));
            _shimmerBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.5f, ColorHelper.FromArgb(110, 255, 255, 255))); // Center highlight
            _shimmerBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.55f, ColorHelper.FromArgb(30, 255, 255, 255)));
            _shimmerBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.0f, ColorHelper.FromArgb(0, 255, 255, 255)));

            // PREMIUM ANIMATION: Use Cubic Bezier for "fluid" motion instead of linear
            var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0.0f), new Vector2(0.2f, 1.0f));

            var startAnim = _compositor.CreateVector2KeyFrameAnimation();
            startAnim.InsertKeyFrame(0.0f, new Vector2(-2.0f, -2.0f));
            startAnim.InsertKeyFrame(1.0f, new Vector2(1.0f, 1.0f), easing);
            startAnim.Duration = TimeSpan.FromSeconds(2.2);
            startAnim.IterationBehavior = AnimationIterationBehavior.Forever;

            var endAnim = _compositor.CreateVector2KeyFrameAnimation();
            endAnim.InsertKeyFrame(0.0f, new Vector2(-1.0f, -1.0f));
            endAnim.InsertKeyFrame(1.0f, new Vector2(2.0f, 2.0f), easing);
            endAnim.Duration = TimeSpan.FromSeconds(2.2);
            endAnim.IterationBehavior = AnimationIterationBehavior.Forever;

            _shimmerBrush.StartAnimation("StartPoint", startAnim);
            _shimmerBrush.StartAnimation("EndPoint", endAnim);
        }

        private void ApplyShimmerToSkeleton(Rectangle skeleton)
        {
            var visual = ElementCompositionPreview.GetElementVisual(skeleton);
            var sprite = _compositor.CreateSpriteVisual();
            
            // Sync size with the parent visual
            var bindSize = _compositor.CreateExpressionAnimation("visual.Size");
            bindSize.SetReferenceParameter("visual", visual);
            sprite.StartAnimation("Size", bindSize);

            // SOFTENED EDGES: Apply a GeometricClip with RoundedRectangleGeometry to match the parent's CornerRadius
            // This fixes the "sharp rectangle" look and provides better compatibility across SDK versions.
            var geometry = _compositor.CreateRoundedRectangleGeometry();
            geometry.CornerRadius = new Vector2(6, 6); // Matches SkeletonStyle's RadiusX/Y
            geometry.StartAnimation("Size", bindSize);

            var clip = _compositor.CreateGeometricClip(geometry);
            sprite.Clip = clip;

            sprite.Brush = _shimmerBrush;
            ElementCompositionPreview.SetElementChildVisual(skeleton, sprite);
        }

        public async Task ShowAsync(int streamId, string url)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            
            ResetUI();
            this.Visibility = Visibility.Visible;
            RootGrid.Visibility = Visibility.Visible;
            MainPivot.SelectedIndex = 1;

            var progress = new Progress<ProbeResult>(result => 
            {
                _dispatcher.TryEnqueue(() => 
                {
                    UpdateUI(result);
                    AnalysisUpdated?.Invoke(this, result);
                });
            });

            try
            {
                var finalResult = await StreamProberService.Instance.ProbeAsync(streamId, url, progress, _cts.Token);
                _dispatcher.TryEnqueue(() => FinalizeAnalysis(finalResult));
            }
            catch (Exception ex) 
            { 
                CacheLogger.Error(CacheLogger.Category.Probe, "Overlay Error", ex.Message);
                _dispatcher.TryEnqueue(() => FinalizeAnalysis(null));
            }
        }

        private void ResetUI()
        {
            foreach (var field in _allFields)
            {
                var s = FindName("S_" + field) as Rectangle;
                var v = FindName("V_" + field) as TextBlock;
                if (s != null) { s.Visibility = Visibility.Visible; ApplyShimmerToSkeleton(s); }
                if (v != null) { v.Visibility = Visibility.Collapsed; v.Opacity = 0; v.Text = ""; }
            }
            V_Subtitles.Text = "Analiz ediliyor...";
            BufferProgress.Value = 0;
            V_Drm.Text = "Analiz ediliyor...";
        }

        private void UpdateUI(ProbeResult result)
        {
            if (result == null) return;

            SetStat("Res", result.Resolution);
            SetStat("Fps", !string.IsNullOrEmpty(result.Fps) ? result.Fps + " FPS" : null);
            SetStat("VideoCodec", !string.IsNullOrEmpty(result.Codec) ? result.Codec + (result.IsHdr ? " (HDR)" : "") : null);
            SetStat("Bitrate", result.Bitrate > 0 ? $"{(result.Bitrate / 1000.0):F0} kbps" : null);
            string audioInfo = null;
            if (!string.IsNullOrEmpty(result.AudioCodec))
            {
                audioInfo = !string.IsNullOrEmpty(result.AudioChannels) 
                    ? $"{result.AudioCodec} ({result.AudioChannels} Ch)" 
                    : result.AudioCodec;
            }
            SetStat("Audio", audioInfo);
            
            SetStat("ColorSpace", result.ColorSpace);
            SetStat("Scan", !string.IsNullOrEmpty(result.ScanType) ? (result.ScanType == "i" ? "1080i (Interlaced)" : "1080p (Progressive)") : null);
            SetStat("Chroma", result.ChromaSubsampling);
            SetStat("Range", result.ColorRange);
            SetStat("Langs", result.AudioLanguages);

            SetStat("Container", result.Container);
            SetStat("Protocol", result.Protocol);
            SetStat("Mime", result.MimeType);
            SetStat("Server", result.Server);
            SetStat("Encoder", result.Encoder);

            if (!string.IsNullOrEmpty(result.SubtitleTracks)) V_Subtitles.Text = result.SubtitleTracks;

            V_AvSync.Text = $"{result.AvSync:F2} ms";
            V_Buffer.Text = $"{result.BufferDuration:F2} sn / {(result.BufferSize / 1024.0 / 1024.0):F1} MB";
            BufferProgress.Value = Math.Min(10, result.BufferDuration);
            V_Drm.Text = result.IsEncrypted ? (result.DrmType ?? "Encrypted") : "None (Clear)";
        }

        private void FinalizeAnalysis(ProbeResult result)
        {
            foreach (var field in _allFields)
            {
                var v = FindName("V_" + field) as TextBlock;
                var s = FindName("S_" + field) as Rectangle;
                if (v != null && v.Visibility != Visibility.Visible) SetStat(field, "N/A");
            }
            if (V_Subtitles.Text == "Analiz ediliyor...") V_Subtitles.Text = "Altyazı bulunamadı.";
            if (V_Drm.Text == "Analiz ediliyor...") V_Drm.Text = "Clear (No DRM)";
            if (result != null) AnalysisUpdated?.Invoke(this, result);
        }

        private void SetStat(string field, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var s = FindName("S_" + field) as Rectangle;
            var v = FindName("V_" + field) as TextBlock;

            if (v != null && v.Text != value)
            {
                v.Text = value;
                if (v.Visibility != Visibility.Visible)
                {
                    if (s != null) s.Visibility = Visibility.Collapsed;
                    v.Visibility = Visibility.Visible;
                    var anim = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(400) };
                    Storyboard.SetTarget(anim, v);
                    Storyboard.SetTargetProperty(anim, "Opacity");
                    var sb = new Storyboard();
                    sb.Children.Add(anim);
                    sb.Begin();
                }
            }
        }

        private void RootGrid_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            // Only close if the background (RootGrid) was directly tapped
            CloseButton_Click(this, null);
        }

        private void MainCard_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            // Prevent tapping the card from closing the overlay
            e.Handled = true;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            RootGrid.Visibility = Visibility.Collapsed;
            this.Visibility = Visibility.Collapsed;
        }
    }
}
