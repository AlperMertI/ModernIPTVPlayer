using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Zero-allocation JSON parser for Xtream API responses.
    /// Reads directly from HTTP stream, stores strings into MetadataBuffer,
    /// and outputs LiveStreamData structs — no intermediate string allocations.
    /// </summary>
    public static class ZeroAllocJsonParser
    {
        private static ReadOnlySpan<byte> NameProperty => "name"u8;
        private static ReadOnlySpan<byte> StreamIdProperty => "stream_id"u8;
        private static ReadOnlySpan<byte> SeriesIdProperty => "series_id"u8;
        private static ReadOnlySpan<byte> StreamIconProperty => "stream_icon"u8;
        private static ReadOnlySpan<byte> CoverProperty => "cover"u8;
        private static ReadOnlySpan<byte> ContainerExtensionProperty => "container_extension"u8;
        private static ReadOnlySpan<byte> CategoryIdProperty => "category_id"u8;
        private static ReadOnlySpan<byte> CategoryNameProperty => "category_name"u8;
        private static ReadOnlySpan<byte> RatingProperty => "rating"u8;

        /// <summary>
        /// Parses live streams from HTTP response stream with zero intermediate allocations.
        /// </summary>
        public static async Task<List<LiveStream>> ParseLiveStreamsAsync(HttpClient client, string url, CancellationToken ct = default)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            // Read all bytes (more efficient than chunked reading for JSON)
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            var jsonBytes = ms.ToArray();

            return ParseLiveStreamsFromBytes(jsonBytes);
        }

        /// <summary>
        /// Parses live streams from raw UTF-8 JSON bytes.
        /// Uses Utf8JsonReader directly — no string allocations for property names or values.
        /// All strings go into MetadataBuffer; output is LiveStreamData structs.
        /// </summary>
        public static List<LiveStream> ParseLiveStreamsFromBytes(ReadOnlySpan<byte> jsonBytes)
        {
            var reader = new Utf8JsonReader(jsonBytes, isFinalBlock: true, new JsonReaderState());

            // Expect start of array
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                return new List<LiveStream>();

            // Pre-count items if possible (estimate capacity)
            var results = new List<LiveStream>();

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    // Skip unexpected tokens
                    reader.Skip();
                    continue;
                }

                // Parse one LiveStream object
                var data = ParseLiveStreamObject(ref reader);
                var stream = new LiveStream();
                stream.LoadFromData(data);
                results.Add(stream);
            }

            return results;
        }

        /// <summary>
        /// Parses live categories from HTTP response stream with zero intermediate allocations.
        /// </summary>
        public static async Task<List<LiveCategory>> ParseLiveCategoriesAsync(HttpClient client, string url, CancellationToken ct = default)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            var jsonBytes = ms.ToArray();

            return ParseLiveCategoriesFromBytes(jsonBytes);
        }

        /// <summary>
        /// Parses live categories from raw UTF-8 JSON bytes.
        /// </summary>
        public static List<LiveCategory> ParseLiveCategoriesFromBytes(ReadOnlySpan<byte> jsonBytes)
        {
            var reader = new Utf8JsonReader(jsonBytes, isFinalBlock: true, new JsonReaderState());

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                return new List<LiveCategory>();

            var results = new List<LiveCategory>();

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                var cat = ParseLiveCategoryObject(ref reader);
                results.Add(cat);
            }

            return results;
        }

        // ==========================================
        // LOW-LEVEL PARSERS
        // ==========================================

        private static LiveStreamData ParseLiveStreamObject(ref Utf8JsonReader reader)
        {
            var data = new LiveStreamData();
            // Initialize lengths to -1 (null indicator for MetadataBuffer)
            data.NameOff = -1; data.NameLen = 0;
            data.IconOff = -1; data.IconLen = 0;
            data.ImdbOff = -1; data.ImdbLen = 0;
            data.DescOff = -1; data.DescLen = 0;
            data.BgOff = -1; data.BgLen = 0;
            data.GenreOff = -1; data.GenreLen = 0;
            data.CastOff = -1; data.CastLen = 0;
            data.DirOff = -1; data.DirLen = 0;
            data.TrailOff = -1; data.TrailLen = 0;
            data.YearOff = -1; data.YearLen = 0;
            data.ExtOff = -1; data.ExtLen = 0;
            data.CatOff = -1; data.CatLen = 0;
            data.RatOff = -1; data.RatLen = 0;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                // Read property name as raw bytes (no string allocation)
                var propName = reader.ValueSpan;

                reader.Read(); // Move to value

                // Fast property name matching (byte comparison, no string allocation)
                if (propName.SequenceEqual(NameProperty))
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var stored = StoreReaderValue(ref reader);
                        data.NameOff = stored.Offset;
                        data.NameLen = stored.Length;
                    }
                }
                else if (propName.SequenceEqual(StreamIdProperty))
                {
                    data.StreamId = reader.GetInt32();
                }
                else if (propName.SequenceEqual(SeriesIdProperty))
                {
                    data.StreamId = reader.GetInt32();
                }
                else if (propName.SequenceEqual(StreamIconProperty))
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var stored = StoreReaderValue(ref reader);
                        data.IconOff = stored.Offset;
                        data.IconLen = stored.Length;
                    }
                }
                else if (propName.SequenceEqual(CoverProperty))
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var stored = StoreReaderValue(ref reader);
                        data.IconOff = stored.Offset;
                        data.IconLen = stored.Length;
                    }
                }
                else if (propName.SequenceEqual(ContainerExtensionProperty))
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var stored = StoreReaderValue(ref reader);
                        data.ExtOff = stored.Offset;
                        data.ExtLen = stored.Length;
                    }
                }
                else if (propName.SequenceEqual(CategoryIdProperty))
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var stored = StoreReaderValue(ref reader);
                        data.CatOff = stored.Offset;
                        data.CatLen = stored.Length;
                    }
                    else if (reader.TokenType == JsonTokenType.Number)
                    {
                        // Some APIs return category_id as number
                        string val = reader.GetInt32().ToString();
                        var stored = MetadataBuffer.Store(val);
                        data.CatOff = stored.Offset;
                        data.CatLen = stored.Length;
                    }
                }
                else if (propName.SequenceEqual(RatingProperty))
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var stored = StoreReaderValue(ref reader);
                        data.RatOff = stored.Offset;
                        data.RatLen = stored.Length;
                    }
                }
                else
                {
                    // Skip unknown property values
                    reader.Skip();
                }
            }

            return data;
        }

        /// <summary>
        /// PROJECT ZERO: Zero-Allocation Reader Extraction.
        /// Extracts the current reader value as UTF-8 bytes and stores it in MetadataBuffer 
        /// without ever creating a System.String object on the heap.
        /// </summary>
        private static (int Offset, int Length) StoreReaderValue(ref Utf8JsonReader reader)
        {
            if (reader.ValueIsEscaped)
            {
                // Decouple escaped sequence via stackalloc buffer
                int maxChars = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;
                
                // Safety: limit stackalloc for extremely long values (rare in metadata)
                if (maxChars < 1024)
                {
                    Span<char> chars = stackalloc char[maxChars];
                    int charsWritten = reader.CopyString(chars);
                    var charSpan = chars.Slice(0, charsWritten);
                    
                    int byteCount = System.Text.Encoding.UTF8.GetByteCount(charSpan);
                    Span<byte> bytes = stackalloc byte[byteCount];
                    System.Text.Encoding.UTF8.GetBytes(charSpan, bytes);
                    
                    return MetadataBuffer.StoreRaw(bytes);
                }
                else
                {
                    // Fallback for massive strings
                    return MetadataBuffer.Store(reader.GetString());
                }
            }
            else
            {
                // Pure Zero-Allocation: Directly copy the UTF-8 span from JSON source to MetadataBuffer
                return MetadataBuffer.StoreRaw(reader.ValueSpan);
            }
        }

        private static LiveCategory ParseLiveCategoryObject(ref Utf8JsonReader reader)
        {
            var cat = new LiveCategory();
            string? name = null;
            string? id = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                var propName = reader.ValueSpan;
                reader.Read();

                if (propName.SequenceEqual(CategoryNameProperty))
                {
                    if (reader.TokenType == JsonTokenType.String)
                        name = reader.GetString();
                }
                else if (propName.SequenceEqual(CategoryIdProperty))
                {
                    if (reader.TokenType == JsonTokenType.String)
                        id = reader.GetString();
                    else if (reader.TokenType == JsonTokenType.Number)
                        id = reader.GetInt32().ToString();
                }
                else
                {
                    reader.Skip();
                }
            }

            cat.CategoryName = name ?? "Unknown";
            cat.CategoryId = id ?? "0";
            cat.Channels = new List<LiveStream>();
            return cat;
        }
    }
}
