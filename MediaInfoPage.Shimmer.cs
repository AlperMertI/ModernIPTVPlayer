using System;
using System.Collections.Generic;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage
    {
        /// <summary>
        /// Calculates enough shimmer rows to fill a panel while keeping a sensible lower and upper bound.
        /// Shared by source and episode placeholder generation so both panels feel consistent.
        /// </summary>
        protected int CalculateSkeletonCount(double containerHeight, double itemHeight, int minCount = 6)
        {
            double height = containerHeight > 0 ? containerHeight : (ActualHeight > 0 ? ActualHeight * 0.72 : 720);
            int count = (int)Math.Ceiling(height / Math.Max(1, itemHeight));
            return Math.Clamp(count, minCount, 20);
        }

        /// <summary>
        /// Produces a subtle top-to-bottom opacity fade for shimmer placeholder rows.
        /// </summary>
        protected IEnumerable<double> GenerateShimmerOpacitySequence(int requestedCount, int minCount = 6)
        {
            int count = Math.Max(minCount, requestedCount);
            for (int i = 0; i < count; i++)
            {
                double position = count <= 1 ? 0 : (double)i / (count - 1);
                yield return Math.Max(0.52, 1.0 - (position * 0.48));
            }
        }
    }
}
