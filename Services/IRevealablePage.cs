using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Represents a UI slot that can be revealed with an animation.
    /// </summary>
    public class RevealSlot
    {
        public FrameworkElement Content { get; }
        public FrameworkElement Skeleton { get; }
        public int DelayMs { get; }
        public int DurationMs { get; }
        public float StartOpacity { get; }
        public bool CollapseWhenContentHidden { get; }
        public bool AnimateTranslation { get; }

        public RevealSlot(
            FrameworkElement content, 
            FrameworkElement skeleton, 
            int delayMs, 
            int durationMs, 
            float startOpacity = 0.6f, 
            bool collapseWhenContentHidden = true,
            bool AnimateTranslation = true)
        {
            Content = content;
            Skeleton = skeleton;
            DelayMs = delayMs;
            DurationMs = durationMs;
            StartOpacity = startOpacity;
            CollapseWhenContentHidden = collapseWhenContentHidden;
            this.AnimateTranslation = AnimateTranslation;
        }
    }

    /// <summary>
    /// Defines the contract for a page that supports staggered reveal animations.
    /// </summary>
    public interface IRevealablePage
    {
        Task PrepareInfoSkeletonForRevealAsync();
        bool IsIdentityRevealReady();
        void MatchTitleSkeletonToContent();
        
        // Element accessors for the orchestrator
        FrameworkElement TitlePanel { get; }
        FrameworkElement TitleShimmer { get; }
        FrameworkElement TechBadgesContent { get; }
        FrameworkElement TechBadgesShimmer { get; }
        FrameworkElement MetadataPanel { get; }
        FrameworkElement MetadataShimmer { get; }
        FrameworkElement ActionBarPanel { get; }
        FrameworkElement ActionBarShimmer { get; }
        FrameworkElement OverviewPanel { get; }
        FrameworkElement OverviewShimmer { get; }
        FrameworkElement DirectorSection { get; }
        FrameworkElement DirectorShimmer { get; }
        FrameworkElement CastSection { get; }
        FrameworkElement CastShimmer { get; }

        // Lifecycle & Threading
        Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; }
        bool IsLoaded { get; }
        Task WaitForIdentityReadyAsync(CancellationToken token);
    }
}
