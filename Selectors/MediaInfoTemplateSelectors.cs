using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ModernIPTVPlayer
{
    /// <summary>
    /// Chooses the source row template without changing the ItemsRepeater data source.
    /// Placeholder rows render shimmer; real rows render playable sources.
    /// </summary>
    public partial class StreamTemplateSelector : DataTemplateSelector
    {
        public DataTemplate RealTemplate { get; set; }
        public DataTemplate ShimmerTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item) => SelectTemplateInternal(item);
        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateInternal(item);

        private DataTemplate? SelectTemplateInternal(object? item)
        {
            if (item is StremioStreamViewModel vm)
            {
                return vm.IsPlaceholder ? ShimmerTemplate : RealTemplate;
            }
            return RealTemplate;
        }
    }

    /// <summary>
    /// Chooses the episode row template for shimmer placeholders versus real episode data.
    /// </summary>
    public partial class EpisodeTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? RealTemplate { get; set; }
        public DataTemplate? ShimmerTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object? item) => SelectTemplateInternal(item);
        protected override DataTemplate? SelectTemplateCore(object? item, DependencyObject? container) => SelectTemplateInternal(item);

        private DataTemplate? SelectTemplateInternal(object? item)
        {
            if (item is EpisodeItem vm)
            {
                return vm.IsPlaceholder ? ShimmerTemplate : RealTemplate;
            }
            return RealTemplate;
        }
    }
}
