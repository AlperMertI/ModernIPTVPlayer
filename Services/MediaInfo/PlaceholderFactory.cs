using System;
using System.Collections.Generic;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Generates shimmer placeholder objects for any panel collection.
    /// Replaces the duplicated <c>CreatePlaceholders</c> and <c>CreateEpisodePlaceholders</c>
    /// methods that previously existed in both <c>MediaInfoPage.Sources.cs</c> and
    /// <c>MediaInfoPage.Episodes.cs</c>.
    /// </summary>
    internal static class PlaceholderFactory
    {
        /// <summary>
        /// Produces a subtle top-to-bottom opacity fade for shimmer placeholder rows.
        /// Earlier rows are fully opaque; later rows gradually fade to create a
        /// visual sense of depth while the real data loads.
        /// </summary>
        /// <param name="requestedCount">Desired number of placeholders.</param>
        /// <param name="minCount">Minimum count to always produce (default 6).</param>
        /// <returns>A sequence of opacity values, one per placeholder.</returns>
        public static IEnumerable<double> GenerateShimmerOpacitySequence(int requestedCount, int minCount = 6)
        {
            int count = Math.Max(minCount, requestedCount);
            for (int i = 0; i < count; i++)
            {
                double position = count <= 1 ? 0 : (double)i / (count - 1);
                yield return Math.Max(0.52, 1.0 - (position * 0.48));
            }
        }

        /// <summary>
        /// Calculates how many shimmer rows are needed to fill a container of a given height.
        /// </summary>
        /// <param name="containerHeight">Measured height of the panel or scroll viewer.</param>
        /// <param name="itemHeight">Expected height of a single placeholder row.</param>
        /// <param name="minCount">Minimum count to return when height is unavailable (default 8).</param>
        /// <returns>Number of placeholders needed to fill the viewport.</returns>
        public static int CalculateSkeletonCount(double containerHeight, double itemHeight, int minCount = 8)
        {
            if (containerHeight <= 0) return minCount;
            int count = (int)Math.Ceiling(containerHeight / Math.Max(1, itemHeight));
            return Math.Clamp(count, minCount, 25);
        }

        /// <summary>
        /// Creates a list of placeholder objects of type <typeparamref name="T"/>.
        /// Each placeholder is constructed by invoking <paramref name="factory"/> with
        /// a shimmer opacity value from <see cref="GenerateShimmerOpacitySequence"/>.
        /// </summary>
        /// <typeparam name="T">The placeholder type (e.g., <c>StremioStreamViewModel</c>, <c>EpisodeItem</c>).</typeparam>
        /// <param name="count">Number of placeholders to create.</param>
        /// <param name="factory">A function that takes an opacity value and returns a new placeholder instance.</param>
        /// <returns>A list of <paramref name="count"/> placeholder objects.</returns>
        public static List<T> CreatePlaceholders<T>(int count, Func<double, T> factory)
        {
            var list = new List<T>(count);
            foreach (var opacity in GenerateShimmerOpacitySequence(count))
            {
                list.Add(factory(opacity));
            }
            return list;
        }
    }
}
