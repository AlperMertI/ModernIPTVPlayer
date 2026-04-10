using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Media.Core;

namespace ModernIPTVPlayer.Helpers
{
    public static class CodecHelper
    {
        public static async Task<Dictionary<string, bool>> GetCodecSupportStatusAsync()
        {
            var results = new Dictionary<string, bool>();
            try
            {
                var query = new CodecQuery();
                
                // Optimized: Run all discovery tasks in parallel
                var mpeg2Task = query.FindAllAsync(CodecKind.Video, CodecCategory.Decoder, CodecSubtypes.VideoFormatMpeg2).AsTask();
                var hevcTask = query.FindAllAsync(CodecKind.Video, CodecCategory.Decoder, CodecSubtypes.VideoFormatHevc).AsTask();
                var h264Task = query.FindAllAsync(CodecKind.Video, CodecCategory.Decoder, CodecSubtypes.VideoFormatH264).AsTask();

                await Task.WhenAll(mpeg2Task, hevcTask, h264Task);

                results["MPEG-2"] = mpeg2Task.Result.Count > 0;
                results["HEVC"] = hevcTask.Result.Count > 0;
                results["H.264"] = h264Task.Result.Count > 0;

                foreach (var kvp in results)
                {
                    Debug.WriteLine($"[CodecInfo] {kvp.Key} Support: {kvp.Value}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CodecInfo] Error checking codecs: {ex.Message}");
            }

            return results;
        }
    }
}
