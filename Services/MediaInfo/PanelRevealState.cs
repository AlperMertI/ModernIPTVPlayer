using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Tracks the reveal animation state for a single detail panel (Sources or Episodes).
    /// Each panel gets its own instance so generations, reveal limits, and presentation
    /// timestamps are isolated and correctly named.
    /// </summary>
    internal sealed class PanelRevealState
    {
        #region Fields

        private int _visualGeneration;
        private int _revealItemLimit;
        private readonly HashSet<int> _animatedRevealIndexes = new();
        private DateTime _presentationTime;

        #endregion

        #region Properties

        /// <summary>
        /// Monotonically increasing counter incremented each time the panel's visual
        /// content is replaced. Used to stamp ItemsRepeater elements so stale items
        /// can be distinguished from fresh ones.
        /// </summary>
        public int VisualGeneration
        {
            get => _visualGeneration;
            set => _visualGeneration = value;
        }

        /// <summary>
        /// Maximum number of items that receive the staggered reveal animation.
        /// Items beyond this threshold appear instantly to avoid scroll jitter.
        /// </summary>
        public int RevealItemLimit
        {
            get => _revealItemLimit;
            set => _revealItemLimit = value;
        }

        /// <summary>
        /// Set of item indexes that have already been animated. Prevents re-animating
        /// the same element when the ItemsRepeater recycles containers.
        /// </summary>
        public HashSet<int> AnimatedRevealIndexes => _animatedRevealIndexes;

        /// <summary>
        /// Timestamp of the last time the panel was opened with real data.
        /// Used to determine whether the current reveal is an "initial" reveal
        /// (within 3 seconds) or a subsequent data refresh.
        /// </summary>
        public DateTime PresentationTime
        {
            get => _presentationTime;
            set => _presentationTime = value;
        }

        #endregion

        #region Lifecycle

        /// <summary>
        /// Resets all state in preparation for a new panel open operation.
        /// Should be called before binding a fresh collection to the ItemsRepeater.
        /// </summary>
        /// <param name="itemCount">Number of items in the incoming collection.</param>
        public void PrepareOpen(int itemCount)
        {
            _presentationTime = DateTime.Now;
            _revealItemLimit = Math.Min(35, itemCount);
            _visualGeneration++;
            _animatedRevealIndexes.Clear();
        }

        /// <summary>
        /// Performs a full reset without setting a presentation timestamp.
        /// Used when the panel is being cleared or disposed.
        /// </summary>
        public void Reset()
        {
            _visualGeneration++;
            _animatedRevealIndexes.Clear();
            _revealItemLimit = 0;
            _presentationTime = DateTime.MinValue;
        }

        /// <summary>
        /// Returns true if the current reveal is still within the "initial reveal"
        /// window (3 seconds from <see cref="PresentationTime"/>).
        /// </summary>
        public bool IsInitialReveal => (DateTime.Now - _presentationTime).TotalMilliseconds < 3000;

        #endregion
    }
}
