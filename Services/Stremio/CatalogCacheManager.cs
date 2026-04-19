using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using ModernIPTVPlayer.Models.Stremio;
using System.Text.Json;
using ModernIPTVPlayer.Services.Json;

namespace ModernIPTVPlayer.Services.Stremio
{
    public static class CatalogCacheManager
    {
        private const string CACHE_DIR = "StremioCatalogs";
        private const string MAGIC = "CTLC";
        private const int VERSION = 2;

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
                string fileName = GetSafeFileName(url);
                var folder = await GetCacheFolderAsync();
                
                string tmpName = fileName + ".tmp";
                var file = await folder.CreateFileAsync(tmpName, CreationCollisionOption.ReplaceExisting);

                using (var stream = await file.OpenStreamForWriteAsync())
                using (var compressor = new ZstandardStream(stream, CompressionLevel.Optimal))
                using (var writer = new BinaryWriter(compressor, Encoding.UTF8))
                {
                    writer.Write(MAGIC);
                    writer.Write(VERSION);
                    writer.Write(DateTime.UtcNow.Ticks);
                    writer.Write(etag ?? "");
                    var json = JsonSerializer.Serialize(items, AppJsonContext.Default.ListStremioMediaStream);
                    writer.Write(json);
                }

                var finalFile = await folder.TryGetItemAsync(fileName);
                if (finalFile != null) await finalFile.DeleteAsync();
                await file.RenameAsync(fileName);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[CatalogCache] Save failed for {url}", ex);
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

                var fileInfo = await folder.GetFileAsync(fileName);
                var props = await fileInfo.GetBasicPropertiesAsync();
                
                if (props.Size < 300) // Lowered slightly due to better Zstd compression
                {
                    try { await fileInfo.DeleteAsync(); } catch { }
                    return (null, null, DateTime.MinValue);
                }

                using var stream = await folder.OpenStreamForReadAsync(fileName);
                using var decompressor = new ZstandardStream(stream, CompressionMode.Decompress);
                using var reader = new BinaryReader(decompressor, Encoding.UTF8);

                if (reader.ReadString() != MAGIC) return (null, null, DateTime.MinValue);
                int version = reader.ReadInt32();
                if (version != VERSION) return (null, null, DateTime.MinValue);
                
                long ticks = reader.ReadInt64();
                string etag = reader.ReadString();
                string json = reader.ReadString();
 
                var items = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListStremioMediaStream);
                return (etag, items, new DateTime(ticks));
            }
            catch (Exception ex)
            {
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
