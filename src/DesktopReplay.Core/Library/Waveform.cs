using DesktopReplay.Core.Media;
using FFmpeg.AutoGen;

namespace DesktopReplay.Core.Library;

/// <summary>Peak envelopes of every audio track (for drawing waveforms), computed in one decoding pass.</summary>
public static unsafe class Waveform
{
    /// <returns>Stream index → peaks (0..1), <paramref name="buckets"/> values spread over the file duration.</returns>
    public static Dictionary<int, float[]> Compute(string path, int buckets, CancellationToken ct = default)
    {
        FF.Init();
        var result = new Dictionary<int, float[]>();
        AVFormatContext* fmt = null;
        var decoders = new Dictionary<int, IntPtr>();
        var pkt = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            FF.Check(ffmpeg.avformat_open_input(&fmt, path, null, null), "open");
            FF.Check(ffmpeg.avformat_find_stream_info(fmt, null), "find_stream_info");
            double duration = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            if (duration <= 0) return result;
            double start = fmt->start_time != ffmpeg.AV_NOPTS_VALUE ? fmt->start_time / (double)ffmpeg.AV_TIME_BASE : 0;
            var timeBases = new Dictionary<int, AVRational>();
            for (int i = 0; i < fmt->nb_streams; i++)
            {
                var st = fmt->streams[i];
                if (st->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO) { st->discard = AVDiscard.AVDISCARD_ALL; continue; }
                var codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
                if (codec == null) { st->discard = AVDiscard.AVDISCARD_ALL; continue; }
                var c = ffmpeg.avcodec_alloc_context3(codec);
                ffmpeg.avcodec_parameters_to_context(c, st->codecpar);
                c->pkt_timebase = st->time_base;
                if (ffmpeg.avcodec_open2(c, codec, null) < 0) { ffmpeg.avcodec_free_context(&c); continue; }
                decoders[i] = (IntPtr)c;
                timeBases[i] = st->time_base;
                result[i] = new float[buckets];
            }

            while (ffmpeg.av_read_frame(fmt, pkt) >= 0)
            {
                ct.ThrowIfCancellationRequested();
                if (decoders.TryGetValue(pkt->stream_index, out var dp))
                {
                    var c = (AVCodecContext*)dp;
                    if (ffmpeg.avcodec_send_packet(c, pkt) >= 0)
                        while (ffmpeg.avcodec_receive_frame(c, frame) >= 0)
                        {
                            Accumulate(frame, result[pkt->stream_index], timeBases[pkt->stream_index], start, duration, buckets);
                            ffmpeg.av_frame_unref(frame);
                        }
                }
                ffmpeg.av_packet_unref(pkt);
            }
            return result;
        }
        finally
        {
            foreach (var dp in decoders.Values) { var c = (AVCodecContext*)dp; ffmpeg.avcodec_free_context(&c); }
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.av_frame_free(&frame);
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        }
    }

    private static void Accumulate(AVFrame* f, float[] peaks, AVRational tb, double start, double duration, int buckets)
    {
        long ts = f->best_effort_timestamp;
        if (ts == ffmpeg.AV_NOPTS_VALUE || f->sample_rate <= 0) return;
        double t0 = ts * ffmpeg.av_q2d(tb) - start;
        int n = f->nb_samples, ch = f->ch_layout.nb_channels;
        var fmt = (AVSampleFormat)f->format;
        bool planar = ffmpeg.av_sample_fmt_is_planar(fmt) != 0;
        // read ~every 8th sample: plenty for an envelope and much cheaper
        for (int i = 0; i < n; i += 8)
        {
            int b = (int)((t0 + i / (double)f->sample_rate) / duration * buckets);
            if ((uint)b >= (uint)buckets) continue;
            float peak = 0;
            for (int c = 0; c < ch; c++)
            {
                float v = Sample(f, fmt, planar, ch, c, i);
                if (v < 0) v = -v;
                if (v > peak) peak = v;
            }
            if (peak > peaks[b]) peaks[b] = Math.Min(1f, peak);
        }
    }

    private static float Sample(AVFrame* f, AVSampleFormat fmt, bool planar, int channels, int c, int i)
    {
        byte* p = planar ? f->extended_data[c] : f->extended_data[0];
        int idx = planar ? i : i * channels + c;
        return fmt switch
        {
            AVSampleFormat.AV_SAMPLE_FMT_FLTP or AVSampleFormat.AV_SAMPLE_FMT_FLT => ((float*)p)[idx],
            AVSampleFormat.AV_SAMPLE_FMT_S16P or AVSampleFormat.AV_SAMPLE_FMT_S16 => ((short*)p)[idx] / 32768f,
            AVSampleFormat.AV_SAMPLE_FMT_S32P or AVSampleFormat.AV_SAMPLE_FMT_S32 => ((int*)p)[idx] / 2147483648f,
            AVSampleFormat.AV_SAMPLE_FMT_DBLP or AVSampleFormat.AV_SAMPLE_FMT_DBL => (float)((double*)p)[idx],
            _ => 0,
        };
    }
}
