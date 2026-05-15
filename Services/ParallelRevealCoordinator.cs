using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Coordinates parallel, event-driven reveal animations for page components.
    /// Gold Standard implementation: 
    /// 1. Awaits the "Loaded" state (Liveness Gate).
    /// 2. Ensures at least one layout pass occurred after metadata population.
    /// 3. Returns a completion Task (WhenAll) for synchronized state management.
    /// </summary>
    public sealed class ParallelRevealCoordinator : IDisposable
    {
        private readonly IRevealablePage _owner;
        private readonly List<RevealTask> _tasks = new();
        private CancellationTokenSource? _cts;

        public ParallelRevealCoordinator(IRevealablePage owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public void Initialize()
        {
            CancelCurrent();
            _cts = new CancellationTokenSource();
            _tasks.Clear();

            // 1. Identity Slot (Title/Logo)
            _tasks.Add(new RevealTask(_owner.TitlePanel, _owner.TitleShimmer, 0, 560, 0.84f, false, false, 
                owner => owner.WaitForIdentityReadyAsync(_cts.Token)));

            // 2. Metadata Slot
            _tasks.Add(new RevealTask(_owner.MetadataPanel, _owner.MetadataShimmer, 60, 340, 0.72f, false));

            // 4. Action Bar Slot
            _tasks.Add(new RevealTask(_owner.ActionBarPanel, _owner.ActionBarShimmer, 100, 460, 0.74f, false));

            // 5. Overview Slot
            _tasks.Add(new RevealTask(_owner.OverviewPanel, _owner.OverviewShimmer, 160, 600, 0.6f, false));

            // 6. Optional Sections (Director/Cast)
            if (_owner.DirectorSection != null) 
                _tasks.Add(new RevealTask(_owner.DirectorSection, _owner.DirectorShimmer, 240, 500, 0.64f, false));
            
            if (_owner.CastSection != null) 
                _tasks.Add(new RevealTask(_owner.CastSection, _owner.CastShimmer, 300, 500, 0.64f, false));
        }

        public void PrepareForLoading()
        {
            foreach (var task in _tasks)
            {
                task.Prepare();
            }
        }

        public void ShowReadyImmediate()
        {
            CancelCurrent();
            foreach (var task in _tasks)
            {
                task.FinalizeReady();
            }
        }

        public async Task StartRevealAsync()
        {
            if (_cts == null) return;
            var token = _cts.Token;
            // #region agent log
            App.DebugSessionNdjson("ParallelRevealCoordinator.cs:StartRevealAsync",
                "Parallel reveal started",
                new Dictionary<string, object?>
                {
                    ["ownerLoaded"] = _owner.IsLoaded,
                    ["taskCount"] = _tasks.Count,
                    ["tokenCanceled"] = token.IsCancellationRequested
                },
                "H6");
            // #endregion

            // Stage A: Ensure the page is Loaded (Liveness Gate)
            await WaitForPageLoadedAsync(token);

            // Stage B: Force a layout pass synchronization. 
            // Even if IsLoaded is true, we need to ensure the WinUI layout engine 
            // has processed the newly bound metadata text.
            await Task.Yield(); 
            _owner.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { }); 

            // Stage C: Run all reveal tasks in parallel
            var runningTasks = _tasks.Select(t => RunTaskAsync(t, token)).ToList();
            await Task.WhenAll(runningTasks);
        }

        public async Task RevealIdentityOnlyAsync()
        {
            if (_cts == null) return;
            var token = _cts.Token;

            var identityTask = _tasks.FirstOrDefault(t => t.Content == _owner.TitlePanel);
            if (identityTask != null)
            {
                await RunTaskAsync(identityTask, token);
            }
        }

        private async Task WaitForPageLoadedAsync(CancellationToken token)
        {
            if (_owner.IsLoaded) return;

            var tcs = new TaskCompletionSource<bool>();
            RoutedEventHandler handler = (s, e) => tcs.TrySetResult(true);

            if (_owner is FrameworkElement page)
            {
                page.Loaded += handler;
                try { await tcs.Task.WaitAsync(token); }
                finally { page.Loaded -= handler; }
            }
        }

        private async Task RunTaskAsync(RevealTask task, CancellationToken token)
        {
            try
            {
                bool isIdentityTask = task.Content == _owner.TitlePanel;
                if (isIdentityTask)
                {
                    // #region agent log
                    App.DebugSessionNdjson("ParallelRevealCoordinator.cs:RunTaskAsync",
                        "Identity reveal task waiting for readiness",
                        new Dictionary<string, object?>
                        {
                            ["ownerLoaded"] = _owner.IsLoaded,
                            ["contentName"] = task.Content?.Name,
                            ["contentLoaded"] = task.Content?.IsLoaded,
                            ["contentXamlRootReady"] = task.Content?.XamlRoot != null,
                            ["skeletonName"] = task.Skeleton?.Name,
                            ["tokenCanceled"] = token.IsCancellationRequested
                        },
                        "H6");
                    // #endregion
                }
                // Stage 1: Wait for Element-specific readiness
                await task.WaitForReadinessAsync(_owner, token);
                if (token.IsCancellationRequested) return;

                if (isIdentityTask)
                {
                    // #region agent log
                    App.DebugSessionNdjson("ParallelRevealCoordinator.cs:RunTaskAsync",
                        "Identity reveal task readiness completed",
                        new Dictionary<string, object?>
                        {
                            ["ownerLoaded"] = _owner.IsLoaded,
                            ["contentName"] = task.Content?.Name,
                            ["contentLoaded"] = task.Content?.IsLoaded,
                            ["contentXamlRootReady"] = task.Content?.XamlRoot != null,
                            ["contentVisibility"] = task.Content?.Visibility.ToString(),
                            ["tokenCanceled"] = token.IsCancellationRequested
                        },
                        "H6");
                    // #endregion
                }

                // [LIFECYCLE GUARD] If the section was logically collapsed by the metadata sync
                // (e.g. no cast data found), we abort the reveal to prevent a frame-one flash.
                if (task.Content != null && task.Content.Visibility == Visibility.Collapsed)
                {
                    return;
                }

                // Stage 2: Execute Reveal Animation with Scoped Batch
                await task.AnimateRevealAsync(_owner, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PARALLEL-COORD] Task failed: {task.Content?.Name ?? "Unknown"}. Error: {ex.Message}");
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    if (task.Content == _owner.TitlePanel)
                    {
                        // #region agent log
                        App.DebugSessionNdjson("ParallelRevealCoordinator.cs:RunTaskAsync",
                            "Identity reveal task finalizing ready state",
                            new Dictionary<string, object?>
                            {
                                ["ownerLoaded"] = _owner.IsLoaded,
                                ["contentName"] = task.Content?.Name,
                                ["contentLoaded"] = task.Content?.IsLoaded,
                                ["contentXamlRootReady"] = task.Content?.XamlRoot != null,
                                ["skeletonName"] = task.Skeleton?.Name,
                                ["tokenCanceled"] = token.IsCancellationRequested
                            },
                            "H6");
                        // #endregion
                    }
                    task.FinalizeReady();
                }
            }
        }

        public void MatchTitleSkeletonToContent()
        {
            var identityTask = _tasks.FirstOrDefault(t => t.Content == _owner.TitlePanel);
            if (identityTask != null && identityTask.Skeleton != null && identityTask.Content != null)
            {
                // [NATIVE AOT SAFE] We use the same matching logic used elsewhere
                MatchSkeletonToContent(identityTask.Skeleton, identityTask.Content, 48);
            }
        }

        private void MatchSkeletonToContent(FrameworkElement skeleton, FrameworkElement content, double minHeight)
        {
            if (skeleton == null || content == null) return;
            skeleton.Width = Math.Max(120, content.ActualWidth);
            skeleton.Height = Math.Max(minHeight, content.ActualHeight);
        }

        public void CancelCurrent()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        public void Dispose()
        {
            CancelCurrent();
        }

        private class RevealTask
        {
            public FrameworkElement Content { get; }
            public FrameworkElement Skeleton { get; }
            public int DelayMs { get; }
            public int DurationMs { get; }
            public float StartOpacity { get; }
            public bool AnimateTranslation { get; }
            public Func<IRevealablePage, Task>? DataTrigger { get; }

            public RevealTask(FrameworkElement content, FrameworkElement skeleton, int delay, int duration, float opacity, 
                bool animateTranslation = true, bool collapse = true, Func<IRevealablePage, Task>? dataTrigger = null)
            {
                Content = content;
                Skeleton = skeleton;
                DelayMs = delay;
                DurationMs = duration;
                StartOpacity = opacity;
                AnimateTranslation = animateTranslation;
                DataTrigger = dataTrigger;
            }

            public void Prepare()
            {
                if (Content == null || Skeleton == null) return;
                
                // Stage 1: Immediate XAML Reset (NativeAOT Safe)
                // We set Opacity = 0 to hide the old item's content immediately.
                // We show the skeleton/shimmer.
                Content.Opacity = 0;
                Skeleton.Visibility = Visibility.Visible;
                Skeleton.Opacity = 1;

                // We do NOT touch Composition here because the element might not be loaded.
                // This eliminates the "Skip" logs and redundant GPU work during navigation reset.
            }

            public async Task WaitForReadinessAsync(IRevealablePage owner, CancellationToken token)
            {
                if (Content == null) return;

                // Stage 2: The Liveness Gate (Event Driven)
                // We wait for the element to enter the Visual Tree.
                if (!Content.IsLoaded)
                {
                    var tcs = new TaskCompletionSource<bool>();
                    RoutedEventHandler handler = (s, e) => tcs.TrySetResult(true);
                    Content.Loaded += handler;
                    try { await tcs.Task.WaitAsync(token); }
                    finally { Content.Loaded -= handler; }
                }

                // Stage 3: Professional Composition Reset
                // Now that we are GUARANTEED to be loaded, we safely access the Visual.
                CompositionService.Run(Content, visual => 
                {
                    CompositionService.StopAll(visual);
                    visual.Opacity = 0f;
                    visual.Offset = Vector3.Zero;
                    visual.Clip = null;
                });

                if (Skeleton != null)
                {
                    CompositionService.Run(Skeleton, visual => 
                    {
                        CompositionService.StopAll(visual);
                        visual.Opacity = 1f;
                    });
                }

                // Stage 4: Data Readiness (Logo Ready, etc.)
                if (DataTrigger != null)
                {
                    await DataTrigger(owner);
                    owner.MatchTitleSkeletonToContent();
                }

                // Stage 5: Final Layout Sync
                if (Content.ActualWidth <= 0)
                {
                    var sizeTcs = new TaskCompletionSource<bool>();
                    SizeChangedEventHandler sizeHandler = (s, e) => { if (e.NewSize.Width > 0) sizeTcs.TrySetResult(true); };
                    Content.SizeChanged += sizeHandler;
                    try { await sizeTcs.Task.WaitAsync(token); }
                    finally { Content.SizeChanged -= sizeHandler; }
                }
            }

            public Task AnimateRevealAsync(IRevealablePage owner, CancellationToken token)
            {
                // Stage 1: Logical Visibility Switch (XAML Layer)
                // We only proceed if the content is intended to be visible.
                if (Content != null)
                {
                    if (Content.Visibility != Visibility.Visible) return Task.CompletedTask;
                    
                    // Opacity is managed by the visual layer to prevent a frame-one flash.
                    CompositionService.Run(Content, v => v.Opacity = StartOpacity);
                }

                var completionTcs = new TaskCompletionSource<bool>();

                CompositionService.Run(Content, visual =>
                {
                    var compositor = visual.Compositor;
                    var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                    batch.Completed += (s, e) => completionTcs.TrySetResult(true);

                    var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 0.86f), new Vector2(0.16f, 1.0f));
                    var delayTime = TimeSpan.FromMilliseconds(DelayMs);

                    // [NATIVE AOT SAFE] Avoid direct casting if we can't be sure of the projected type
                    var clip = visual.Clip as InsetClip;
                    if (clip == null)
                    {
                        clip = compositor.CreateInsetClip();
                        visual.Clip = clip;
                    }

                    if (clip != null)
                    {
                        var wipe = compositor.CreateScalarKeyFrameAnimation();
                        wipe.InsertKeyFrame(0f, (float)Math.Max(1, Content.ActualWidth));
                        wipe.InsertKeyFrame(1f, 0f, easing);
                        wipe.Duration = TimeSpan.FromMilliseconds(DurationMs);
                        wipe.DelayTime = delayTime;
                        clip.StartAnimation(nameof(InsetClip.RightInset), wipe);
                    }

                    var fade = compositor.CreateScalarKeyFrameAnimation();
                    fade.InsertKeyFrame(0f, StartOpacity);
                    fade.InsertKeyFrame(1f, 1f, easing);
                    fade.Duration = TimeSpan.FromMilliseconds(DurationMs);
                    fade.DelayTime = delayTime;
                    visual.StartAnimation(CompositionService.OpacityProperty, fade);

                    if (AnimateTranslation)
                    {
                        visual.Offset = new Vector3(24, 0, 0);
                        var spring = compositor.CreateSpringVector3Animation();
                        spring.FinalValue = Vector3.Zero;
                        spring.DampingRatio = 0.8f;
                        spring.Period = TimeSpan.FromMilliseconds(50);
                        spring.DelayTime = delayTime;
                        visual.StartAnimation(CompositionService.OffsetProperty, spring);
                    }
                    batch.End();
                });

                CompositionService.Run(Skeleton, visual =>
                {
                    var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
                    fade.InsertKeyFrame(0f, 1f);
                    fade.InsertKeyFrame(1f, 0f);
                    fade.Duration = TimeSpan.FromMilliseconds(400);
                    fade.DelayTime = TimeSpan.FromMilliseconds(DelayMs);
                    visual.StartAnimation(CompositionService.OpacityProperty, fade);
                });

                // Safety: If Composition layer was skipped, resolve immediately
                if (completionTcs.Task.Status == TaskStatus.Created) completionTcs.TrySetResult(false);

                return completionTcs.Task;
            }

            public void FinalizeReady()
            {
                if (Content != null) Content.Opacity = 1;
                if (Skeleton != null) Skeleton.Visibility = Visibility.Collapsed;

                CompositionService.Run(Content, v =>
                {
                    v.Opacity = 1f;
                    v.Offset = Vector3.Zero;
                    v.Clip = null;
                });
            }
        }
    }
}
