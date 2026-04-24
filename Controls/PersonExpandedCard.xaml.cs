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
        private Action<IMediaStream>? _onNavigate;
        private bool _isEntranceAnimationActive = false;


        public PersonExpandedCard()
        {
            this.InitializeComponent();
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
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

            DispatcherQueue.TryEnqueue(() =>
            {
                MainScroll.ChangeView(0, 0, 1.0f, true);
                NameText.Text = name.ToUpper();
                KnownForText.Text = string.IsNullOrEmpty(character) ? "" : $"as {character}".ToUpper();
                KnownForText.Visibility = string.IsNullOrEmpty(character) ? Visibility.Collapsed : Visibility.Visible;
                
                BirthText.Visibility = Visibility.Collapsed;
                BioSection.Visibility = Visibility.Collapsed;
                SortControls.Visibility = Visibility.Collapsed;
                FilmographyListView.Visibility = Visibility.Collapsed;
                NoResultsText.Visibility = Visibility.Collapsed;
                
                ProfileShimmer.Visibility = Visibility.Visible;
                FilmographyShimmer.Visibility = Visibility.Visible;
                BioSkeleton.Visibility = Visibility.Collapsed;
                FilmographyCount.Text = "";
                CounterShimmer.Visibility = Visibility.Visible;
                
                if (!string.IsNullOrEmpty(profileUrl))
                    ProfileImageBrush.ImageSource = new BitmapImage(new Uri(profileUrl));
                else
                    ProfileImageBrush.ImageSource = null;
                
                AmbientBackdrop.Source = !string.IsNullOrEmpty(profileUrl) ? new BitmapImage(new Uri(profileUrl)) : null;
            });

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
                VisualStateManager.GoToState(this, "FullMetadata", true);
                ProfileImageBrush.ImageSource = null; // Reset for new person
                BioSkeleton.Visibility = Visibility.Visible; // Show bio loading
                
                if (person != null)
                {
                    if (!string.IsNullOrEmpty(person.ProfilePath))
                        ProfileImageBrush.ImageSource = new BitmapImage(new Uri($"https://image.tmdb.org/t/p/w185{person.ProfilePath}"));

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

                    // Hide bio skeleton once we have data (even if bio is empty)
                    BioSkeleton.Visibility = Visibility.Collapsed;
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

            bool isInitialLoad = FilmographyListView.ItemsSource == null;
            FilmographyListView.ItemsSource = items.OrderByDescending(f => f.ReleaseDate ?? DateTime.MinValue).ToList();
            FilmographyListView.Visibility = Visibility.Visible;
            FilmographyShimmer.Visibility = Visibility.Collapsed;
            
            // Only show sort controls if we have a reasonable amount of metadata
            SortControls.Visibility = Height > 450 ? Visibility.Visible : Visibility.Collapsed;
            
            if (isInitialLoad)
            {
                _isEntranceAnimationActive = true;
                AnimateFilmographyEntrance();
                _isEntranceAnimationActive = false;
            }
        }

        private void FilmographyListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            // Only force 0 opacity during the entrance animation phase.
            // This prevents the "flash" without breaking items scrolled in later.
            if (!args.InRecycleQueue)
            {
                var visual = ElementCompositionPreview.GetElementVisual(args.ItemContainer);
                visual.Opacity = _isEntranceAnimationActive && args.ItemIndex <= 13 ? 0f : 1f;
            }
        }

        private void StopShimmer()
        {
            ProfileShimmer.Visibility = Visibility.Collapsed;
            FilmographyShimmer.Visibility = Visibility.Collapsed;
            BioSkeleton.Visibility = Visibility.Collapsed;
        }

        private void AnimateFilmographyEntrance()
        {
            FilmographyListView.UpdateLayout();
            for (int i = 0; i < FilmographyListView.Items.Count; i++)
            {
                var container = FilmographyListView.ContainerFromIndex(i) as ListViewItem;
                if (container != null)
                {
                    var visual = ElementCompositionPreview.GetElementVisual(container);
                    // Ensure it starts at 0 for the animation
                    visual.Opacity = 0f;
                    visual.Offset = new Vector3(20, 0, 0);

                    var compositor = visual.Compositor;
                    var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
                    fadeAnim.InsertKeyFrame(1f, 1f);
                    fadeAnim.Duration = TimeSpan.FromMilliseconds(400);
                    fadeAnim.DelayTime = TimeSpan.FromMilliseconds(i * 35);

                    var slideAnim = compositor.CreateVector3KeyFrameAnimation();
                    slideAnim.InsertKeyFrame(1f, Vector3.Zero);
                    slideAnim.Duration = TimeSpan.FromMilliseconds(500);
                    slideAnim.DelayTime = TimeSpan.FromMilliseconds(i * 35);
                    slideAnim.Target = "Offset";

                    visual.StartAnimation("Opacity", fadeAnim);
                    visual.StartAnimation("Offset", slideAnim);
                }
                if (i > 12) break;
            }
        }

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
            FilmographyListView.ItemsSource = _allFilms.OrderByDescending(f => f.ReleaseDate).ToList();
            SortDateBtn.Foreground = (Brush)Resources["AccentBrush"];
            SortRatingBtn.Foreground = new SolidColorBrush(Color.FromArgb(100, 148, 163, 184)); // Indigo-Slate
            AnimateFilmographyEntrance();
        }

        private void SortRatingBtn_Click(object sender, RoutedEventArgs e)
        {
            FilmographyListView.ItemsSource = _allFilms.OrderByDescending(f => f.VoteAverage).ToList();
            SortRatingBtn.Foreground = (Brush)Resources["AccentBrush"];
            SortDateBtn.Foreground = new SolidColorBrush(Color.FromArgb(100, 148, 163, 184)); // Indigo-Slate
            AnimateFilmographyEntrance();
        }

        private void ProfileImageBrush_ImageOpened(object sender, RoutedEventArgs e)
        {
            ProfileShimmer.Visibility = Visibility.Collapsed;
        }

        private void Image_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (sender is Image img)
            {
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
    }

}
