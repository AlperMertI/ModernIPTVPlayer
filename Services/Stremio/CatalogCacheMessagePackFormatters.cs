using System.Collections.Generic;
using MessagePack;
using MessagePack.Formatters;
using ModernIPTVPlayer.Models.Common;

namespace ModernIPTVPlayer.Services.Stremio
{
    internal static class CatalogCacheMessagePackFormatters
    {
        internal static readonly IMessagePackFormatter[] Formatters =
        {
            CatalogCacheDTOFormatter.Instance,
            MediaItemDTOFormatter.Instance
        };

        internal sealed class CatalogCacheDTOFormatter : IMessagePackFormatter<CatalogCacheDTO>
        {
            internal static readonly CatalogCacheDTOFormatter Instance = new();

            public void Serialize(ref MessagePackWriter writer, CatalogCacheDTO value, MessagePackSerializerOptions options)
            {
                if (value is null)
                {
                    writer.WriteNil();
                    return;
                }

                writer.WriteArrayHeader(3);
                writer.Write(value.ETag);
                writer.Write(value.Timestamp);

                var items = value.Items ?? new List<MediaItemDTO>();
                writer.WriteArrayHeader(items.Count);
                foreach (var item in items)
                {
                    MediaItemDTOFormatter.Instance.Serialize(ref writer, item, options);
                }
            }

            public CatalogCacheDTO Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                if (reader.TryReadNil()) return new CatalogCacheDTO();

                int count = reader.ReadArrayHeader();
                var dto = new CatalogCacheDTO();
                if (count > 0) dto.ETag = reader.ReadString() ?? string.Empty;
                if (count > 1) dto.Timestamp = reader.ReadInt64();
                if (count > 2)
                {
                    int itemCount = reader.ReadArrayHeader();
                    var items = new List<MediaItemDTO>(itemCount);
                    for (int i = 0; i < itemCount; i++)
                    {
                        items.Add(MediaItemDTOFormatter.Instance.Deserialize(ref reader, options));
                    }
                    dto.Items = items;
                }

                for (int i = 3; i < count; i++)
                {
                    reader.Skip();
                }

                return dto;
            }
        }

        internal sealed class MediaItemDTOFormatter : IMessagePackFormatter<MediaItemDTO>
        {
            internal static readonly MediaItemDTOFormatter Instance = new();

            public void Serialize(ref MessagePackWriter writer, MediaItemDTO value, MessagePackSerializerOptions options)
            {
                if (value is null)
                {
                    writer.WriteNil();
                    return;
                }

                writer.WriteArrayHeader(22);
                writer.Write(value.Id);
                writer.Write(value.Title);
                writer.Write(value.Poster);
                writer.Write(value.Background);
                writer.Write(value.Logo);
                writer.Write(value.Type);
                writer.Write(value.Year);
                writer.Write(value.Rating);
                writer.Write(value.Overview);
                writer.Write(value.Genres);
                writer.Write(value.Trailer);
                writer.Write(value.Resolution);
                writer.Write(value.Codec);
                writer.Write(value.IsHdr);
                writer.Write(value.Bitrate);
                writer.Write(value.Fps);
                writer.Write(value.SourceAddon);
                writer.Write(value.Progress);
                writer.Write(value.IsAvailableOnIptv);
                writer.Write(value.IsIptv);
                writer.Write(value.SeriesName);
                writer.Write(value.EpisodeSubtext);
            }

            public MediaItemDTO Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                if (reader.TryReadNil()) return new MediaItemDTO();

                int count = reader.ReadArrayHeader();
                var item = new MediaItemDTO();
                if (count > 0) item.Id = reader.ReadString() ?? string.Empty;
                if (count > 1) item.Title = reader.ReadString() ?? string.Empty;
                if (count > 2) item.Poster = reader.ReadString() ?? string.Empty;
                if (count > 3) item.Background = reader.ReadString() ?? string.Empty;
                if (count > 4) item.Logo = reader.ReadString() ?? string.Empty;
                if (count > 5) item.Type = reader.ReadString() ?? string.Empty;
                if (count > 6) item.Year = reader.ReadString() ?? string.Empty;
                if (count > 7) item.Rating = reader.ReadDouble();
                if (count > 8) item.Overview = reader.ReadString() ?? string.Empty;
                if (count > 9) item.Genres = reader.ReadString() ?? string.Empty;
                if (count > 10) item.Trailer = reader.ReadString() ?? string.Empty;
                if (count > 11) item.Resolution = reader.ReadString() ?? string.Empty;
                if (count > 12) item.Codec = reader.ReadString() ?? string.Empty;
                if (count > 13) item.IsHdr = reader.ReadBoolean();
                if (count > 14) item.Bitrate = reader.ReadInt64();
                if (count > 15) item.Fps = reader.ReadString() ?? string.Empty;
                if (count > 16) item.SourceAddon = reader.ReadString() ?? string.Empty;
                if (count > 17) item.Progress = reader.ReadInt32();
                if (count > 18) item.IsAvailableOnIptv = reader.ReadBoolean();
                if (count > 19) item.IsIptv = reader.ReadBoolean();
                if (count > 20) item.SeriesName = reader.ReadString() ?? string.Empty;
                if (count > 21) item.EpisodeSubtext = reader.ReadString() ?? string.Empty;

                for (int i = 22; i < count; i++)
                {
                    reader.Skip();
                }

                return item;
            }
        }
    }
}
