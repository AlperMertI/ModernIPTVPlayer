using System.Collections.Generic;
using Microsoft.UI.Xaml;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer.Controls
{
    /// <summary>
    /// A high-performance element factory for the Stremio Discovery page.
    /// Manages object pooling for different row types to ensure 120fps scrolling.
    /// </summary>
    public sealed class DiscoveryElementFactory : IElementFactory
    {
        public DataTemplate StandardCardTemplate { get; set; }
        public DataTemplate LandscapeCardTemplate { get; set; }

        private readonly Stack<CatalogRow> _standardPool = new();
        private readonly Stack<CatalogRow> _landscapePool = new();
        private readonly Stack<SpotlightInjectRow> _spotlightPool = new();

        /// <summary>
        /// Retrieves or creates a row element for the discovery list.
        /// </summary>
        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            if (args.Data is CatalogRowViewModel vm)
            {
                if (vm.RowStyle == "Spotlight")
                {
                    var row = _spotlightPool.Count > 0 ? _spotlightPool.Pop() : new SpotlightInjectRow();
                    row.DataContext = vm;
                    return row;
                }
                
                // Standard vs Landscape pooling reduces VisualState transition overhead
                if (vm.RowStyle == "Landscape")
                {
                    var row = _landscapePool.Count > 0 ? _landscapePool.Pop() : new CatalogRow { RowStyle = "Landscape", ItemTemplate = LandscapeCardTemplate };
                    row.DataContext = vm;
                    return row;
                }
                else
                {
                    var row = _standardPool.Count > 0 ? _standardPool.Pop() : new CatalogRow { RowStyle = "Standard", ItemTemplate = StandardCardTemplate };
                    row.DataContext = vm;
                    return row;
                }
            }

            return new CatalogRow();
        }

        /// <summary>
        /// Resets the element and returns it to the appropriate pool.
        /// </summary>
        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is SpotlightInjectRow spotlight)
            {
                spotlight.PrepareForRecycle();
                _spotlightPool.Push(spotlight);
            }
            else if (args.Element is CatalogRow catalog)
            {
                catalog.PrepareForRecycle();
                if (catalog.RowStyle == "Landscape")
                    _landscapePool.Push(catalog);
                else
                    _standardPool.Push(catalog);
            }
        }
    }
}
