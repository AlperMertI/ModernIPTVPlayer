using System.Collections.Generic;
using Microsoft.UI.Xaml;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services.Stremio;
using System;
using System.Linq;
using System.Threading.Tasks;
using ModernIPTVPlayer.Pages;

namespace ModernIPTVPlayer.Controls
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed partial class SpotlightSearchControl : UserControl
    {
        public event EventHandler<IMediaStream> ItemClicked;
        public event EventHandler<SearchArgs> SeeAllClicked;

        private DispatcherTimer _debounceTimer;
        private string _lastQuery;
        private System.Collections.ObjectModel.ObservableCollection<IMediaStream> _resultsCollection = new();
        private System.Threading.CancellationTokenSource _searchCts;
        
        private string _searchType = "all"; // all, movie, series
        private string _searchScope = "all"; // all, iptv

        public SpotlightSearchControl()
        {
            this.InitializeComponent();
            ResultsList.ItemsSource = _resultsCollection;

            _debounceTimer = new DispatcherTimer();
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(400); // 400ms debounce
            _debounceTimer.Tick += DebounceTimer_Tick;
        }

        public void Show()
        {
            this.Visibility = Visibility.Visible;
            OpenAnimation.Begin();
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
        }

        public async void Hide()
        {
            _searchCts?.Cancel();
            CloseAnimation.Begin();
            await Task.Delay(250); // Wait for animation (Open is 250ms, Close should be similar)
            this.Visibility = Visibility.Collapsed;
        }

        private void SearchScrim_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            Hide();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _debounceTimer.Stop();
            
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                _lastQuery = null; // [FIX] Reset last query to allow re-typing same term
                _resultsCollection.Clear();
                EmptyStatePanel.Visibility = Visibility.Collapsed;
                ShimmerPanel.Visibility = Visibility.Collapsed;
                SeeAllButton.Visibility = Visibility.Collapsed;
                return;
            }

            _debounceTimer.Start();
        }

        private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                SeeAllButton_Click(null, null);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                Hide();
            }
            else if (e.Key == Windows.System.VirtualKey.Down)
            {
                if (ResultsList.Items.Count > 0)
                {
                    ResultsList.Focus(FocusState.Programmatic);
                    ResultsList.SelectedIndex = 0;
                }
            }
        }

        private async void DebounceTimer_Tick(object sender, object e)
        {
            _debounceTimer.Stop();
            await RunSearchAsync();
        }

        private async Task RunSearchAsync(bool force = false)
        {
            string query = SearchBox.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;
            
            // Check if we need to re-run
            string currentSearchKey = $"{query}|{_searchType}|{_searchScope}";
            if (!force && currentSearchKey == _lastQuery) return;
            _lastQuery = currentSearchKey;

            // Update UI State -> Loading
            ResultsList.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            ShimmerPanel.Visibility = Visibility.Visible;
            
            SeeAllText.Text = $"Tüm sonuçları gör: '{query}'";
            SeeAllButton.Visibility = Visibility.Visible;

            _searchCts?.Cancel();
            _searchCts = new System.Threading.CancellationTokenSource();
            var token = _searchCts.Token;

            // Perform Unified Search (Service now handles IPTV + Type/Scope Filtering)
            try
            {
                await StremioService.Instance.SearchAsync(query, _searchType, _searchScope, (partialResults) =>
                {
                    if (token.IsCancellationRequested) return;

                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (token.IsCancellationRequested) return;

                        var topItems = partialResults.Take(8).ToList();
                        
                        // [NEW] Trigger Lazy IPTV Matching for the visible top 8 items
                        _ = StremioService.Instance.MatchVisibleIptvAsync(topItems, query);

                        // [SMOOTH SYNC] Avoid clearing the list to prevent catastrophic flicker.
                        // Replace existing matching spots natively retaining container refs, and trim tails.
                        for (int i = 0; i < topItems.Count; i++)
                        {
                            var target = topItems[i];
                            int existingIndex = -1;
                            for (int j = i; j < _resultsCollection.Count; j++)
                            {
                                if (_resultsCollection[j].Id == target.Id) { existingIndex = j; break; }
                            }

                            if (existingIndex == -1)
                            {
                                _resultsCollection.Insert(i, target);
                            }
                            else if (existingIndex != i)
                            {
                                _resultsCollection.Move(existingIndex, i);
                                // Only replace if it's a completely different object (which shouldn't happen, but safe)
                                if (!ReferenceEquals(_resultsCollection[i], target))
                                {
                                    _resultsCollection[i] = target;
                                }
                            }
                            else
                            {
                                // Already in the correct position.
                                if (!ReferenceEquals(_resultsCollection[i], target))
                                {
                                    _resultsCollection[i] = target;
                                }
                            }
                        }

                        // Remove leftover ghost items
                        for (int i = _resultsCollection.Count - 1; i >= topItems.Count; i--)
                        {
                            _resultsCollection.RemoveAt(i);
                        }

                        ResultsList.Visibility = _resultsCollection.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                        ShimmerPanel.Visibility = Visibility.Collapsed;
                        
                        if (ResultsList.Items.Count == 0 && !string.IsNullOrEmpty(query)) 
                            EmptyStatePanel.Visibility = Visibility.Visible;
                        else
                            EmptyStatePanel.Visibility = Visibility.Collapsed;
                    });
                }, token);
            }
            catch (OperationCanceledException) { /* Ignored */ }
        }

        private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is IMediaStream item)
            {
                ItemClicked?.Invoke(this, item);
                Hide();
            }
        }

        private void SeeAllButton_Click(object sender, RoutedEventArgs e)
        {
            string query = SearchBox.Text.Trim();
            if(!string.IsNullOrEmpty(query))
            {
                var args = new SearchArgs 
                { 
                    Query = query, 
                    Type = _searchType, 
                    PreferredSource = _searchScope == "iptv" ? "IPTV" : "Stremio" 
                };
                SeeAllClicked?.Invoke(this, args);
                Hide();
            }
        }

        private async void TypeFilterBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_searchType == "all") _searchType = "movie";
            else if (_searchType == "movie") _searchType = "series";
            else _searchType = "all";
            
            UpdateFilterUI();
            SearchBox.Focus(FocusState.Programmatic);
            await RunSearchAsync(true);
        }

        private async void ScopeFilterBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_searchScope == "all") _searchScope = "iptv";
            else if (_searchScope == "iptv") _searchScope = "library";
            else _searchScope = "all";
            UpdateFilterUI();
            SearchBox.Focus(FocusState.Programmatic);
            await RunSearchAsync(true);
        }

        private void UpdateFilterUI()
        {
            // Update Type Button
            switch (_searchType)
            {
                case "movie":
                    TypeIcon.Glyph = "\uE8B2"; // Movie icon
                    TypeText.Text = "FİLM";
                    break;
                case "series":
                    TypeIcon.Glyph = "\uE7C8"; // TV icon
                    TypeText.Text = "DİZİ";
                    break;
                default:
                    TypeIcon.Glyph = "\uE71D"; // All/Search icon
                    TypeText.Text = "HEPSİ";
                    break;
            }

            // Update Scope Button
            if (_searchScope == "iptv")
            {
                ScopeIcon.Glyph = "\uE753"; // World icon
                ScopeText.Text = "IPTV";
            }
            else if (_searchScope == "library")
            {
                ScopeIcon.Glyph = "\uE8B1"; // Library icon
                ScopeText.Text = "KÜTÜPHANE";
            }
            else
            {
                ScopeIcon.Glyph = "\uE753"; 
                ScopeText.Text = "HEPSİ";
            }
        }
    }
}
