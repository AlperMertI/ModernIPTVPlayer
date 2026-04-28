using System;
using WinRT;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer.Helpers
{
    public static class WinRTHelpers
    {
        /// <summary>
        /// Safely resolves an object to IMediaStream.
        /// If the object is a WinRT proxy (IWinRTObject), returns null immediately — 
        /// callers must use index-based recovery from the ItemsSource instead.
        /// </summary>
        public static IMediaStream? AsMediaStream(object? obj)
        {
            if (obj == null) return null;

            // 1. [PROXY ISOLATION] If this is a WinRT proxy, do NOT touch it.
            // Any interaction with a proxy's native state (NativeObject, FindObject, QI) 
            // can crash with NullReferenceException in ComWrappers.ManagedObjectWrapper.get_Holder()
            // during page transitions. Return null and let the caller recover via index.
            if (obj is IWinRTObject)
                return null;

            // 2. [MANAGED ONLY] Pure C# objects — safe to type-check.
            if (obj is Models.Stremio.StremioMediaStream s) return s;
            if (obj is Models.Iptv.LiveStream l) return l;
            if (obj is Models.Iptv.VodStream v) return v;
            if (obj is Models.Iptv.SeriesStream ss) return ss;
            if (obj is Models.WatchlistItem w) return w;

            // Context wrapper
            if (obj is Controls.UnifiedMediaItemContext context) return context.Data;

            // Interface fallback (safe for managed objects only)
            if (obj is IMediaStream managed) return managed;

            return null;
        }
    }
}
