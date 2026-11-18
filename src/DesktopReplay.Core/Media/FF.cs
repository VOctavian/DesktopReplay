using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace DesktopReplay.Core.Media;

/// <summary>FFmpeg bootstrap and small helpers.</summary>
public static unsafe class FF
{
    public const int EAGAIN = -11;
    public static readonly AVRational TimeBaseUs = new() { num = 1, den = 1_000_000 };

    private static bool _initialized;
    private static av_log_set_callback_callback? _logCallback; // keep delegate alive

    public static void Init()
    {
        if (_initialized) return;
        ffmpeg.RootPath = AppContext.BaseDirectory;
        Log.Info($"FFmpeg {ffmpeg.av_version_info()} loaded from {ffmpeg.RootPath}");
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
        _logCallback = OnLog;
        ffmpeg.av_log_set_callback(_logCallback);
        _initialized = true;
    }

    private static void OnLog(void* avcl, int level, string fmt, byte* vl)
    {
        if (level > ffmpeg.av_log_get_level()) return;
        const int size = 1024;
        var buffer = stackalloc byte[size];
        int printPrefix = 1;
        ffmpeg.av_log_format_line(avcl, level, fmt, vl, buffer, size, &printPrefix);
        var line = Marshal.PtrToStringUTF8((IntPtr)buffer)?.TrimEnd();
        if (!string.IsNullOrEmpty(line)) Log.Write(level <= ffmpeg.AV_LOG_ERROR ? "FFE" : "FFW", line);
    }

    public static string ErrorText(int err)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];
        ffmpeg.av_strerror(err, buffer, size);
        return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"error {err}";
    }

    public static int Check(int ret, string what)
    {
        if (ret < 0) throw new FFmpegException($"{what}: {ErrorText(ret)} ({ret})", ret);
        return ret;
    }

    public static bool HasEncoder(string name) => ffmpeg.avcodec_find_encoder_by_name(name) != null;
}

public sealed class FFmpegException(string message, int code) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Owned copy of AVCodecParameters describing one elementary stream.</summary>
public sealed unsafe class StreamDescriptor : IDisposable
{
    public int Index { get; }
    public bool IsVideo { get; }
    public string Title { get; }
    /// <summary>For video: monitor number; for audio: track number (1-based).</summary>
    public int Tag { get; }
    public AVCodecParameters* Parameters { get; private set; }

    public StreamDescriptor(int index, bool isVideo, string title, int tag, AVCodecContext* ctx)
    {
        Index = index;
        IsVideo = isVideo;
        Title = title;
        Tag = tag;
        Parameters = ffmpeg.avcodec_parameters_alloc();
        FF.Check(ffmpeg.avcodec_parameters_from_context(Parameters, ctx), "avcodec_parameters_from_context");
    }

    public void Dispose()
    {
        if (Parameters != null)
        {
            var p = Parameters;
            ffmpeg.avcodec_parameters_free(&p);
            Parameters = null;
        }
    }
}

/// <summary>An encoded packet; timestamps are in microseconds relative to the session start.</summary>
public sealed class EncodedPacket
{
    public int Stream;
    public long Pts;
    public long Dts;
    public long Duration;
    public bool Key;
    public byte[] Data = Array.Empty<byte>();
}
