using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Registry of all UI sections on the MediaInfoPage.
    /// Provides centralized access and bulk operations for section lifecycle management.
    /// </summary>
    internal sealed class SectionRegistry : IDisposable
    {
        #region Fields

        private readonly Dictionary<string, SectionController> _sections = new();
        private bool _disposed;

        #endregion

        #region Public API

        /// <summary>
        /// Registers a section with its panel, content, and skeleton elements.
        /// </summary>
        public void Register(string name, FrameworkElement panel, FrameworkElement content,
            FrameworkElement skeleton, Compositor compositor, int displayOrder)
        {
            ThrowIfDisposed();

            if (_sections.ContainsKey(name))
            {
                Debug.WriteLine($"[SECTION-REGISTRY] Section '{name}' already registered, replacing");
                _sections[name].Dispose();
            }

            var controller = new SectionController(name, panel, content, skeleton, compositor, displayOrder);
            _sections[name] = controller;

            Debug.WriteLine($"[SECTION-REGISTRY] Registered '{name}' (order: {displayOrder})");
        }

        /// <summary>
        /// Gets a registered section controller by name.
        /// </summary>
        public SectionController Get(string name)
        {
            ThrowIfDisposed();

            if (!_sections.TryGetValue(name, out var controller))
            {
                Debug.WriteLine($"[SECTION-REGISTRY] Section '{name}' not found");
                return null;
            }

            return controller;
        }

        /// <summary>
        /// Returns all registered section controllers.
        /// </summary>
        public IEnumerable<SectionController> All
        {
            get
            {
                ThrowIfDisposed();
                return _sections.Values;
            }
        }

        /// <summary>
        /// Sets the dispatcher used for batch scheduling across all sections.
        /// </summary>
        public void SetDispatcher(DispatcherQueue dispatcher)
        {
            SectionController.SetDispatcher(dispatcher);
        }

        /// <summary>
        /// Animates all info panel sections sliding in with skeletons visible.
        /// </summary>
        public void ShowAllPanelsAnimated()
        {
            ThrowIfDisposed();

            foreach (var section in _sections.Values)
            {
                try
                {
                    section.ShowPanelAnimated();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SECTION-REGISTRY] ShowPanelAnimated for '{section.Name}' failed: {ex.Message}");
                }
            }

            Debug.WriteLine("[SECTION-REGISTRY] All panels slide-in triggered");
        }

        /// <summary>
        /// Shows skeletons for all sections immediately.
        /// </summary>
        public void ShowAllSkeletons()
        {
            ThrowIfDisposed();

            foreach (var section in _sections.Values)
            {
                try
                {
                    section.ShowSkeleton();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SECTION-REGISTRY] ShowSkeleton for '{section.Name}' failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Schedules all sections with skeletons for content crossfade.
        /// </summary>
        public void RevealAllContent()
        {
            ThrowIfDisposed();

            foreach (var section in _sections.Values)
            {
                try
                {
                    if (section.IsShowingSkeleton)
                    {
                        section.RevealContent();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SECTION-REGISTRY] RevealContent for '{section.Name}' failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Dismisses all sections.
        /// </summary>
        public void HideAll()
        {
            ThrowIfDisposed();

            foreach (var section in _sections.Values)
            {
                try
                {
                    section.Hide();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SECTION-REGISTRY] Hide for '{section.Name}' failed: {ex.Message}");
                }
            }

            Debug.WriteLine("[SECTION-REGISTRY] All panels dismissed");
        }

        /// <summary>
        /// Flushes any pending reveal batches immediately.
        /// </summary>
        public void FlushPendingReveals()
        {
            SectionController.FlushPendingReveals();
        }

        #endregion

        #region IDisposable

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SectionRegistry));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var section in _sections.Values)
            {
                section.Dispose();
            }

            _sections.Clear();
            Debug.WriteLine("[SECTION-REGISTRY] Disposed");
        }

        #endregion
    }
}
