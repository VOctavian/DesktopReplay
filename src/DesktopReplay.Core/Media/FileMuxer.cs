using FFmpeg.AutoGen;

namespace DesktopReplay.Core.Media;

/// <summary>
/// Writes already-encoded packets (µs timestamps) into an MP4/MKV container.
/// One output file holds one video stream plus all audio tracks.
/// </summary>
public sealed unsafe class FileMuxer : IDisposable
{
    private AVFormatContext* _ctx;
    private readonly Dictionary<int, int> _map = new(); // store stream index → output stream index
    private long _originUs;
    private bool _headerWritten;
    private bool _gotKeyframe;
    private readonly int _videoStream;
    private AVPacket* _pkt;
    public string Path { get; }
    public long LastPtsUs { get; private set; }

    /// <param name="format">"mp4" or "matroska"</param>
    /// <param name="originUs">Timestamp that becomes 0 in the file; -1 = the first video keyframe.</param>
    public FileMuxer(string path, string format, StreamDescriptor video, IReadOnlyList<StreamDescriptor> audio, long originUs, bool fastStart)
    {
        Path = path;
        _originUs = originUs;
        _videoStream = video.Index;
        AVFormatContext* ctx = null;
        FF.Check(ffmpeg.avformat_alloc_output_context2(&ctx, null, format, path), "avformat_alloc_output_context2");
        _ctx = ctx;

        AddStream(video, true);
        bool first = true;
        foreach (var a in audio)
        {
            AddStream(a, first);
            first = false;
        }

        ffmpeg.av_dict_set(&_ctx->metadata, "encoder", "DesktopReplay", 0);
        FF.Check(ffmpeg.avio_open(&_ctx->pb, path, ffmpeg.AVIO_FLAG_WRITE), $"avio_open {path}");

        AVDictionary* opts = null;
        if (fastStart && format == "mp4") ffmpeg.av_dict_set(&opts, "movflags", "+faststart", 0);
        int ret = ffmpeg.avformat_write_header(_ctx, &opts);
        ffmpeg.av_dict_free(&opts);
        FF.Check(ret, "avformat_write_header");
        _headerWritten = true;
        _pkt = ffmpeg.av_packet_alloc();
    }

    private void AddStream(StreamDescriptor d, bool isDefault)
    {
        var st = ffmpeg.avformat_new_stream(_ctx, null);
        FF.Check(ffmpeg.avcodec_parameters_copy(st->codecpar, d.Parameters), "avcodec_parameters_copy");
        st->codecpar->codec_tag = 0;
        st->time_base = d.IsVideo ? FF.TimeBaseUs : new AVRational { num = 1, den = d.Parameters->sample_rate };
        st->disposition = isDefault ? ffmpeg.AV_DISPOSITION_DEFAULT : 0;
        ffmpeg.av_dict_set(&st->metadata, "title", d.Title, 0);
        ffmpeg.av_dict_set(&st->metadata, "handler_name", d.Title, 0);
        _map[d.Index] = st->index;
    }

    /// <summary>Writes a packet. Packets before the first video keyframe are dropped.</summary>
    public void Write(EncodedPacket p)
    {
        if (_ctx == null || !_map.TryGetValue(p.Stream, out int outIndex)) return;
        if (!_gotKeyframe)
        {
            if (p.Stream != _videoStream || !p.Key) return;
            _gotKeyframe = true;
            if (_originUs < 0) _originUs = p.Pts;
        }
        long pts = p.Pts - _originUs;
        long dts = p.Dts - _originUs;
        if (pts < 0 || dts < 0) return;

        fixed (byte* data = p.Data)
        {
            FF.Check(ffmpeg.av_new_packet(_pkt, p.Data.Length), "av_new_packet");
            Buffer.MemoryCopy(data, _pkt->data, p.Data.Length, p.Data.Length);
        }
        _pkt->pts = pts;
        _pkt->dts = dts;
        _pkt->duration = p.Duration;
        _pkt->flags = p.Key ? ffmpeg.AV_PKT_FLAG_KEY : 0;
        _pkt->stream_index = outIndex;
        ffmpeg.av_packet_rescale_ts(_pkt, FF.TimeBaseUs, _ctx->streams[outIndex]->time_base);
        int ret = ffmpeg.av_interleaved_write_frame(_ctx, _pkt);
        if (ret < 0) Log.Warn($"Write packet failed ({System.IO.Path.GetFileName(Path)}): {FF.ErrorText(ret)}");
        if (p.Pts > LastPtsUs) LastPtsUs = p.Pts;
    }

