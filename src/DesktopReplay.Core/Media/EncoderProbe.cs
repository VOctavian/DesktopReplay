using System.Collections.Concurrent;
using DesktopReplay.Core.Settings;
using FFmpeg.AutoGen;

namespace DesktopReplay.Core.Media;

/// <summary>Checks whether an NVENC codec actually works on this GPU (av1_nvenc exists in FFmpeg but needs RTX 40+).</summary>
public static unsafe class EncoderProbe
{
    private static readonly ConcurrentDictionary<VideoCodec, bool> Cache = new();

    public static bool IsUsable(VideoCodec codec) => Cache.GetOrAdd(codec, Probe);

    private static bool Probe(VideoCodec codec)
    {
        try
        {
            FF.Init();
            var c = ffmpeg.avcodec_find_encoder_by_name(VideoPipeline.EncoderFor(codec));
            if (c == null) return false;
            var ctx = ffmpeg.avcodec_alloc_context3(c);
            try
            {
                ctx->width = 640;
                ctx->height = 360;
                ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
                ctx->time_base = new AVRational { num = 1, den = 30 };
                ctx->framerate = new AVRational { num = 30, den = 1 };
                int level = ffmpeg.av_log_get_level();
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
                int ret = ffmpeg.avcodec_open2(ctx, c, null);
                ffmpeg.av_log_set_level(level);
                Log.Info($"Encoder probe {codec}: {(ret >= 0 ? "OK" : FF.ErrorText(ret))}");
                return ret >= 0;
            }
            finally { ffmpeg.avcodec_free_context(&ctx); }
        }
        catch (Exception ex)
        {
            Log.Warn($"Encoder probe {codec} failed: {ex.Message}");
            return false;
        }
    }
}
