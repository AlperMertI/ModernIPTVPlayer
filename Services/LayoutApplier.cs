using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Supplies UI element references to the LayoutApplier.
    /// Decouples the applier from direct page field access.
    /// </summary>
    internal sealed class LayoutElements
    {
        public ColumnDefinition Col0 { get; set; }
        public ColumnDefinition Col1 { get; set; }
        public RowDefinition Row0 { get; set; }
        public RowDefinition Row1 { get; set; }
        public RowDefinition Row2 { get; set; }
        public RowDefinition Row3 { get; set; }
        public RowDefinition Row4 { get; set; }
        public Grid ContentGrid { get; set; }
        public ScrollViewer RootScrollViewer { get; set; }
        public Grid InfoContainer { get; set; }
        public Grid SourcesPanel { get; set; }
        public Grid EpisodesPanel { get; set; }
        public StackPanel NarrowSectionsContainer { get; set; }
        public TextBlock OverviewText { get; set; }
        public TextBlock GenresText { get; set; }
        public StackPanel InfoColumn { get; set; }
        public Controls.MediaIdentityControl IdentityControl { get; set; }
        public StackPanel MetadataRibbon { get; set; }
        public Grid ActionBarGroup { get; set; }
        public StackPanel ActionBarPanel { get; set; }
        public Grid InfoContainerInner { get; set; }
        public Grid AdaptiveInfoHost { get; set; }
        public FrameworkElement CastSection { get; set; }
        public FrameworkElement CastShimmer { get; set; }
        public FrameworkElement DirectorSection { get; set; }
        public FrameworkElement DirectorShimmer { get; set; }
        public Button BtnHideSources { get; set; }
        public Button BtnBackToEpisodes { get; set; }
        public FrameworkElement SourcesShowHandle { get; set; }
        public FrameworkElement MetadataPanel { get; set; }
        public FrameworkElement OverviewPanel { get; set; }
    }

    /// <summary>
    /// Applies layout decisions to UI elements with diff-based property updates.
    /// Only mutates properties that have actually changed from the previous decision.
    /// </summary>
    internal sealed class LayoutApplier : IDisposable
    {
        #region Fields

        private readonly LayoutElements _elements;
        private LayoutDecision _lastDecision;
        private bool _hasPreviousDecision;
        private bool _disposed;

        #endregion

        #region Constructor

        public LayoutApplier(LayoutElements elements)
        {
            _elements = elements ?? throw new ArgumentNullException(nameof(elements));
        }

        #endregion

        #region Public API

        public void Apply(LayoutDecision decision)
        {
            if (_disposed) return;

            try
            {
                ApplyGridChanges(decision);
                ApplyPanelPlacement(decision);
                ApplyVisualProperties(decision);
                ApplyVisibilityChanges(decision);

                _lastDecision = decision;
                _hasPreviousDecision = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LAYOUT-APPLIER] Apply failed: {ex.Message}");
            }
        }

        #endregion

        #region Grid Changes

        private void ApplyGridChanges(LayoutDecision decision)
        {
            if (!_hasPreviousDecision)
            {
                ApplyGridColumnDefinitions(decision.Grid);
                ApplyGridRowDefinitions(decision.Grid);
                ApplyContentGridPadding(decision.Grid.ContentGridPadding);
                ApplyScrollViewerConfig(decision.Grid.ScrollBarVisibility, decision.Grid.ScrollMode);
                return;
            }

            var prev = _lastDecision.Grid;
            var curr = decision.Grid;

            if (!AreGridLengthsEqual(prev.Col0Width, curr.Col0Width) && _elements.Col0 != null)
                _elements.Col0.Width = curr.Col0Width;

            if (!AreGridLengthsEqual(prev.Col1Width, curr.Col1Width) && _elements.Col1 != null)
            {
                _elements.Col1.Width = curr.Col1Width;
                _elements.Col1.MinWidth = curr.Col1MinWidth;
                _elements.Col1.MaxWidth = curr.Col1MaxWidth;
            }

            if (!AreRowHeightsEqual(prev.RowHeights, curr.RowHeights))
                ApplyGridRowDefinitions(curr);

            if (prev.ContentGridPadding != curr.ContentGridPadding)
                ApplyContentGridPadding(curr.ContentGridPadding);

            if (prev.ScrollBarVisibility != curr.ScrollBarVisibility)
                ApplyScrollViewerConfig(curr.ScrollBarVisibility, curr.ScrollMode);
        }

        private void ApplyGridColumnDefinitions(GridConfig config)
        {
            if (_elements.Col0 != null) _elements.Col0.Width = config.Col0Width;
            if (_elements.Col1 != null)
            {
                _elements.Col1.Width = config.Col1Width;
                _elements.Col1.MinWidth = config.Col1MinWidth;
                _elements.Col1.MaxWidth = config.Col1MaxWidth;
            }
        }

        private void ApplyGridRowDefinitions(GridConfig config)
        {
            if (_elements.Row0 != null && config.RowHeights.Length > 0) _elements.Row0.Height = config.RowHeights[0];
            if (_elements.Row1 != null && config.RowHeights.Length > 1) _elements.Row1.Height = config.RowHeights[1];
            if (_elements.Row2 != null && config.RowHeights.Length > 2) _elements.Row2.Height = config.RowHeights[2];
            if (_elements.Row3 != null && config.RowHeights.Length > 3) _elements.Row3.Height = config.RowHeights[3];
            if (_elements.Row4 != null && config.RowHeights.Length > 4) _elements.Row4.Height = config.RowHeights[4];
        }

        private void ApplyContentGridPadding(Thickness padding)
        {
            if (_elements.ContentGrid != null) _elements.ContentGrid.Padding = padding;
        }

        private void ApplyScrollViewerConfig(ScrollBarVisibility scrollBarVisibility, ScrollMode scrollMode)
        {
            if (_elements.RootScrollViewer != null)
            {
                _elements.RootScrollViewer.VerticalScrollBarVisibility = scrollBarVisibility;
                _elements.RootScrollViewer.VerticalScrollMode = scrollMode;
            }
        }

        private static bool AreGridLengthsEqual(GridLength a, GridLength b)
        {
            return a.GridUnitType == b.GridUnitType && Math.Abs(a.Value - b.Value) < 0.01;
        }

        private static bool AreRowHeightsEqual(GridLength[] a, GridLength[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!AreGridLengthsEqual(a[i], b[i])) return false;
            }
            return true;
        }

        #endregion

        #region Panel Placement

        private void ApplyPanelPlacement(LayoutDecision decision)
        {
            if (!_hasPreviousDecision)
            {
                ApplyPanelPlacementValues(decision.Placement);
                return;
            }

            var prev = _lastDecision.Placement;
            var curr = decision.Placement;

            if (prev.InfoRow != curr.InfoRow || prev.InfoColumn != curr.InfoColumn || prev.InfoColumnSpan != curr.InfoColumnSpan)
            {
                if (_elements.InfoContainer != null)
                {
                    Grid.SetRow(_elements.InfoContainer, curr.InfoRow);
                    Grid.SetColumn(_elements.InfoContainer, curr.InfoColumn);
                    Grid.SetColumnSpan(_elements.InfoContainer, curr.InfoColumnSpan);
                }
            }

            if (prev.SourcesRow != curr.SourcesRow || prev.SourcesColumn != curr.SourcesColumn || prev.SourcesColumnSpan != curr.SourcesColumnSpan)
            {
                if (_elements.SourcesPanel != null)
                {
                    Grid.SetRow(_elements.SourcesPanel, curr.SourcesRow);
                    Grid.SetColumn(_elements.SourcesPanel, curr.SourcesColumn);
                    Grid.SetColumnSpan(_elements.SourcesPanel, curr.SourcesColumnSpan);
                }
            }

            if (prev.EpisodesRow != curr.EpisodesRow || prev.EpisodesColumn != curr.EpisodesColumn || prev.EpisodesColumnSpan != curr.EpisodesColumnSpan)
            {
                if (_elements.EpisodesPanel != null)
                {
                    Grid.SetRow(_elements.EpisodesPanel, curr.EpisodesRow);
                    Grid.SetColumn(_elements.EpisodesPanel, curr.EpisodesColumn);
                    Grid.SetColumnSpan(_elements.EpisodesPanel, curr.EpisodesColumnSpan);
                }
            }

            if (prev.NarrowSectionsVisible != curr.NarrowSectionsVisible)
            {
                if (_elements.NarrowSectionsContainer != null)
                    _elements.NarrowSectionsContainer.Visibility = curr.NarrowSectionsVisible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ApplyPanelPlacementValues(PanelPlacement placement)
        {
            if (_elements.InfoContainer != null)
            {
                Grid.SetRow(_elements.InfoContainer, placement.InfoRow);
                Grid.SetColumn(_elements.InfoContainer, placement.InfoColumn);
                Grid.SetColumnSpan(_elements.InfoContainer, placement.InfoColumnSpan);
            }

            if (_elements.SourcesPanel != null)
            {
                Grid.SetRow(_elements.SourcesPanel, placement.SourcesRow);
                Grid.SetColumn(_elements.SourcesPanel, placement.SourcesColumn);
                Grid.SetColumnSpan(_elements.SourcesPanel, placement.SourcesColumnSpan);
            }

            if (_elements.EpisodesPanel != null)
            {
                Grid.SetRow(_elements.EpisodesPanel, placement.EpisodesRow);
                Grid.SetColumn(_elements.EpisodesPanel, placement.EpisodesColumn);
                Grid.SetColumnSpan(_elements.EpisodesPanel, placement.EpisodesColumnSpan);
            }

            if (_elements.NarrowSectionsContainer != null)
                _elements.NarrowSectionsContainer.Visibility = placement.NarrowSectionsVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region Visual Properties

        private void ApplyVisualProperties(LayoutDecision decision)
        {
            if (!_hasPreviousDecision)
            {
                ApplyVisualPropertiesValues(decision.Visual);
                return;
            }

            var prev = _lastDecision.Visual;
            var curr = decision.Visual;

            if (prev.IsWide != curr.IsWide)
            {
                ApplyVisualPropertiesValues(curr);
            }
        }

        private void ApplyVisualPropertiesValues(VisualProperties v)
        {
            if (_elements.OverviewText != null) _elements.OverviewText.TextAlignment = v.OverviewTextAlignment;
            if (_elements.GenresText != null) _elements.GenresText.TextAlignment = v.GenresTextAlignment;

            if (_elements.InfoColumn != null)
            {
                _elements.InfoColumn.MaxWidth = v.InfoColumnMaxWidth;
                _elements.InfoColumn.HorizontalAlignment = v.InfoColumnHAlign;
                _elements.InfoColumn.Spacing = v.InfoColumnSpacing;
            }

            if (_elements.IdentityControl != null) _elements.IdentityControl.HorizontalAlignment = v.IdentityControlHAlign;

            if (_elements.MetadataRibbon != null)
            {
                _elements.MetadataRibbon.HorizontalAlignment = v.MetadataRibbonHAlign;
                _elements.MetadataRibbon.Margin = v.MetadataRibbonMargin;
            }

            if (_elements.ActionBarGroup != null) _elements.ActionBarGroup.HorizontalAlignment = v.ActionBarHAlign;
            if (_elements.ActionBarPanel != null) _elements.ActionBarPanel.HorizontalAlignment = v.ActionBarHAlign;

            if (_elements.InfoContainerInner != null)
            {
                _elements.InfoContainerInner.VerticalAlignment = v.InfoContainerInnerVAlign;
                _elements.InfoContainerInner.HorizontalAlignment = v.InfoContainerInnerHAlign;
                _elements.InfoContainerInner.Margin = v.InfoContainerInnerMargin;
            }

            if (_elements.AdaptiveInfoHost != null)
            {
                _elements.AdaptiveInfoHost.Width = v.AdaptiveInfoHostWidth;
                _elements.AdaptiveInfoHost.VerticalAlignment = v.AdaptiveInfoHostVAlign;
                _elements.AdaptiveInfoHost.HorizontalAlignment = v.AdaptiveInfoHostHAlign;
            }

            if (_elements.EpisodesPanel != null)
            {
                _elements.EpisodesPanel.VerticalAlignment = v.EpisodesPanelVAlign;
                _elements.EpisodesPanel.HorizontalAlignment = v.EpisodesPanelHAlign;
                _elements.EpisodesPanel.Width = v.EpisodesPanelWidth;
                _elements.EpisodesPanel.MaxWidth = v.EpisodesPanelMaxWidth;
                _elements.EpisodesPanel.Margin = v.EpisodesPanelMargin;
                Debug.WriteLine($"[LAYOUT-APPLIER] EpisodesPanel props: Vis={_elements.EpisodesPanel.Visibility}, Opacity={_elements.EpisodesPanel.Opacity}, VA={v.EpisodesPanelVAlign}, Width={v.EpisodesPanelWidth}");
            }

            if (_elements.SourcesPanel != null)
            {
                _elements.SourcesPanel.VerticalAlignment = v.SourcesPanelVAlign;
                _elements.SourcesPanel.HorizontalAlignment = v.SourcesPanelHAlign;
                _elements.SourcesPanel.Width = v.SourcesPanelWidth;
                _elements.SourcesPanel.MaxWidth = v.SourcesPanelMaxWidth;
                _elements.SourcesPanel.Margin = v.SourcesPanelMargin;
            }
        }

        #endregion

        #region Visibility Changes

        private void ApplyVisibilityChanges(LayoutDecision decision)
        {
            if (!_hasPreviousDecision)
            {
                ApplyVisibilityValues(decision.Visibility);
                return;
            }

            var prev = _lastDecision.Visibility;
            var curr = decision.Visibility;

            SetVisibilityIfChanged(_elements.InfoContainer, prev.InfoContainer, curr.InfoContainer);
            SetVisibilityIfChanged(_elements.CastSection, prev.CastSection, curr.CastSection);
            SetVisibilityIfChanged(_elements.CastShimmer, prev.CastShimmer, curr.CastShimmer);
            SetVisibilityIfChanged(_elements.DirectorSection, prev.DirectorSection, curr.DirectorSection);
            SetVisibilityIfChanged(_elements.DirectorShimmer, prev.DirectorShimmer, curr.DirectorShimmer);
            SetVisibilityIfChanged(_elements.NarrowSectionsContainer, prev.NarrowSectionsContainer, curr.NarrowSectionsContainer);
            SetVisibilityIfChanged(_elements.BtnHideSources, prev.BtnHideSources, curr.BtnHideSources);
            SetVisibilityIfChanged(_elements.BtnBackToEpisodes, prev.BtnBackToEpisodes, curr.BtnBackToEpisodes);
            SetVisibilityIfChanged(_elements.SourcesShowHandle, prev.SourcesShowHandle, curr.SourcesShowHandle);
            SetVisibilityIfChanged(_elements.IdentityControl, prev.IdentityControl, curr.IdentityControl);
            SetVisibilityIfChanged(_elements.MetadataPanel, prev.MetadataPanel, curr.MetadataPanel);
            SetVisibilityIfChanged(_elements.OverviewPanel, prev.OverviewPanel, curr.OverviewPanel);
            SetVisibilityIfChanged(_elements.ActionBarPanel, prev.ActionBarPanel, curr.ActionBarPanel);
        }

        private void ApplyVisibilityValues(VisibilityMap v)
        {
            SetVisibility(_elements.InfoContainer, v.InfoContainer);
            SetVisibility(_elements.CastSection, v.CastSection);
            SetVisibility(_elements.CastShimmer, v.CastShimmer);
            SetVisibility(_elements.DirectorSection, v.DirectorSection);
            SetVisibility(_elements.DirectorShimmer, v.DirectorShimmer);
            SetVisibility(_elements.NarrowSectionsContainer, v.NarrowSectionsContainer);
            SetVisibility(_elements.BtnHideSources, v.BtnHideSources);
            SetVisibility(_elements.BtnBackToEpisodes, v.BtnBackToEpisodes);
            SetVisibility(_elements.SourcesShowHandle, v.SourcesShowHandle);
            SetVisibility(_elements.IdentityControl, v.IdentityControl);
            SetVisibility(_elements.MetadataPanel, v.MetadataPanel);
            SetVisibility(_elements.OverviewPanel, v.OverviewPanel);
            SetVisibility(_elements.ActionBarPanel, v.ActionBarPanel);
        }

        private static void SetVisibilityIfChanged(FrameworkElement element, Visibility prev, Visibility curr)
        {
            if (prev != curr) SetVisibility(element, curr);
        }

        private static void SetVisibility(FrameworkElement element, Visibility visibility)
        {
            if (element != null && element.Visibility != visibility)
            {
                Debug.WriteLine($"[LAYOUT-APPLIER] SetVisibility {element.Name}: {element.Visibility} -> {visibility}");
                element.Visibility = visibility;
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _disposed = true;
        }

        #endregion
    }
}
