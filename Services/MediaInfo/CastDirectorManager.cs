using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Models.Stremio;
using Windows.Foundation;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    internal sealed class CastDirectorManager : IDisposable
    {
        private readonly MediaInfoPage _page;
        private readonly Compositor _compositor;
        private readonly DispatcherTimer _personHoverTimer;
        private CancellationTokenSource _personCloseCts;
        private FrameworkElement _pendingPersonSource;
        private FrameworkElement _activePersonAnchorSource;
        private bool _isPointerOverPersonCard;
        private bool _isCastDragging;
        private Point _lastCastPointerPos;
        private bool _disposed;

        public ObservableCollection<CastItem> CastList { get; } = new();
        public ObservableCollection<CastItem> DirectorList { get; } = new();

        public CastDirectorManager(MediaInfoPage page, Compositor compositor)
        {
            _page = page ?? throw new ArgumentNullException(nameof(page));
            _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));

            _personHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _personHoverTimer.Tick += PersonHoverTimer_Tick;

            SetupDragToScroll();
            ModernIPTVPlayer.Services.AppLogger.Info("[CAST-DIRECTOR] Initialized");
        }

        #region Cast & Director Loading

        public async Task PopulateCastAndDirectorsAsync(UnifiedMetadata unified)
        {
            if (_disposed || unified == null) return;

            try
            {
                var newCast = new List<CastItem>();
                if (unified.Cast != null && unified.Cast.Count > 0)
                {
                    foreach (var c in unified.Cast.Take(10))
                    {
                        newCast.Add(new CastItem
                        {
                            Name = c.Name,
                            Character = c.Character,
                            FullProfileUrl = c.ProfileUrl,
                            ProfileImage = ImageHelper.GetImage(c.ProfileUrl, 80, 100)
                        });
                    }
                }
                else if (unified.TmdbInfo != null && AppSettings.IsTmdbEnabled)
                {
                    var credits = await TmdbHelper.GetCreditsAsync(unified.TmdbInfo.Id, unified.IsSeries);
                    if (credits?.Cast != null)
                    {
                        foreach (var c in credits.Cast.Take(10))
                        {
                            newCast.Add(new CastItem
                            {
                                Name = c.Name,
                                Character = c.Character,
                                FullProfileUrl = c.FullProfileUrl,
                                ProfileImage = ImageHelper.GetImage(c.FullProfileUrl, 80, 100)
                            });
                        }
                    }
                }

                bool castChanged = CastList.Count != newCast.Count ||
                    (CastList.Count > 0 && newCast.Count > 0 &&
                     (CastList[0].Name != newCast[0].Name || CastList[0].FullProfileUrl != newCast[0].FullProfileUrl));

                if (castChanged)
                {
                    _page.DispatcherQueue.TryEnqueue(() =>
                    {
                        CastList.Clear();
                        foreach (var c in newCast) CastList.Add(c);
                        _page.SetCastListItemsSource(CastList);
                        _page.SetNarrowCastListItemsSource(CastList);
                    });
                }

                var newDirectors = new List<CastItem>();
                bool hasWritersString = !string.IsNullOrEmpty(unified.Writers);
                if (unified.IsSeries && hasWritersString)
                {
                    var writers = unified.Writers.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var w in writers.Take(3))
                    {
                        newDirectors.Add(new CastItem { Name = w.Trim(), Character = "Yazar", ProfileImage = ImageHelper.GetImage(null, 80, 100) });
                    }
                }

                if (unified.Directors != null && unified.Directors.Count > 0)
                {
                    foreach (var d in unified.Directors.Take(5))
                    {
                        var existing = newDirectors.FirstOrDefault(nd => nd.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            existing.Character = "Yönetmen / Yazar";
                            existing.FullProfileUrl = d.ProfileUrl;
                            existing.ProfileImage = ImageHelper.GetImage(d.ProfileUrl, 80, 100);
                            continue;
                        }
                        newDirectors.Add(new CastItem { Name = d.Name, Character = "Yönetmen", FullProfileUrl = d.ProfileUrl, ProfileImage = ImageHelper.GetImage(d.ProfileUrl, 80, 100) });
                    }
                }

                if (unified.TmdbInfo != null && AppSettings.IsTmdbEnabled)
                {
                    var credits = await TmdbHelper.GetCreditsAsync(unified.TmdbInfo.Id, unified.IsSeries);
                    if (credits?.Crew != null)
                    {
                        var tmdbDirectors = credits.Crew.Where(c => c.Job == "Director").ToList();
                        foreach (var d in newDirectors)
                        {
                            if (string.IsNullOrEmpty(d.FullProfileUrl))
                            {
                                var match = tmdbDirectors.FirstOrDefault(tc => tc.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
                                if (match != null)
                                {
                                    d.FullProfileUrl = match.FullProfileUrl;
                                    d.ProfileImage = ImageHelper.GetImage(match.FullProfileUrl, 80, 100);
                                }
                            }
                        }
                    }
                }

                bool hasDirectors = newDirectors.Any(d => d.Character.Contains("Yönetmen"));
                bool hasWriters = newDirectors.Any(d => d.Character.Contains("Yazar"));
                string headerText = hasDirectors && hasWriters ? "Yönetmen / Yazar" : (hasWriters ? (unified.IsSeries ? "Yaratıcı" : "Yazar") : "Yönetmen");
                _page.SetDirectorHeaderText(headerText);

                bool directorChanged = DirectorList.Count != newDirectors.Count ||
                    (DirectorList.Count > 0 && newDirectors.Count > 0 && DirectorList[0].Name != newDirectors[0].Name);

                if (directorChanged)
                {
                    _page.DispatcherQueue.TryEnqueue(() =>
                    {
                        DirectorList.Clear();
                        foreach (var d in newDirectors) DirectorList.Add(d);
                        _page.SetDirectorListItemsSource(DirectorList);
                        _page.SetNarrowDirectorListItemsSource(DirectorList);
                    });
                }

                ModernIPTVPlayer.Services.AppLogger.Info($"[CAST-DIRECTOR] Populated {newCast.Count} cast, {newDirectors.Count} directors");
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error("[CAST-DIRECTOR] PopulateCastAndDirectorsAsync error", ex);
            }
        }

        public void Clear()
        {
            if (_disposed) return;
            CastList.Clear();
            DirectorList.Clear();
            _personHoverTimer.Stop();
            ModernIPTVPlayer.Services.AppLogger.Info("[CAST-DIRECTOR] Cleared");
        }

        #endregion

        #region Person Hover Card

        public void OnCastItemPointerEntered(FrameworkElement element)
        {
            if (_disposed || element == null) return;

            _personCloseCts?.Cancel();
            _personCloseCts?.Dispose();
            _personCloseCts = null;

            var container = _page.FindParent<ListViewItem>(element);
            if (container != null) Canvas.SetZIndex(container, 100);

            var visual = ElementCompositionPreview.GetElementVisual(element);
            var scaleAnim = visual.Compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(1f, new Vector3(1.08f, 1.08f, 1.0f));
            scaleAnim.Duration = TimeSpan.FromMilliseconds(250);
            visual.StartAnimation("Scale", scaleAnim);

            _pendingPersonSource = element;
            bool isCardVisible = _page.IsPersonCardVisible;
            _personHoverTimer.Interval = TimeSpan.FromMilliseconds(isCardVisible ? 700 : 400);
            _personHoverTimer.Stop();
            _personHoverTimer.Start();
        }

        public void OnCastItemPointerExited(FrameworkElement element, PointerRoutedEventArgs e)
        {
            if (_disposed || element == null) return;

            try
            {
                var point = _page.GetCurrentPoint(element, e);
                if (point.X >= 0 && point.Y >= 0 && point.X <= element.ActualWidth && point.Y <= element.ActualHeight)
                    return;
            }
            catch { }

            var container = _page.FindParent<ListViewItem>(element);
            if (container != null) Canvas.SetZIndex(container, 0);

            var visual = ElementCompositionPreview.GetElementVisual(element);
            var scaleAnim = visual.Compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(1f, new Vector3(1.0f, 1.0f, 1.0f));
            scaleAnim.Duration = TimeSpan.FromMilliseconds(200);
            visual.StartAnimation("Scale", scaleAnim);

            _personHoverTimer.Stop();
            _ = ClosePersonCardAsync();
        }

        public void OnCastItemTapped(FrameworkElement element, bool isCastVisible, bool isDirectorVisible)
        {
            if (_disposed || element == null || element.DataContext is not CastItem castItem) return;

            bool isSectionVisible = isCastVisible || isDirectorVisible;
            if (!isSectionVisible) return;

            _personHoverTimer.Stop();
            _pendingPersonSource = element;
            ModernIPTVPlayer.Services.AppLogger.Info($"[PersonCard] Opening for: {castItem.Name}");
            _ = ShowPersonCardAsync(castItem);
        }

        public void OnPersonCardPointerEntered()
        {
            if (_disposed) return;
            _isPointerOverPersonCard = true;
            _personCloseCts?.Cancel();
            _personCloseCts?.Dispose();
            _personCloseCts = null;
        }

        public void OnPersonCardPointerExited()
        {
            if (_disposed) return;
            _isPointerOverPersonCard = false;
            _ = ClosePersonCardAsync();
        }

        public void OnRootGridPointerPressed()
        {
            if (_disposed) return;
            if (_page.IsPersonCardVisible && !_isPointerOverPersonCard)
                ClosePersonCard();
        }

        public void OnActivePersonCardSizeChanged()
        {
            if (_disposed || _activePersonAnchorSource == null) return;
            _page.PlacePersonCard(_activePersonAnchorSource, animateMove: true);
        }

        private void PersonHoverTimer_Tick(object sender, object e)
        {
            if (_disposed) return;
            _personHoverTimer.Stop();
            if (_pendingPersonSource != null && _pendingPersonSource.DataContext is CastItem castItem)
            {
                _ = ShowPersonCardAsync(castItem);
            }
        }

        public async Task ShowPersonCardAsync(CastItem castItem)
        {
            if (_disposed || _pendingPersonSource == null) return;

            var sourceAtRequest = _pendingPersonSource;
            _activePersonAnchorSource = sourceAtRequest;
            _page.ActivePersonCardItem = castItem;

            var visual = ElementCompositionPreview.GetElementVisual(_page.ActivePersonCardControl);
            bool isAlreadyVisible = _page.ActivePersonCardControl.Visibility == Visibility.Visible && visual.Opacity > 0.1f;

            _page.LoadPersonCardAsync(castItem.Name, castItem.Character, castItem.FullProfileUrl,
                _page.UnifiedMetadata?.ImdbId, _page.Item?.TmdbInfo,
                (stream) => { ClosePersonCard(); _page.NavigateToMediaInfoPage(stream); });

            if (!isAlreadyVisible) _page.ActivePersonCardControl.Opacity = 0;
            _page.SetPersonCardOverlayVisibility(Visibility.Visible);
            _page.ActivePersonCardControl.Visibility = Visibility.Visible;
            await WaitForPersonCardLayoutAsync();

            if (_pendingPersonSource != sourceAtRequest || sourceAtRequest == null) return;

            _page.PlacePersonCard(_activePersonAnchorSource, animateMove: true);
            _page.ActivePersonCardControl.Opacity = 1;

            if (!isAlreadyVisible && visual != null)
            {
                var compositor = visual.Compositor;
                try { visual.StopAnimation("Translation"); } catch { }
                try { visual.Properties.InsertVector3("Translation", Vector3.Zero); } catch { }
                visual.Scale = new Vector3(0.85f, 0.85f, 1f);
                visual.Opacity = 0f;

                var springAnim = compositor.CreateSpringVector3Animation();
                springAnim.Target = "Scale"; springAnim.FinalValue = Vector3.One;
                springAnim.DampingRatio = 0.7f; springAnim.Period = TimeSpan.FromMilliseconds(50);

                var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
                fadeAnim.Target = "Opacity"; fadeAnim.InsertKeyFrame(1f, 1f);
                fadeAnim.Duration = TimeSpan.FromMilliseconds(150);

                visual.StartAnimation("Scale", springAnim);
                visual.StartAnimation("Opacity", fadeAnim);
            }
        }

        private async Task WaitForPersonCardLayoutAsync()
        {
            for (int i = 0; i < 3; i++)
            {
                MediaInfoPage.TryUpdateLayout(_page.PersonCardOverlayControl, "WaitForPersonCardLayout");
                MediaInfoPage.TryUpdateLayout(_page.ActivePersonCardControl, "WaitForPersonCardLayout");
                await Task.Yield();
            }
        }

        public void ClosePersonCard()
        {
            if (_disposed) return;
            _personHoverTimer.Stop();

            if (_activePersonAnchorSource != null)
            {
                var anchorVisual = ElementCompositionPreview.GetElementVisual(_activePersonAnchorSource);
                if (anchorVisual != null)
                {
                    var resetScale = anchorVisual.Compositor.CreateVector3KeyFrameAnimation();
                    resetScale.InsertKeyFrame(1f, new Vector3(1.0f, 1.0f, 1.0f));
                    resetScale.Duration = TimeSpan.FromMilliseconds(200);
                    anchorVisual.StartAnimation("Scale", resetScale);

                    var container = _page.FindParent<ListViewItem>(_activePersonAnchorSource);
                    if (container != null) Canvas.SetZIndex(container, 0);
                }
            }

            _pendingPersonSource = null;
            _activePersonAnchorSource = null;
            _page.ActivePersonCardItem = null;
            _page.SetPersonCardOverlayHitTestVisible(false);

            var visual = ElementCompositionPreview.GetElementVisual(_page.ActivePersonCardControl);
            if (visual != null && _page.ActivePersonCardControl.Visibility == Visibility.Visible)
            {
                var compositor = visual.Compositor;
                var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                fadeOut.Target = "Opacity"; fadeOut.InsertKeyFrame(1f, 0f); fadeOut.Duration = TimeSpan.FromMilliseconds(200);
                var scaleDown = compositor.CreateVector3KeyFrameAnimation();
                scaleDown.Target = "Scale"; scaleDown.InsertKeyFrame(1f, new Vector3(0.9f, 0.9f, 1.0f)); scaleDown.Duration = TimeSpan.FromMilliseconds(200);
                var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                visual.StartAnimation("Opacity", fadeOut);
                visual.StartAnimation("Scale", scaleDown);
                batch.End();
                batch.Completed += (s, e) =>
                {
                    _page.DispatcherQueue.TryEnqueue(() =>
                    {
                        _page.SetPersonCardOverlayVisibility(Visibility.Collapsed);
                        _page.SetPersonCardOverlayHitTestVisible(true);
                        _page.ActivePersonCardControl.Opacity = 0;
                    });
                };
            }
        }

        public async Task ClosePersonCardAsync(int delayMs = 600)
        {
            if (_disposed) return;
            _personCloseCts?.Cancel();
            _personCloseCts?.Dispose();
            _personCloseCts = new CancellationTokenSource();
            var token = _personCloseCts.Token;

            try
            {
                await Task.Delay(delayMs, token);
                if (_isPointerOverPersonCard) return;
                ClosePersonCard();
            }
            catch (TaskCanceledException) { }
        }

        #endregion

        #region Drag-to-Scroll

        private void SetupDragToScroll()
        {
            try
            {
                var castList = _page.CastListViewControl;
                if (castList == null) return;

                castList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnCastPointerPressed), true);
                castList.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnCastPointerMoved), true);
                castList.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnCastPointerReleased), true);
                castList.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnCastPointerReleased), true);
                castList.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnCastPointerReleased), true);
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error("[CAST-DIRECTOR] SetupDragToScroll error", ex);
            }
        }

        private void OnCastPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_disposed) return;
            var ptr = e.GetCurrentPoint(null);
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && ptr.Properties.IsLeftButtonPressed)
            {
                _isCastDragging = true;
                _lastCastPointerPos = ptr.Position;
                _page.AbortMainDragging();
            }
        }

        private void OnCastPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_disposed || !_isCastDragging) return;
            var ptr = e.GetCurrentPoint(null);
            if (!ptr.Properties.IsLeftButtonPressed)
            {
                _isCastDragging = false;
                try { _page.CastListViewControl?.ReleasePointerCapture(e.Pointer); } catch {}
                return;
            }

            double deltaX = _lastCastPointerPos.X - ptr.Position.X;
            if (Math.Abs(deltaX) > 3.0)
            {
                var castList = _page.CastListViewControl;
                if (castList.PointerCaptures == null || !castList.PointerCaptures.Any(c => c.PointerId == e.Pointer.PointerId))
                    castList.CapturePointer(e.Pointer);

                var scroll = _page.GetScrollViewer(castList);
                if (scroll != null)
                {
                    scroll.ChangeView(scroll.HorizontalOffset + deltaX, null, null, true);
                    _lastCastPointerPos = ptr.Position;
                    e.Handled = true;
                }
            }
        }

        private void OnCastPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_disposed) return;
            if (_isCastDragging)
            {
                _isCastDragging = false;
                _page.CastListViewControl?.ReleasePointerCapture(e.Pointer);
            }
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _personHoverTimer.Stop();
                _personCloseCts?.Cancel();
                _personCloseCts?.Dispose();

                var castList = _page.CastListViewControl;
                if (castList != null)
                {
                    castList.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnCastPointerPressed));
                    castList.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnCastPointerMoved));
                    castList.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnCastPointerReleased));
                    castList.RemoveHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnCastPointerReleased));
                    castList.RemoveHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnCastPointerReleased));
                }
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error("[CAST-DIRECTOR] Dispose error", ex);
            }

            ModernIPTVPlayer.Services.AppLogger.Info("[CAST-DIRECTOR] Disposed");
        }
    }
}
