using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopReplay.Core.Media;
using FFmpeg.AutoGen;

namespace DesktopReplay.Core.Library;

public sealed record AudioStreamInfo(int Index, string Title, int Channels, long Bitrate);

public sealed record MediaInfo(
    double Duration, int Width, int Height, double Fps, string VideoCodec, long VideoBitrate,
    double StartTime, IReadOnlyList<AudioStreamInfo> Audio);

public static unsafe class MediaProbe
{
    /// <summary>Reads container info. <paramref name="full"/> also scans the streams (codec, fps, bitrates).</summary>
    public static MediaInfo Probe(string path, bool full = true)
    {
        FF.Init();
        AVFormatContext* f = null;
        FF.Check(ffmpeg.avformat_open_input(&f, path, null, null), "open");
        try
        {
            if (full) FF.Check(ffmpeg.avformat_find_stream_info(f, null), "find_stream_info");
            double dur = f->duration > 0 ? f->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            double start = f->start_time != ffmpeg.AV_NOPTS_VALUE ? f->start_time / (double)ffmpeg.AV_TIME_BASE : 0;
            int w = 0, h = 0;
            double fps = 0;
            string codec = "";
            long vbr = 0;
            var audio = new List<AudioStreamInfo>();
            for (int i = 0; i < f->nb_streams; i++)
            {
                var st = f->streams[i];
                var p = st->codecpar;
                if (p->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && w == 0 && (st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
                {
                    w = p->width; h = p->height;
                    codec = ffmpeg.avcodec_get_name(p->codec_id);
                    vbr = p->bit_rate;
                    var r = st->avg_frame_rate.den != 0 && st->avg_frame_rate.num != 0 ? st->avg_frame_rate : st->r_frame_rate;
                    fps = r.den != 0 ? r.num / (double)r.den : 0;
                    if (dur <= 0 && st->duration > 0) dur = st->duration * ffmpeg.av_q2d(st->time_base);
                }
                else if (p->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    var t = ffmpeg.av_dict_get(st->metadata, "title", null, 0);
                    if (t == null) t = ffmpeg.av_dict_get(st->metadata, "handler_name", null, 0);
                    var title = t != null ? Marshal.PtrToStringUTF8((IntPtr)t->value) ?? "" : "";
                    if (title is "SoundHandler" or "Sound Media Handler") title = "";
                    audio.Add(new AudioStreamInfo(i, title, p->ch_layout.nb_channels, p->bit_rate));
                }
            }
            if (fps is <= 0 or > 1000) fps = 30;
            return new MediaInfo(dur, w, h, fps, codec, vbr, start, audio);
        }
        finally { ffmpeg.avformat_close_input(&f); }
    }
}

/// <summary>Decodes single keyframes as small BGRA bitmaps (thumbnails, timeline strip).</summary>
public sealed unsafe class FrameGrabber : IDisposable
{
    private AVFormatContext* _fmt;
    private AVCodecContext* _dec;
    private SwsContext* _sws;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private readonly int _vs;
    private readonly long _start;

    public double Duration { get; }

    public FrameGrabber(string path)
    {
        FF.Init();
        AVFormatContext* f = null;
        FF.Check(ffmpeg.avformat_open_input(&f, path, null, null), "open");
        _fmt = f;
        try
        {
            FF.Check(ffmpeg.avformat_find_stream_info(f, null), "find_stream_info");
            AVCodec* codec = null;
            _vs = FF.Check(ffmpeg.av_find_best_stream(f, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0), "no video stream");
            for (int i = 0; i < f->nb_streams; i++)
                if (i != _vs) f->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            _dec = ffmpeg.avcodec_alloc_context3(codec);
            FF.Check(ffmpeg.avcodec_parameters_to_context(_dec, f->streams[_vs]->codecpar), "params");
            _dec->thread_count = 0;
            _dec->skip_frame = AVDiscard.AVDISCARD_NONKEY; // keyframes only: fast and exactly what a seek lands on
            FF.Check(ffmpeg.avcodec_open2(_dec, codec, null), "open decoder");
            _frame = ffmpeg.av_frame_alloc();
            _pkt = ffmpeg.av_packet_alloc();
            _start = f->start_time != ffmpeg.AV_NOPTS_VALUE ? f->start_time : 0;
            Duration = f->duration > 0 ? f->duration / (double)ffmpeg.AV_TIME_BASE : 0;
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Nearest keyframe at or before <paramref name="seconds"/>, scaled to <paramref name="width"/>.</summary>
    public BitmapSource? Grab(double seconds, int width)
    {
        long ts = _start + (long)(Math.Max(0, seconds) * ffmpeg.AV_TIME_BASE);
        ffmpeg.av_seek_frame(_fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(_dec);
        bool eof = false;
        for (int guard = 0; guard < 4000; guard++)
        {
            if (!eof)
            {
                int r = ffmpeg.av_read_frame(_fmt, _pkt);
                if (r < 0) { eof = true; ffmpeg.avcodec_send_packet(_dec, null); }
                else
                {
                    if (_pkt->stream_index == _vs) ffmpeg.avcodec_send_packet(_dec, _pkt);
                    ffmpeg.av_packet_unref(_pkt);
                }
            }
            int ret = ffmpeg.avcodec_receive_frame(_dec, _frame);
            if (ret == 0)
            {
                var bmp = Convert(width);
                ffmpeg.av_frame_unref(_frame);
                return bmp;
            }
            if (ret == ffmpeg.AVERROR_EOF || (eof && ret == FF.EAGAIN)) return null;
        }
        return null;
    }

    private BitmapSource Convert(int width)
    {
        int sw = _frame->width, sh = _frame->height;
        double sar = _frame->sample_aspect_ratio.num > 0 && _frame->sample_aspect_ratio.den > 0 ? ffmpeg.av_q2d(_frame->sample_aspect_ratio) : 1;
        int w = Math.Max(2, Math.Min(width, sw)) & ~1;
        int h = Math.Max(2, (int)Math.Round(w * sh / (sw * sar))) & ~1;
        _sws = ffmpeg.sws_getCachedContext(_sws, sw, sh, (AVPixelFormat)_frame->format, w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
            ffmpeg.SWS_BILINEAR, null, null, null);
        int stride = w * 4;
        var buffer = new byte[stride * h];
        fixed (byte* p = buffer)
        {
            ffmpeg.sws_scale(_sws, _frame->data.ToArray(), _frame->linesize.ToArray(), 0, sh, new[] { p }, new[] { stride });
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, buffer, stride);
        bmp.Freeze();
        return bmp;
    }

    public void Dispose()
    {
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_frame != null) { var fr = _frame; ffmpeg.av_frame_free(&fr); _frame = null; }
        if (_pkt != null) { var pk = _pkt; ffmpeg.av_packet_free(&pk); _pkt = null; }
        if (_dec != null) { var d = _dec; ffmpeg.avcodec_free_context(&d); _dec = null; }
        if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
    }
}
