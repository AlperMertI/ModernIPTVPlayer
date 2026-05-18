using System;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Metadata;
using MpvWinUI;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Orchestrates stream metadata resolution: IPTV metadata lookup, probe cache,
    /// and smart probing (reuse prebuffer player or probe fresh).
    /// Extracted from MediaInfoPage.UpdateTechnicalBadgesAsync.
    /// </summary>
    internal sealed class StreamMetadataService
    {
        #region Fields

        private readonly ProbeCacheService _probeCache;
        private readonly StreamProberService _proberService;

        #endregion

        #region Constructor

        public StreamMetadataService()
        {
            _probeCache = ProbeCacheService.Instance;
            _proberService = StreamProberService.Instance;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Resolves stream metadata with layered fallbacks:
        /// 1. IPTV episode/series metadata (no probing needed)
        /// 2. Unified metadata
        /// 3. Probe cache (by item ID)
        /// 4. Smart probe (reuse prebuffer player or probe fresh)
        /// </summary>
        public async Task<ProbeResult?> ResolveAsync(
            string url,
            int itemId,
            EpisodeItem? selectedEpisode,
            UnifiedMetadata? unifiedMetadata,
            MpvPlayer? existingPlayer,
            string? prebufferUrl,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(url)) return null;

            // 1. Check IPTV metadata first (skip probing)
            var iptvMetadata = GetIptvMetadata(selectedEpisode, unifiedMetadata);
            if (iptvMetadata != null)
            {
                return iptvMetadata;
            }

            // 2. Check probe cache
            await _probeCache.EnsureLoadedAsync();
            if (_probeCache.Get(itemId) is ProbeData cached)
            {
                return ToProbeResult(cached);
            }

            // 3. Smart probe
            return await ProbeAsync(url, itemId, existingPlayer, prebufferUrl, cancellationToken);
        }

        /// <summary>
        /// Caches probe result for future lookups.
        /// </summary>
        public async Task CacheAsync(int itemId, ProbeResult result)
        {
            await _probeCache.EnsureLoadedAsync();
            _probeCache.Update(itemId, result.ToCacheData());
        }

        /// <summary>
        /// Checks if metadata is available without probing (IPTV metadata or cache).
        /// </summary>
        public bool HasCachedMetadata(int itemId, EpisodeItem? selectedEpisode, UnifiedMetadata? unifiedMetadata)
        {
            return GetIptvMetadata(selectedEpisode, unifiedMetadata) != null ||
                   _probeCache.Get(itemId) != null;
        }

        #endregion

        #region Private Methods

        private static ProbeResult? GetIptvMetadata(EpisodeItem? selectedEpisode, UnifiedMetadata? unifiedMetadata)
        {
            string? metadataRes = null;
            string? metadataCodec = null;
            long metadataBitrate = 0;
            bool? metadataHdr = null;

            if (selectedEpisode != null)
            {
                metadataRes = selectedEpisode.Resolution;
                metadataCodec = selectedEpisode.VideoCodec;
                metadataBitrate = selectedEpisode.Bitrate;
                metadataHdr = selectedEpisode.IsHdr;
            }
            else if (unifiedMetadata != null)
            {
                metadataRes = unifiedMetadata.Resolution;
                metadataCodec = unifiedMetadata.VideoCodec;
                metadataBitrate = unifiedMetadata.Bitrate;
                metadataHdr = unifiedMetadata.IsHdr;
            }

            if (string.IsNullOrEmpty(metadataRes) && string.IsNullOrEmpty(metadataCodec))
                return null;

            return new ProbeResult
            {
                Resolution = metadataRes,
                Codec = metadataCodec,
                Bitrate = metadataBitrate,
                IsHdr = metadataHdr ?? false,
                Success = true
            };
        }

        private static ProbeResult ToProbeResult(ProbeData data)
        {
            return new ProbeResult
            {
                Resolution = data.Resolution,
                Codec = data.Codec,
                Bitrate = data.Bitrate,
                IsHdr = data.IsHdr,
                Fps = data.Fps,
                AspectRatio = data.AspectRatio,
                PixelFormat = data.PixelFormat,
                ColorSpace = data.ColorSpace,
                ColorRange = data.ColorRange,
                ChromaSubsampling = data.ChromaSubsampling,
                ScanType = data.ScanType,
                Encoder = data.Encoder,
                AudioCodec = data.AudioCodec,
                AudioChannels = data.AudioChannels,
                AudioSampleRate = data.AudioSampleRate,
                AudioLanguages = data.AudioLanguages,
                Container = data.Container,
                Protocol = data.Protocol,
                Server = data.Server,
                MimeType = data.MimeType,
                Latency = data.Latency,
                BufferSize = data.BufferSize,
                BufferDuration = data.BufferDuration,
                AvSync = data.AvSync,
                IsEncrypted = data.IsEncrypted,
                DrmType = data.DrmType,
                SubtitleTracks = data.SubtitleTracks,
                Success = true
            };
        }

        private async Task<ProbeResult?> ProbeAsync(
            string url,
            int itemId,
            MpvPlayer? existingPlayer,
            string? prebufferUrl,
            CancellationToken cancellationToken)
        {
            try
            {
                ProbeResult? result;

                if (existingPlayer != null && prebufferUrl == url)
                {
                    result = await StreamProberService.ExtractProbeDataAsync(existingPlayer, cancellationToken);
                }
                else
                {
                    result = await _proberService.ProbeAsync(itemId, url, progress: null, ct: cancellationToken);
                }

                return result?.Success == true ? result : null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}
