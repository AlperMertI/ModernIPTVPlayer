using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using ModernIPTVPlayer.Models.Stremio;
using MessagePack;
using ModernIPTVPlayer.Models.Common;
using System.Linq;

namespace ModernIPTVPlayer.Services.Stremio
{
    public static class CatalogCacheManager
    {
        private const string CACHE_DIR = "StremioCatalogs";
        private const string MAGIC = "CTLC";
        private const int VERSION = 3;
        
        // [NATIVE AOT] Use a static options object with a composite resolver.
        // This ensures compatibility with the AOT source generator and avoids runtime reflection.
        private static readonly MessagePackSerializerOptions AotOptions = MessagePackSerializerOptions.Standard
            .WithResolver(MessagePack.Resolvers.CompositeResolver.Create(
                MessagePack.Resolvers.StandardResolver.Instance
            ));

        public static string CanonicalizeCatalogUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url ?? "";
            string t = url.Trim();
            if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) return t;
            string path = uri.AbsolutePath.TrimEnd('/');
            var b = new UriBuilder(uri) { Path = path };
            return b.Uri.AbsoluteUri;
        }

        public static async Task SaveCatalogBinaryAsync(string url, string etag, List<StremioMediaStream> items)
        {
            url = CanonicalizeCatalogUrl(url);
            if (items == null || items.Count == 0) return;

            try
            {
                var dto = new CatalogCacheDTO
                {
                    ETag = etag ?? string.Empty,
                    Timestamp = DateTime.UtcNow.Ticks,
                    Items = items.Select(x => new MediaItemDTO
                    {
                        Id = x.IMDbId ?? x.Meta?.Id ?? string.Empty,
                        Title = x.Title ?? string.Empty,
                        Poster = x.PosterUrl ?? string.Empty,
                        Background = x.BackdropUrl ?? string.Empty,
                        Logo = x.LogoUrl ?? string.Empty,
                        Type = x.Type ?? "movie",
                        Year = x.Year ?? string.Empty,
                        Rating = double.TryParse(x.Rating, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0,
                        Overview = x.Overview ?? string.Empty,
                        Genres = x.Genres ?? string.Empty,
                        Trailer = x.TrailerUrl ?? string.Empty,
                        Resolution = x.Resolution ?? string.Empty,
                        Codec = x.Codec ?? string.Empty,
                        IsHdr = x.IsHdr,
                        Bitrate = x.Bitrate,
                        Fps = x.Fps ?? string.Empty,
                        SourceAddon = x.SourceAddon ?? string.Empty,
                        Progress = (int)x.ProgressValue,
                        IsAvailableOnIptv = x.IsAvailableOnIptv,
                        IsIptv = x.IsIptv,
                        SeriesName = x.SeriesName ?? string.Empty,
                        EpisodeSubtext = x.EpisodeSubtext ?? string.Empty
                    }).ToList()
                };

                string fileName = GetSafeFileName(url);
                var folder = await GetCacheFolderAsync();
                
                string tmpName = fileName + ".tmp";
                var file = await folder.CreateFileAsync(tmpName, CreationCollisionOption.ReplaceExisting);

                using (var stream = await file.OpenStreamForWriteAsync())
                using (var compressor = new ZstandardStream(stream, CompressionLevel.Optimal))
                {
                    // Write Magic & Version for fast header checks
                    using (var writer = new BinaryWriter(compressor, Encoding.UTF8, true))
                    {
                        writer.Write(MAGIC);
                        writer.Write(VERSION);
                    }
                    
                    // Serialize DTO directly to the compression stream using AOT-safe options
                    await MessagePackSerializer.SerializeAsync(compressor, dto, AotOptions);
                }

                var finalFile = await folder.TryGetItemAsync(fileName);
                if (finalFile != null) await finalFile.DeleteAsync();
                await file.RenameAsync(fileName);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[CatalogCache] MessagePack Save failed for {url}", ex);
            }
        }

        public static async Task<(string ETag, List<StremioMediaStream> Items, DateTime Timestamp)> LoadCatalogBinaryAsync(string url)
        {
            url = CanonicalizeCatalogUrl(url);
            try
            {
                string fileName = GetSafeFileName(url);
                var folder = await GetCacheFolderAsync();
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) return (null, null, DateTime.MinValue);

                using var stream = await folder.OpenStreamForReadAsync(fileName);
                using var decompressor = new ZstandardStream(stream, CompressionMode.Decompress);
                
                using (var reader = new BinaryReader(decompressor, Encoding.UTF8, true))
                {
                    if (reader.ReadString() != MAGIC) return (null, null, DateTime.MinValue);
                    int version = reader.ReadInt32();
                    if (version != VERSION) return (null, null, DateTime.MinValue);
                }

                // Deserialize directly from the compression stream using AOT-safe options
                var dto = await MessagePackSerializer.DeserializeAsync<CatalogCacheDTO>(decompressor, AotOptions);
                if (dto == null) return (null, null, DateTime.MinValue);

                var items = dto.Items.Select(x => new StremioMediaStream
                {
                    Meta = new StremioMeta
                    {
                        Id = x.Id,
                        Name = x.Title,
                        Poster = x.Poster,
                        Background = x.Background,
                        Logo = x.Logo,
                        Type = x.Type,
                        Year = x.Year,
                        Imdbrating = x.Rating,
                        Description = x.Overview,
                        Genres = x.Genres
                    },
                    SourceAddon = x.SourceAddon,
                    TrailerUrl = x.Trailer,
                    ProgressValue = x.Progress,
                    Resolution = x.Resolution,
                    Codec = x.Codec,
                    IsHdr = x.IsHdr,
                    Bitrate = x.Bitrate,
                    Fps = x.Fps,
                    IsAvailableOnIptv = x.IsAvailableOnIptv,
                    IsIptv = x.IsIptv,
                    SeriesName = x.SeriesName,
                    EpisodeSubtext = x.EpisodeSubtext
                }).ToList();

                return (dto.ETag, items, new DateTime(dto.Timestamp));
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[CatalogCache] MessagePack Load failed for {url}: {ex.Message}");
                return (null, null, DateTime.MinValue);
            }
        }

        public static async Task ClearCacheAsync()
        {
            try
            {
                var folder = await GetCacheFolderAsync();
                var files = await folder.GetFilesAsync();
                foreach (var file in files) await file.DeleteAsync();
            }
            catch { }
        }

        private static async Task<StorageFolder> GetCacheFolderAsync()
        {
            try 
            {
                var local = ApplicationData.Current.LocalCacheFolder;
                return await local.CreateFolderAsync(CACHE_DIR, CreationCollisionOption.OpenIfExists);
            }
            catch 
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModernIPTVPlayer", CACHE_DIR);
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                return await StorageFolder.GetFolderFromPathAsync(path);
            }
        }

        private static string GetSafeFileName(string url)
        {
            string norm = CanonicalizeCatalogUrl(url);
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(norm));
            return $"{Convert.ToHexString(hash).ToLowerInvariant()}.bin.zst";
        }
    }
}
