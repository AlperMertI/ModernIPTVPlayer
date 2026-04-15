using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using ModernIPTVPlayer.Models.Stremio;
using System.Text.Json;

namespace ModernIPTVPlayer.Services.Stremio
{
    public static class CatalogCacheManager
    {
        private const string CACHE_DIR = "StremioCatalogs";
        private const string MAGIC = "CTLC";
        private const int VERSION = 1;

        public static async Task SaveCatalogBinaryAsync(string url, string etag, List<StremioMediaStream> items)
        {
            try
            {
                string fileName = GetSafeFileName(url);
                var folder = await GetCacheFolderAsync();
                
                string tmpName = fileName + ".tmp";
                var file = await folder.CreateFileAsync(tmpName, CreationCollisionOption.ReplaceExisting);

                using (var stream = await file.OpenStreamForWriteAsync())
                using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
                using (var writer = new BinaryWriter(gzip, Encoding.UTF8))
                {
                    writer.Write(MAGIC);
                    writer.Write(VERSION);
                    writer.Write(DateTime.UtcNow.Ticks);
                    writer.Write(etag ?? "");
                    string json = JsonSerializer.Serialize(items);
                    writer.Write(json);
                }

                var finalFile = await folder.TryGetItemAsync(fileName);
                if (finalFile != null) await finalFile.DeleteAsync();
                await file.RenameAsync(fileName);

                // StremioService.Log($"[CatalogCache] SUCCESS: Saved {items.Count} items for {url}");
            }
            catch (Exception ex)
            {
                StremioService.Log($"[CatalogCache] ERROR: Save failed for {url}: {ex.Message}");
                AppLogger.Error($"[CatalogCache] Save failed for {url}", ex);
            }
        }

        public static async Task<(string ETag, List<StremioMediaStream> Items, DateTime Timestamp)> LoadCatalogBinaryAsync(string url)
        {
            try
            {
                string fileName = GetSafeFileName(url);
                var folder = await GetCacheFolderAsync();
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) 
                {
                     StremioService.Log($"[CatalogCache] MISS: No file for {url} (FileName: {fileName})");
                     return (null, null, DateTime.MinValue);
                }

                var fileInfo = await folder.GetFileAsync(fileName);
                var props = await fileInfo.GetBasicPropertiesAsync();
                
                // [FIX] Sanity Check: Files < 500 bytes are typically corrupt header-only files (e.g. the 74-byte ones)
                if (props.Size < 500)
                {
                    StremioService.Log($"[CatalogCache] REJECTED: File {fileName} is suspiciously small ({props.Size} bytes). Treating as MISS.");
                    return (null, null, DateTime.MinValue);
                }

                // StremioService.Log($"[CatalogCache] LOADING: {fileName} ({props.Size} bytes)");

                using var stream = await folder.OpenStreamForReadAsync(fileName);
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                using var reader = new BinaryReader(gzip, Encoding.UTF8);

                if (reader.ReadString() != MAGIC) return (null, null, DateTime.MinValue);
                int version = reader.ReadInt32();
                long ticks = reader.ReadInt64();
                string etag = reader.ReadString();
                string json = reader.ReadString();

                var items = JsonSerializer.Deserialize<List<StremioMediaStream>>(json);
                int count = items?.Count ?? 0;
                // StremioService.Log($"[CatalogCache] HIT: Loaded {count} items for {url} | ETag: {etag} | Age: {(DateTime.UtcNow - new DateTime(ticks)).TotalMinutes:F1} min");
                return (etag, items, new DateTime(ticks));
            }
            catch (Exception ex)
            {
                StremioService.Log($"[CatalogCache] LOAD ERROR for {url}: {ex.Message}");
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
                StremioService.Log("[CatalogCache] Cache cleared.");
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
                // Fallback for unpackaged execution
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModernIPTVPlayer", CACHE_DIR);
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                return await StorageFolder.GetFolderFromPathAsync(path);
            }
        }

        private static string GetSafeFileName(string url)
        {
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(url));
            return $"{Convert.ToHexString(hash).ToLowerInvariant()}.bin.gz";
        }
    }
}
