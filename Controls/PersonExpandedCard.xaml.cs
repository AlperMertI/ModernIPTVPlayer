using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Models.Tmdb;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Json;

namespace ModernIPTVPlayer.Controls
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed partial class PersonExpandedCard : UserControl
    {
        private Compositor _compositor;
        private string? _imdbId;
        private string? _name;
        private CancellationTokenSource? _cts;
        private List<PersonFilmographyItem> _allFilms = new();
        private ObservableCollection<PersonFilmographyItem> _filmographyItems = new();
        private Action<IMediaStream>? _onNavigate;
        private bool _isEntranceAnimationActive = false;


        public PersonExpandedCard()
        {
            this.InitializeComponent();
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            FilmographyListView.ItemsSource = _filmographyItems;
            SetupImplicitAnimations();
            SetupBreathingAnimation();
        }


        private void SetupImplicitAnimations()
        {
            var visual = ElementCompositionPreview.GetElementVisual(RootGrid);
            var compositor = visual.Compositor;
            
            var implicitAnimations = compositor.CreateImplicitAnimationCollection();
            
            var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.Target = "Offset";
            offsetAnim.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
            offsetAnim.Duration = TimeSpan.FromMilliseconds(400);

            var sizeAnim = compositor.CreateVector2KeyFrameAnimation();
            sizeAnim.Target = "Size";
            sizeAnim.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
            sizeAnim.Duration = TimeSpan.FromMilliseconds(450);

            implicitAnimations["Offset"] = offsetAnim;
            implicitAnimations["Size"] = sizeAnim;
            
            visual.ImplicitAnimations = implicitAnimations;
        }

        private void SetupBreathingAnimation()
        {
            var visual = ElementCompositionPreview.GetElementVisual(ProfileGlow);
            var compositor = visual.Compositor;

            var pulseAnim = compositor.CreateScalarKeyFrameAnimation();
            pulseAnim.InsertKeyFrame(0.0f, 0.15f);
            pulseAnim.InsertKeyFrame(0.5f, 0.35f);
            pulseAnim.InsertKeyFrame(1.0f, 0.15f);
            pulseAnim.Duration = TimeSpan.FromSeconds(3);
            pulseAnim.IterationBehavior = AnimationIterationBehavior.Forever;

            visual.StartAnimation("Opacity", pulseAnim);
        }

        public async void LoadPersonAsync(string name, string character, string profileUrl, string parentImdbId, TmdbMovieResult tmdbInfo, Action<IMediaStream> onNavigate, Color? accentColor = null)
        {
            _name = name;
            _imdbId = parentImdbId;
            _onNavigate = onNavigate;
            Height = 430;
            RootGrid.Height = 430;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // Primary accent color usage removed per designer request. 
            // Theme now uses a stationary Obsidian Indigo for better adaptability.

            // [PERFORMANCE] Run initial setup immediately on UI thread to avoid "frame gap"
            MainScroll.ChangeView(0, 0, 1.0f, true);
            NameText.Text = name.ToUpper();
            KnownForText.Text = string.IsNullOrEmpty(character) ? "" : $"as {character}".ToUpper();
            KnownForText.Visibility = string.IsNullOrEmpty(character) ? Visibility.Collapsed : Visibility.Visible;
            
            BirthText.Visibility = Visibility.Collapsed;
            BioSection.Visibility = Visibility.Collapsed;
            SortControls.Visibility = Visibility.Collapsed;
            FilmographyListView.Visibility = Visibility.Collapsed;
            NoResultsText.Visibility = Visibility.Collapsed;
            
            // [IMMERSION FIX] Check cache before showing skeletons
            bool isCached = TryLoadFromCache(name, character);
            
            if (!isCached)
            {
                ProfileShimmer.Visibility = Visibility.Visible;
                FilmographyShimmer.Visibility = Visibility.Visible;
                BioSkeleton.Visibility = Visibility.Collapsed;
                FilmographyCount.Text = "";
                CounterShimmer.Visibility = Visibility.Visible;
                _filmographyItems.Clear();
                _allFilms.Clear();
            }

            // Reset Opacity for shimmers
            ElementCompositionPreview.GetElementVisual(FilmographyShimmer).Opacity = 1.0f;
            ElementCompositionPreview.GetElementVisual(ProfileShimmer).Opacity = 1.0f;
            ElementCompositionPreview.GetElementVisual(BioSkeleton).Opacity = 1.0f;

            if (!string.IsNullOrEmpty(profileUrl))
            {
                ProfileImageBrush.ImageSource = Helpers.SharedImageManager.GetOptimizedImage(profileUrl, targetWidth: 185, xamlRoot: this.XamlRoot);
                if (isCached) ProfileShimmer.Visibility = Visibility.Collapsed;
            }
            else
            {
                ProfileImageBrush.ImageSource = null;
                ProfileShimmer.Visibility = Visibility.Collapsed;
            }
            
            AmbientBackdrop.Source = !string.IsNullOrEmpty(profileUrl) ? Helpers.SharedImageManager.GetOptimizedImage(profileUrl, targetWidth: 800, xamlRoot: this.XamlRoot) : null;


            try
            {
                var searchResult = await TmdbHelper.SearchPersonAsync(name, token);
                if (searchResult != null)
                {
                    await LoadFromTmdbAsync(searchResult.Id, character, token);
                }
                else
                {
                    await LoadFromStremioSearchAsync(name, character, token);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PersonCard] Load Failed: {ex.Message}");
                DispatcherQueue.TryEnqueue(() => StopShimmer());
            }
        }

        private async Task LoadFromTmdbAsync(int tmdbId, string character, CancellationToken token)
        {
            Height = 560;
            RootGrid.Height = 560;

            var person = await TmdbHelper.GetPersonDetailsAsync(tmdbId, token);
            if (token.IsCancellationRequested) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                ProfileImageBrush.ImageSource = null; // Reset for new person
                BioSkeleton.Visibility = Visibility.Visible; // Show bio loading
                
                if (person != null)
                {
                    ApplyPersonDetails(person, character);
                }
                else
                {
                    StopShimmer();
                }
            });

            var filmography = await TmdbHelper.GetPersonFilmographyAsync(tmdbId, token);
            DispatcherQueue.TryEnqueue(() =>
            {
                _allFilms = filmography;
                FilmographyCount.Text = $"({_allFilms.Count})";
                CounterShimmer.Visibility = Visibility.Collapsed;
                
                if (_allFilms.Count > 0)
                {
                    UpdateFilmographyUI(_allFilms);
                }
                else
                {
                    NoResultsText.Visibility = Visibility.Visible;
                }
            });
        }

        private async Task LoadFromStremioSearchAsync(string name, string character, CancellationToken token)
        {
            Height = 430;
            RootGrid.Height = 430;

            var aioUrl = AppSettings.AioMetadataUrl;
            _allFilms.Clear();
            var results = await TmdbHelper.SearchPeopleViaStremioAsync(aioUrl, name, "all", token, (batch) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var newItems = batch.Select(m => new PersonFilmographyItem(m)).ToList();
                    foreach (var item in newItems)
                    {
                        if (!_allFilms.Any(f => f.Id == item.Id))
                            _allFilms.Add(item);
                    }
                    
                    _allFilms = _allFilms.OrderByDescending(f => f.ReleaseDate ?? DateTime.MinValue).ToList();
                    
                    FilmographyCount.Text = $"({_allFilms.Count})";
                    CounterShimmer.Visibility = Visibility.Collapsed;
                    UpdateFilmographyUI(_allFilms);
                });
            });

            DispatcherQueue.TryEnqueue(() =>
            {
                CounterShimmer.Visibility = Visibility.Collapsed;
                VisualStateManager.GoToState(this, "Minimalist", true);

                if (!string.IsNullOrEmpty(character))
                {
                    KnownForText.Text = $"AS {character}".ToUpper();
                    KnownForText.Visibility = Visibility.Visible;
                }
                else
                {
                    KnownForText.Visibility = Visibility.Collapsed;
                }

                if (_allFilms.Count == 0 && (results == null || results.Count == 0))
                {
                    NoResultsText.Visibility = Visibility.Visible;
                }
            });
        }

        private void UpdateFilmographyUI(List<PersonFilmographyItem> items)
        {
            if (items == null || items.Count == 0) return;

            bool isFirstRender = _filmographyItems.Count == 0;
            
            // Smart update: Only add items that aren't already in the collection
            // to avoid resetting the entire ListView and causing flicker.
            var sortedItems = items.OrderByDescending(f => f.ReleaseDate ?? DateTime.MinValue).ToList();
            
            foreach (var item in sortedItems)
            {
                if (!_filmographyItems.Any(f => f.Id == item.Id))
                {
                    // Find correct insertion index to maintain sort order without a full reset
                    int insertIndex = 0;
                    while (insertIndex < _filmographyItems.Count && 
                           (_filmographyItems[insertIndex].ReleaseDate ?? DateTime.MinValue) > (item.ReleaseDate ?? DateTime.MinValue))
                    {
                        insertIndex++;
                    }
                    _filmographyItems.Insert(insertIndex, item);
                }
            }

            if (isFirstRender && _filmographyItems.Count > 0)
            {
                _isEntranceAnimationActive = true;
                FilmographyListView.Visibility = Visibility.Visible;
                
                // Hide main shimmer immediately if we have items to show, 
                // individual items have their own skeletons now.
                FilmographyShimmer.Visibility = Visibility.Collapsed;
                
                _isEntranceAnimationActive = false; 
            }
            else
            {
                FilmographyListView.Visibility = Visibility.Visible;
                FilmographyShimmer.Visibility = Visibility.Collapsed;
            }
            
            // Only show sort controls if we have a reasonable amount of metadata
            SortControls.Visibility = Height > 450 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FilmographyListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            var container = args.ItemContainer as ListViewItem;
            if (container == null) return;

            // [RECYCLING FIX] Always reset visual state to avoid seeing old data/images
            var posterImage = FindDescendantByName<Image>(container, "FilmPoster");
            var itemShimmer = FindDescendantByName<UserControl>(container, "ItemShimmer");
            if (posterImage != null) posterImage.Opacity = 0;
            if (itemShimmer != null) itemShimmer.Visibility = Visibility.Visible;

            if (!args.InRecycleQueue && _isEntranceAnimationActive && args.ItemIndex < 12)
            {
                var visual = ElementCompositionPreview.GetElementVisual(container);
                ElementCompositionPreview.SetIsTranslationEnabled(container, true);
                var compositor = visual.Compositor;

                // Prepare state: Use Translation instead of Offset to avoid layout conflicts
                visual.Opacity = 0f;
                visual.Properties.InsertVector3("Translation", new Vector3(20, 0, 0));

                // Create animations
                var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
                fadeAnim.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 1f), new Vector2(0.67f, 1f)));
                fadeAnim.Duration = TimeSpan.FromMilliseconds(500);
                fadeAnim.DelayTime = TimeSpan.FromMilliseconds(args.ItemIndex * 40);

                var slideAnim = compositor.CreateVector3KeyFrameAnimation();
                slideAnim.InsertKeyFrame(1f, Vector3.Zero, compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 1f), new Vector2(0.67f, 1f)));
                slideAnim.Duration = TimeSpan.FromMilliseconds(600);
                slideAnim.DelayTime = TimeSpan.FromMilliseconds(args.ItemIndex * 40);
                slideAnim.Target = "Translation";

                visual.StartAnimation("Opacity", fadeAnim);
                visual.StartAnimation("Translation", slideAnim);
            }
            else if (!args.InRecycleQueue)
            {
                var visual = ElementCompositionPreview.GetElementVisual(args.ItemContainer);
                ElementCompositionPreview.SetIsTranslationEnabled(args.ItemContainer, true);
                visual.Opacity = 1f;
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
            }
        }

        private void StopShimmer()
        {
            ProfileShimmer.Visibility = Visibility.Collapsed;
            FilmographyShimmer.Visibility = Visibility.Collapsed;
            BioSkeleton.Visibility = Visibility.Collapsed;
        }

        // Logic moved to FilmographyListView_ContainerContentChanging for better performance and flicker prevention
        private void AnimateFilmographyEntrance() { }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Visibility = Visibility.Collapsed;
        }

        private void FilmItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                if (FindDescendantByName<Border>(element, "PosterFrame") is not FrameworkElement posterFrame)
                {
                    return;
                }

                Canvas.SetZIndex(element, 1000);
                var visual = ElementCompositionPreview.GetElementVisual(posterFrame);
                var compositor = visual.Compositor;

                if (posterFrame.ActualWidth > 0 && posterFrame.ActualHeight > 0)
                {
                    visual.CenterPoint = new Vector3(
                        (float)posterFrame.ActualWidth / 2f,
                        (float)posterFrame.ActualHeight / 2f,
                        0f);
                }

                var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(1f, new Vector3(1.06f, 1.06f, 1.0f));
                scaleAnim.Duration = TimeSpan.FromMilliseconds(230);
                visual.StartAnimation("Scale", scaleAnim);
            }
        }

        private void FilmItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                if (FindDescendantByName<Border>(element, "PosterFrame") is not FrameworkElement posterFrame)
                {
                    return;
                }

                Canvas.SetZIndex(element, 0);
                var visual = ElementCompositionPreview.GetElementVisual(posterFrame);
                var compositor = visual.Compositor;

                var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(1f, Vector3.One);
                scaleAnim.Duration = TimeSpan.FromMilliseconds(200);
                visual.StartAnimation("Scale", scaleAnim);
            }
        }

        private static T? FindDescendantByName<T>(DependencyObject parent, string name) where T : DependencyObject
        {
            if (parent == null) return null;

            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T typed && child is FrameworkElement fe && fe.Name == name)
                {
                    return typed;
                }

                var found = FindDescendantByName<T>(child, name);
                if (found != null) return found;
            }

            return null;
        }

        private async void FilmographyItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is PersonFilmographyItem film)
            {
                var stream = await TmdbHelper.ResolveFilmographyToStreamAsync(film, _imdbId);
                if (stream != null) _onNavigate?.Invoke(stream);
            }
        }

        private void BioExpandBtn_Click(object sender, RoutedEventArgs e)
        {
            BioText.MaxLines = BioText.MaxLines == 3 ? 0 : 3;
            BioExpandBtn.Content = BioText.MaxLines == 3 ? "Read more ▾" : "Show less ▴";
        }

        private void SortDateBtn_Click(object sender, RoutedEventArgs e)
        {
            var sorted = _allFilms.OrderByDescending(f => f.ReleaseDate ?? DateTime.MinValue).ToList();
            SyncFilmographyItems(sorted);
            SortDateBtn.Foreground = (Brush)Resources["AccentBrush"];
            SortRatingBtn.Foreground = new SolidColorBrush(Color.FromArgb(100, 148, 163, 184)); // Indigo-Slate
        }

        private void SortRatingBtn_Click(object sender, RoutedEventArgs e)
        {
            var sorted = _allFilms.OrderByDescending(f => f.VoteAverage).ToList();
            SyncFilmographyItems(sorted);
            SortRatingBtn.Foreground = (Brush)Resources["AccentBrush"];
            SortDateBtn.Foreground = new SolidColorBrush(Color.FromArgb(100, 148, 163, 184)); // Indigo-Slate
        }

        private void SyncFilmographyItems(List<PersonFilmographyItem> sorted)
        {
            _filmographyItems.Clear();
            _isEntranceAnimationActive = true;
            foreach (var item in sorted) _filmographyItems.Add(item);
            // Entrance animation will be triggered by ContainerContentChanging for all new items
            _isEntranceAnimationActive = false;
        }

        private void ProfileImageBrush_ImageOpened(object sender, RoutedEventArgs e)
        {
            ProfileShimmer.Visibility = Visibility.Collapsed;
        }

        private void Image_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (sender is Image img)
            {
                // [SKELETON REVEAL] Hide the local shimmer once the image is ready
                if (VisualTreeHelper.GetParent(img) is Grid parent)
                {
                    var shimmer = FindDescendantByName<UserControl>(parent, "ItemShimmer");
                    if (shimmer != null) shimmer.Visibility = Visibility.Collapsed;
                }

                var anim = new DoubleAnimation
                {
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(350)),
                    EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
                };
                
                Storyboard storyboard = new Storyboard();
                storyboard.Children.Add(anim);
                Storyboard.SetTarget(anim, img);
                Storyboard.SetTargetProperty(anim, "Opacity");
                storyboard.Begin();
            }
        }

        private bool TryLoadFromCache(string name, string character)
        {
            try
            {
                var lang = AppSettings.TmdbLanguage;
                var searchKey = $"search_person_{name}_{lang}";
                var cachedSearch = TmdbCacheService.Instance.Get(searchKey, AppJsonContext.Default.TmdbPersonSearchResponse);
                
                if (cachedSearch?.Results?.Count > 0)
                {
                    var personId = cachedSearch.Results[0].Id;
                    var detailsKey = $"person_details_{personId}_{lang}";
                    var creditsKey = $"person_credits_{personId}_{lang}";
                    
                    var details = TmdbCacheService.Instance.Get(detailsKey, AppJsonContext.Default.TmdbPersonDetails);
                    var credits = TmdbCacheService.Instance.Get(creditsKey, AppJsonContext.Default.TmdbPersonCreditsResponse);
                    
                    if (details != null && credits != null)
                    {
                        ApplyPersonDetails(details, character);
                        var filmography = credits.Cast.Select(c => new PersonFilmographyItem(c)).ToList();
                        _allFilms = filmography;
                        UpdateFilmographyUI(filmography);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void ApplyPersonDetails(TmdbPersonDetails person, string character)
        {
            VisualStateManager.GoToState(this, "FullMetadata", true);
            BioSkeleton.Visibility = Visibility.Collapsed;
            
            if (!string.IsNullOrEmpty(person.ProfilePath))
                ProfileImageBrush.ImageSource = Helpers.SharedImageManager.GetOptimizedImage($"https://image.tmdb.org/t/p/w185{person.ProfilePath}", targetWidth: 185, xamlRoot: this.XamlRoot);

            if (!string.IsNullOrEmpty(person.Biography))
            {
                BioText.Text = person.Biography;
                BioSection.Visibility = Visibility.Visible;
                BioSkeleton.Visibility = Visibility.Collapsed;
                BioExpandBtn.Visibility = person.Biography.Length > 200 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (person.Birthday.HasValue)
            {
                BirthText.Text = $"BORN {person.Birthday.Value:MMMM d, yyyy}" + (!string.IsNullOrEmpty(person.PlaceOfBirth) ? $" in {person.PlaceOfBirth}" : "");
                BirthText.Visibility = Visibility.Visible;
            }

            if (!string.IsNullOrEmpty(person.KnownForDepartment))
            {
                KnownForText.Text = (string.IsNullOrEmpty(character) ? person.KnownForDepartment : $"as {character} | {person.KnownForDepartment}").ToUpper();
                KnownForText.Visibility = Visibility.Visible;
            }
        }
    }

}