    public bool HasData => _gotKeyframe;

    public void Dispose()
    {
        if (_ctx == null) return;
        if (_headerWritten)
        {
            int ret = ffmpeg.av_write_trailer(_ctx);
            if (ret < 0) Log.Warn($"av_write_trailer: {FF.ErrorText(ret)}");
        }
        if (_ctx->pb != null) ffmpeg.avio_closep(&_ctx->pb);
        ffmpeg.avformat_free_context(_ctx);
        _ctx = null;
        if (_pkt != null)
        {
            var pkt = _pkt;
            ffmpeg.av_packet_free(&pkt);
            _pkt = null;
        }
    }

    /// <summary>Copies all streams of <paramref name="src"/> into <paramref name="dst"/> without re-encoding.</summary>
    public static void Remux(string src, string dst)
    {
        AVFormatContext* inCtx = null;
        AVFormatContext* outCtx = null;
        var pkt = ffmpeg.av_packet_alloc();
        try
        {
            FF.Check(ffmpeg.avformat_open_input(&inCtx, src, null, null), $"open {src}");
            FF.Check(ffmpeg.avformat_find_stream_info(inCtx, null), "find_stream_info");
            FF.Check(ffmpeg.avformat_alloc_output_context2(&outCtx, null, "mp4", dst), "alloc mp4");
            for (int i = 0; i < inCtx->nb_streams; i++)
            {
                var ist = inCtx->streams[i];
                var ost = ffmpeg.avformat_new_stream(outCtx, null);
                FF.Check(ffmpeg.avcodec_parameters_copy(ost->codecpar, ist->codecpar), "param copy");
                ost->codecpar->codec_tag = 0;
                ost->time_base = ist->time_base;
                ost->disposition = ist->disposition;
                ffmpeg.av_dict_copy(&ost->metadata, ist->metadata, 0);
                var title = ffmpeg.av_dict_get(ist->metadata, "title", null, 0);
                if (title != null) ffmpeg.av_dict_set(&ost->metadata, "handler_name", System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)title->value), 0);
            }
            ffmpeg.av_dict_copy(&outCtx->metadata, inCtx->metadata, 0);
            FF.Check(ffmpeg.avio_open(&outCtx->pb, dst, ffmpeg.AVIO_FLAG_WRITE), $"open {dst}");
            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "movflags", "+faststart", 0);
            int hr = ffmpeg.avformat_write_header(outCtx, &opts);
            ffmpeg.av_dict_free(&opts);
            FF.Check(hr, "write_header");

            while (ffmpeg.av_read_frame(inCtx, pkt) >= 0)
            {
                var ist = inCtx->streams[pkt->stream_index];
                var ost = outCtx->streams[pkt->stream_index];
                ffmpeg.av_packet_rescale_ts(pkt, ist->time_base, ost->time_base);
                pkt->pos = -1;
                int ret = ffmpeg.av_interleaved_write_frame(outCtx, pkt);
                if (ret < 0) Log.Warn($"Remux write: {FF.ErrorText(ret)}");
                ffmpeg.av_packet_unref(pkt);
            }
            FF.Check(ffmpeg.av_write_trailer(outCtx), "write_trailer");
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            if (inCtx != null) ffmpeg.avformat_close_input(&inCtx);
            if (outCtx != null)
            {
                if (outCtx->pb != null) ffmpeg.avio_closep(&outCtx->pb);
                ffmpeg.avformat_free_context(outCtx);
            }
        }
    }
}
