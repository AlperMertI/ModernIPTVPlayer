using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Linq;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage : Page
    {
        #region Mouse Drag-to-Scroll Input Handlers

        private void OnMainPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var ptr = e.GetCurrentPoint(null); // Use window coords for smoothness
            
            if (ActualWidth >= LayoutAdaptiveThreshold)
            {
                var localPtr = e.GetCurrentPoint(RootGrid);
                if (localPtr.Position.X > (RootGrid.ActualWidth - 500)) 
                {
                    return; 
                }
            }
            
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && ptr.Properties.IsLeftButtonPressed)
            {
                _isMainDragging = true;
                _lastMainPointerPos = ptr.Position;
            }
        }

        private void OnMainPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isMainDragging)
            {
                // Conflict resolution: if cast dragging is active, abort main drag
                if (_isCastDragging)
                {
                    _isMainDragging = false;
                    return;
                }

                var ptr = e.GetCurrentPoint(null); // Use window coords for smoothness
                
                // Safety: check if left button is still pressed
                if (!ptr.Properties.IsLeftButtonPressed)
                {
                    _isMainDragging = false;
                    try { RootScrollViewer.ReleasePointerCapture(e.Pointer); } catch {}
                    return;
                }

                double deltaY = _lastMainPointerPos.Y - ptr.Position.Y;
                
                // Threshold-based capture: only handle if we've actually moved enough. 
                // This allows static clicks to fall through to child items.
                if (Math.Abs(deltaY) > 3.0) 
                {
                    if (RootScrollViewer.PointerCaptures == null || !RootScrollViewer.PointerCaptures.Any(c => c.PointerId == e.Pointer.PointerId))
                    {
                        RootScrollViewer.CapturePointer(e.Pointer);
                    }

                    RootScrollViewer.ChangeView(null, RootScrollViewer.VerticalOffset + deltaY, null, true);
                    _lastMainPointerPos = ptr.Position;
                    e.Handled = true;
                }
            }
        }

        private void OnMainPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isMainDragging)
            {
                _isMainDragging = false;
                RootScrollViewer.ReleasePointerCapture(e.Pointer);
            }
        }

        internal ScrollViewer GetScrollViewerInternal(DependencyObject element)
        {
            if (element is ScrollViewer sv) return sv;
            int childrenCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                var result = GetScrollViewerInternal(child);
                if (result != null) return result;
            }
            return null;
        }

        #endregion
    }
}
