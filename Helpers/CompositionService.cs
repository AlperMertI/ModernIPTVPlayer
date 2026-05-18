using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Provides high-integrity access to the Composition layer, specifically designed for NativeAOT environments.
    /// Replaces the legacy SafeComposition with a contract-based access model that validates VTable integrity.
    /// </summary>
    public static class CompositionService
    {
        private const string TraceTag = "[COMP-SERVICE]";

        // Property Keys
        public const string OpacityProperty = "Opacity";
        public const string OffsetProperty = "Offset";
        public const string TranslationProperty = "Translation";
        public const string ScaleProperty = "Scale";
        public const string RotationProperty = "RotationAngle";

        /// <summary>
        /// Attempts to execute an action on a UIElement's backing visual with strict lifecycle validation.
        /// </summary>
        /// <param name="element">The target FrameworkElement.</param>
        /// <param name="action">The animation or property logic to execute.</param>
        /// <returns>True if the action was executed; false otherwise.</returns>
        public static bool Run(FrameworkElement? element, Action<Visual> action, [CallerMemberName] string caller = "")
        {
            if (element == null) return false;
            string name = element.Name ?? element.GetType().Name;

            if (!element.IsLoaded)
            {
                return false;
            }

            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                if (visual == null)
                {
                    return false;
                }

                action(visual);
                return true;
            }
            catch (InvalidCastException ice)
            {
                ModernIPTVPlayer.Services.AppLogger.Error($"{TraceTag} INTERFACE FAILURE (0x80004002?) in {caller} on {name}", ice);
                return false;
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error($"{TraceTag} ERROR in {caller} on {name}", ex);
                LogFailure(element, caller, ex);
                return false;
            }
        }

        /// <summary>
        /// Ensures the action is executed on the visual, waiting for the Loaded event if necessary.
        /// </summary>
        public static void RunOnceLoaded(FrameworkElement? element, Action<Visual> action, [CallerMemberName] string caller = "")
        {
            if (element == null) return;
            if (element.IsLoaded)
            {
                Run(element, action, caller);
            }
            else
            {
                // [NATIVE AOT] Use a local handler to allow unsubscription
                RoutedEventHandler? handler = null;
                handler = (s, e) =>
                {
                    element.Loaded -= handler;
                    Run(element, action, caller);
                };
                element.Loaded += handler;
            }
        }

        /// <summary>
        /// Safely enables the Translation property on an element, handling NativeAOT interface resolution.
        /// Waits for the element to be loaded if necessary.
        /// </summary>
        public static void EnableTranslation(FrameworkElement? element, [CallerMemberName] string caller = "")
        {
            RunOnceLoaded(element, visual =>
            {
                try
                {
                    ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{TraceTag} Translation Enable Failed in {caller}: {ex.Message}");
                }
            }, caller);
        }

        /// <summary>
        /// Safely stops all animations on a visual and resets common properties.
        /// </summary>
        public static void StopAll(Visual? visual, [CallerMemberName] string caller = "")
        {
            if (visual == null) return;
            try
            {
                visual.StopAnimation(OpacityProperty);
                visual.StopAnimation(OffsetProperty);
                visual.StopAnimation(ScaleProperty);
                visual.StopAnimation(RotationProperty);
            }
            catch { }
        }

        /// <summary>
        /// Resets the visual to its default state (Opacity=1, Scale=1, Translation=0).
        /// </summary>
        public static void StopAllAnimationsImmediately(FrameworkElement? element)
        {
            if (element == null) return;
            if (!element.IsLoaded) return;

            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                if (visual == null) return;

                visual.StopAnimation(OpacityProperty);
                visual.StopAnimation(OffsetProperty);
                visual.StopAnimation(ScaleProperty);
                visual.StopAnimation(RotationProperty);

                ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                visual.StopAnimation(TranslationProperty);

                visual.Opacity = 1.0f;
                visual.Scale = new System.Numerics.Vector3(1, 1, 1);
                visual.Properties.InsertVector3(TranslationProperty, System.Numerics.Vector3.Zero);
            }
            catch { }
        }

        public static void ResetVisual(FrameworkElement? element)
        {
            if (element == null) return;
            string name = element.Name ?? element.GetType().Name;
            
            Debug.WriteLine($"[COMP-SERVICE] ResetVisual on {name}. Before: Vis={element.Visibility}, Opacity={element.Opacity}");
            
            element.Opacity = 1.0;
            
            if (element.IsLoaded)
            {
                try
                {
                    var visual = ElementCompositionPreview.GetElementVisual(element);
                    if (visual != null)
                    {
                        visual.StopAnimation(OpacityProperty);
                        visual.StopAnimation(ScaleProperty);

                        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                        visual.StopAnimation(TranslationProperty);

                        visual.Opacity = 1.0f;
                        visual.Scale = new System.Numerics.Vector3(1, 1, 1);
                        visual.Properties.InsertVector3(TranslationProperty, System.Numerics.Vector3.Zero);
                        Debug.WriteLine($"[COMP-SERVICE] ResetVisual on {name}. After: visual.Opacity={visual.Opacity}");
                    }
                }
                catch { }
            }
            else
            {
                RoutedEventHandler handler = null;
                handler = (s, e) =>
                {
                    element.Loaded -= handler;
                    try
                    {
                        var visual = ElementCompositionPreview.GetElementVisual(element);
                        if (visual != null)
                        {
                            visual.StopAnimation(OpacityProperty);
                            visual.StopAnimation(ScaleProperty);

                            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                            visual.StopAnimation(TranslationProperty);

                            visual.Opacity = 1.0f;
                            visual.Scale = new System.Numerics.Vector3(1, 1, 1);
                            visual.Properties.InsertVector3(TranslationProperty, System.Numerics.Vector3.Zero);
                            Debug.WriteLine($"[COMP-SERVICE] ResetVisual on {name}. After: visual.Opacity={visual.Opacity}");
                        }
                    }
                    catch { }
                };
                element.Loaded += handler;
            }
        }

        /// <summary>
        /// Stops the Translation animation on a visual.
        /// Translation is not a native Visual property — it requires SetIsTranslationEnabled to be called first.
        /// Safe to call on any Visual; silently ignores if Translation is not enabled.
        /// </summary>

        /// <summary>
        /// Safely stops the translation animation on an element.
        /// Safe to call on any element; silently ignores if Translation is not enabled.
        /// </summary>
        public static void StopTranslationAnimation(FrameworkElement? element)
        {
            Run(element, visual =>
            {
                try 
                { 
                    var controller = visual.TryGetAnimationController(TranslationProperty);
                    if (controller != null)
                    {
                        visual.StopAnimation(TranslationProperty);
                    }
                } 
                catch { }
            });
        }

        /// <summary>
        /// Safely starts a translation animation on an element with an optional initial value.
        /// Automatically ensures translation is enabled.
        /// </summary>
        public static void StartTranslationAnimation(FrameworkElement? element, CompositionAnimation animation, Vector3? initialValue = null)
        {
            EnableTranslation(element);

            RunOnceLoaded(element, visual =>
            {
                try
                {
                    if (initialValue.HasValue)
                    {
                        visual.Properties.InsertVector3(TranslationProperty, initialValue.Value);
                    }
                    visual.StartAnimation(TranslationProperty, animation);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{TraceTag} StartTranslationAnimation Failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Safely sets the translation property on an element.
        /// </summary>
        public static void SetTranslation(FrameworkElement? element, Vector3 translation)
        {
            Run(element, visual =>
            {
                try { visual.Properties.InsertVector3(TranslationProperty, translation); } catch { }
            });
        }

        private static void LogFailure(FrameworkElement element, string caller, Exception ex)
        {
            string name = element.GetType().Name;
            Debug.WriteLine($"{TraceTag} Failure in {caller} for {name}: {ex.Message}");
        }
    }
}
