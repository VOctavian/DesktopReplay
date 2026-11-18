using System.Runtime.InteropServices;
using DesktopReplay.Core.Media;
using FFmpeg.AutoGen;

namespace DesktopReplay.Core.Audio;

/// <summary>Encodes one interleaved stereo float track to AAC; packet timestamps in µs from the session start.</summary>
public sealed unsafe class AacEncoder : IDisposable
{
    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private readonly int _streamIndex;
    private readonly Action<EncodedPacket> _sink;
    private int _filled;
    private long _samplePos;

    public StreamDescriptor Stream { get; }

    public AacEncoder(int streamIndex, string title, int trackNumber, int bitrateKbps, Action<EncodedPacket> sink)
    {
        _streamIndex = streamIndex;
        _sink = sink;
        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _ctx->sample_rate = AudioFormat.Rate;
        ffmpeg.av_channel_layout_default(&_ctx->ch_layout, AudioFormat.Channels);
        _ctx->bit_rate = bitrateKbps * 1000L;
        _ctx->time_base = new AVRational { num = 1, den = AudioFormat.Rate };
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        FF.Check(ffmpeg.avcodec_open2(_ctx, codec, null), "avcodec_open2 aac");

        _frame = ffmpeg.av_frame_alloc();
        _frame->nb_samples = _ctx->frame_size;
        _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _frame->sample_rate = AudioFormat.Rate;
        FF.Check(ffmpeg.av_channel_layout_copy(&_frame->ch_layout, &_ctx->ch_layout), "layout copy");
        FF.Check(ffmpeg.av_frame_get_buffer(_frame, 0), "av_frame_get_buffer");
        _pkt = ffmpeg.av_packet_alloc();
        Stream = new StreamDescriptor(streamIndex, false, title, trackNumber, _ctx);
    }

    /// <summary>Pushes interleaved stereo samples (sample index continues from the previous call).</summary>
    public void Push(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / 2;
        int i = 0;
        while (i < frames)
        {
            if (_filled == 0) FF.Check(ffmpeg.av_frame_make_writable(_frame), "make_writable");
            var l = (float*)_frame->data[0];
            var r = (float*)_frame->data[1];
            int n = Math.Min(frames - i, _frame->nb_samples - _filled);
            for (int k = 0; k < n; k++)
            {
                l[_filled + k] = interleaved[(i + k) * 2];
                r[_filled + k] = interleaved[(i + k) * 2 + 1];
            }
            _filled += n;
            i += n;
            if (_filled == _frame->nb_samples)
            {
                _frame->pts = _samplePos;
                _samplePos += _filled;
                _filled = 0;
                FF.Check(ffmpeg.avcodec_send_frame(_ctx, _frame), "send_frame aac");
                Drain();
            }
        }
    }

    private void Drain()
    {
        while (true)
        {
            int ret = ffmpeg.avcodec_receive_packet(_ctx, _pkt);
            if (ret == FF.EAGAIN || ret == ffmpeg.AVERROR_EOF) return;
            FF.Check(ret, "receive_packet aac");
            var data = new byte[_pkt->size];
            Marshal.Copy((IntPtr)_pkt->data, data, 0, _pkt->size);
            long pts = _pkt->pts * 1_000_000 / AudioFormat.Rate;
            _sink(new EncodedPacket
            {
                Stream = _streamIndex,
                Pts = pts,
                Dts = _pkt->dts * 1_000_000 / AudioFormat.Rate,
                Duration = _pkt->duration * 1_000_000 / AudioFormat.Rate,
                Key = true,
                Data = data,
            });
            ffmpeg.av_packet_unref(_pkt);
        }
    }

    public void Dispose()
    {
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        Stream.Dispose();
    }
}

/// <summary>FFT based noise reduction (FFmpeg "afftdn") applied to the microphone.</summary>
public sealed unsafe class NoiseFilter : IDisposable
{
    private AVFilterGraph* _graph;
    private AVFilterContext* _src;
    private AVFilterContext* _sink;
    private AVFrame* _in;
    private AVFrame* _out;
    private long _pts;
    private float[] _tmp = new float[4096];

    public NoiseFilter(string filter = "afftdn=nr=18:nf=-38:tn=1,highpass=f=80")
    {
        _graph = ffmpeg.avfilter_graph_alloc();
        var abuffer = ffmpeg.avfilter_get_by_name("abuffer");
        var abuffersink = ffmpeg.avfilter_get_by_name("abuffersink");
        AVFilterContext* src = null, sink = null;
        FF.Check(ffmpeg.avfilter_graph_create_filter(&src, abuffer, "in",
            $"sample_rate={AudioFormat.Rate}:sample_fmt=flt:channel_layout=stereo:time_base=1/{AudioFormat.Rate}", null, _graph), "abuffer");
        FF.Check(ffmpeg.avfilter_graph_create_filter(&sink, abuffersink, "out", null, null, _graph), "abuffersink");
        _src = src;
        _sink = sink;

        var outputs = ffmpeg.avfilter_inout_alloc();
        var inputs = ffmpeg.avfilter_inout_alloc();
        outputs->name = ffmpeg.av_strdup("in");
        outputs->filter_ctx = _src;
        outputs->pad_idx = 0;
        outputs->next = null;
        inputs->name = ffmpeg.av_strdup("out");
        inputs->filter_ctx = _sink;
        inputs->pad_idx = 0;
        inputs->next = null;
        int ret = ffmpeg.avfilter_graph_parse_ptr(_graph, filter + ",aformat=sample_fmts=flt:channel_layouts=stereo", &inputs, &outputs, null);
        ffmpeg.avfilter_inout_free(&inputs);
        ffmpeg.avfilter_inout_free(&outputs);
        FF.Check(ret, "graph parse");
        FF.Check(ffmpeg.avfilter_graph_config(_graph, null), "graph config");
        _in = ffmpeg.av_frame_alloc();
        _out = ffmpeg.av_frame_alloc();
    }

    /// <summary>Processes interleaved stereo samples and writes the filtered output to the ring.</summary>
    public void Process(float[] data, int samples, AudioRing ring)
    {
        int frames = samples / 2;
        if (frames == 0) return;
        ffmpeg.av_frame_unref(_in);
        _in->nb_samples = frames;
        _in->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT;
        _in->sample_rate = AudioFormat.Rate;
        ffmpeg.av_channel_layout_default(&_in->ch_layout, 2);
        FF.Check(ffmpeg.av_frame_get_buffer(_in, 0), "get_buffer");
        Marshal.Copy(data, 0, (IntPtr)_in->data[0], samples);
        _in->pts = _pts;
        _pts += frames;
        FF.Check(ffmpeg.av_buffersrc_add_frame(_src, _in), "buffersrc_add");
        while (true)
        {
            int ret = ffmpeg.av_buffersink_get_frame(_sink, _out);
            if (ret < 0) break;
            int n = _out->nb_samples * 2;
            if (_tmp.Length < n) _tmp = new float[n];
            Marshal.Copy((IntPtr)_out->data[0], _tmp, 0, n);
            ring.Write(_tmp.AsSpan(0, n));
            ffmpeg.av_frame_unref(_out);
        }
    }

    public void Dispose()
    {
        if (_graph != null) { var g = _graph; ffmpeg.avfilter_graph_free(&g); _graph = null; }
        if (_in != null) { var f = _in; ffmpeg.av_frame_free(&f); _in = null; }
        if (_out != null) { var f = _out; ffmpeg.av_frame_free(&f); _out = null; }
    }
}
