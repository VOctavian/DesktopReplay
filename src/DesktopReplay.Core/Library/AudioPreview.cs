using System.Runtime.InteropServices;
using DesktopReplay.Core.Interop;
using DesktopReplay.Core.Media;
using FFmpeg.AutoGen;

namespace DesktopReplay.Core.Library;

/// <summary>Live mix settings of one audio stream; the UI changes them while audio plays.</summary>
public sealed class PreviewTrack
{
    public int StreamIndex { get; init; }
    public volatile bool Audible;
    public double Volume = 1;
    public double From, To = double.MaxValue;
}

/// <summary>
/// Plays the audio of a file ourselves (decode with FFmpeg, mix the selected tracks, output via WASAPI):
/// the WPF player cannot pick an audio track and plays the wrong one on multi-track files.
/// </summary>
public sealed unsafe class AudioPreview : IDisposable
{
    private const int Rate = 48000;
    private readonly string _path;
    private readonly IReadOnlyList<PreviewTrack> _tracks;
    private Thread? _thread;
    private volatile bool _stop;
    private double _startSec;
    private long _framesWritten;
    private long _paddingFrames;

    public double Volume = 0.8;
    private float _peak;
    private bool _logged;
    public volatile bool Muted;

    public AudioPreview(string path, IReadOnlyList<PreviewTrack> tracks)
    {
        _path = path;
        _tracks = tracks;
    }

    public bool IsPlaying => _thread != null;

    /// <summary>Seconds of the file currently heard (estimated from what WASAPI has consumed).</summary>
    public double Position => _startSec + Math.Max(0, Interlocked.Read(ref _framesWritten) - Interlocked.Read(ref _paddingFrames)) / (double)Rate;

    public void Play(double fromSec)
    {
        Stop();
        if (_tracks.Count == 0) return;
        _stop = false;
        _startSec = fromSec;
        Interlocked.Exchange(ref _framesWritten, 0);
        Interlocked.Exchange(ref _paddingFrames, 0);
        _thread = new Thread(() => Run(fromSec)) { IsBackground = true, Name = "AudioPreview" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        var t = _thread;
        if (t == null) return;
        _stop = true;
        t.Join(1000);
        _thread = null;
    }

    public void Dispose() => Stop();

    // ---------- decoding ----------

    private sealed class Decoder
    {
        public PreviewTrack Track = null!;
        public AVCodecContext* Ctx;
        public SwrContext* Swr;
        public AVRational TimeBase;
        public readonly Queue<float> Samples = new(); // interleaved stereo
        public bool Started;
        public long SkipFrames;
    }

    private void Run(double fromSec)
    {
        IAudioClient? client = null;
        IAudioRenderClient? render = null;
        AVFormatContext* fmt = null;
        var decoders = new List<Decoder>();
        var pkt = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        var evt = new AutoResetEvent(false);
        try
        {
            FF.Init();
            FF.Check(ffmpeg.avformat_open_input(&fmt, _path, null, null), "open");
            FF.Check(ffmpeg.avformat_find_stream_info(fmt, null), "find_stream_info");
            var wanted = _tracks.ToDictionary(t => t.StreamIndex);
            for (int i = 0; i < fmt->nb_streams; i++)
            {
                var st = fmt->streams[i];
                if (!wanted.TryGetValue(i, out var track) || st->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    st->discard = AVDiscard.AVDISCARD_ALL;
                    continue;
                }
                var codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
                if (codec == null) { st->discard = AVDiscard.AVDISCARD_ALL; continue; }
                var c = ffmpeg.avcodec_alloc_context3(codec);
                ffmpeg.avcodec_parameters_to_context(c, st->codecpar);
                c->pkt_timebase = st->time_base;
                FF.Check(ffmpeg.avcodec_open2(c, codec, null), "open audio decoder");
                SwrContext* swr = null;
                AVChannelLayout stereo;
                ffmpeg.av_channel_layout_default(&stereo, 2);
                FF.Check(ffmpeg.swr_alloc_set_opts2(&swr, &stereo, AVSampleFormat.AV_SAMPLE_FMT_FLT, Rate,
                    &c->ch_layout, c->sample_fmt, c->sample_rate, 0, null), "swr_alloc");
                FF.Check(ffmpeg.swr_init(swr), "swr_init");
                decoders.Add(new Decoder { Track = track, Ctx = c, Swr = swr, TimeBase = st->time_base });
            }
            if (decoders.Count == 0) return;

            double offset = fmt->start_time != ffmpeg.AV_NOPTS_VALUE ? fmt->start_time / (double)ffmpeg.AV_TIME_BASE : 0;
            if (fromSec > 0.05)
                ffmpeg.av_seek_frame(fmt, -1, (long)((fromSec + offset) * ffmpeg.AV_TIME_BASE), ffmpeg.AVSEEK_FLAG_BACKWARD);

            // ---- WASAPI output ----
            var en = CoreAudio.CreateEnumerator();
            Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var dev));
            Marshal.ReleaseComObject(en);
            var iid = CoreAudio.IID_IAudioClient;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out var obj));
            Marshal.ReleaseComObject(dev);
            client = (IAudioClient)obj;
            var wf = WaveFormatEx.Float(Rate, 2);
            Marshal.ThrowExceptionForHR(client.Initialize(CoreAudio.AUDCLNT_SHAREMODE_SHARED,
                CoreAudio.AUDCLNT_STREAMFLAGS_EVENTCALLBACK | CoreAudio.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | CoreAudio.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                1_000_000 /* 100 ms */, 0, ref wf, IntPtr.Zero));
            client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle());
            client.GetBufferSize(out uint bufferFrames);
            var riid = CoreAudio.IID_IAudioRenderClient;
            Marshal.ThrowExceptionForHR(client.GetService(ref riid, out var svc));
            render = (IAudioRenderClient)svc;

            bool eof = false, started = false;
            var mix = new float[bufferFrames * 2];
            while (!_stop)
            {
                client.GetCurrentPadding(out uint padding);
                Interlocked.Exchange(ref _paddingFrames, padding);
                int free = (int)(bufferFrames - padding);
                if (free <= 0) { evt.WaitOne(50); continue; }

                // decode until every stream has enough samples (or the file ends)
                while (!eof && decoders.Any(d => d.Samples.Count < free * 2) && decoders.All(d => d.Samples.Count < Rate * 2 * 4))
                {
                    int r = ffmpeg.av_read_frame(fmt, pkt);
                    if (r < 0) { eof = true; break; }
                    int si = pkt->stream_index;
                    var d = decoders.FirstOrDefault(x => x.Track.StreamIndex == si);
                    if (d != null && ffmpeg.avcodec_send_packet(d.Ctx, pkt) >= 0)
                        while (ffmpeg.avcodec_receive_frame(d.Ctx, frame) >= 0)
                        {
                            Append(d, frame, fromSec + offset);
                            ffmpeg.av_frame_unref(frame);
                        }
                    ffmpeg.av_packet_unref(pkt);
                }
                if (eof && decoders.All(d => d.Samples.Count == 0))
                {
                    evt.WaitOne(50); // let the device drain, keep the clock running
                    continue;
                }

                double vol = Muted ? 0 : Volume;
                double t0 = fromSec + Interlocked.Read(ref _framesWritten) / (double)Rate;
                Array.Clear(mix, 0, free * 2);
                foreach (var d in decoders)
                {
                    var tr = d.Track;
                    for (int i = 0; i < free; i++)
                    {
                        if (d.Samples.Count < 2) break;
                        float l = d.Samples.Dequeue(), rr = d.Samples.Dequeue();
                        if (!tr.Audible) continue;
                        double t = t0 + i / (double)Rate;
                        if (t < tr.From || t > tr.To) continue;
                        mix[i * 2] += (float)(l * tr.Volume);
                        mix[i * 2 + 1] += (float)(rr * tr.Volume);
                    }
                }

                if (render.GetBuffer((uint)free, out var buf) != 0) break;
                var dst = new Span<float>((void*)buf, free * 2);
                for (int i = 0; i < free * 2; i++)
                {
                    dst[i] = Math.Clamp((float)(mix[i] * vol), -1f, 1f);
                    _peak = Math.Max(_peak, Math.Abs(dst[i]));
                }
                if (!_logged && _framesWritten > Rate)
                {
                    _logged = true;
                    Log.Info($"Audio preview: {decoders.Count(d => d.Track.Audible)}/{decoders.Count} tracks audible, peak {_peak:F3} in the first second");
                }
                render.ReleaseBuffer((uint)free, 0);
                Interlocked.Add(ref _framesWritten, free);
                if (!started) { client.Start(); started = true; }
                evt.WaitOne(50);
            }
            if (started) client.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn($"Audio preview failed: {ex.Message}");
        }
        finally
        {
            foreach (var d in decoders)
            {
                var c = d.Ctx; ffmpeg.avcodec_free_context(&c);
                var s = d.Swr; ffmpeg.swr_free(&s);
            }
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.av_frame_free(&frame);
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
            if (render != null) Marshal.ReleaseComObject(render);
            if (client != null) Marshal.ReleaseComObject(client);
            evt.Dispose();
        }
    }

    private static void Append(Decoder d, AVFrame* frame, double from)
    {
        int max = ffmpeg.swr_get_out_samples(d.Swr, frame->nb_samples);
        if (max <= 0) return;
        var tmp = new float[max * 2];
        int got;
        fixed (float* p = tmp)
        {
            var outp = (byte*)p;
            got = ffmpeg.swr_convert(d.Swr, &outp, max, frame->extended_data, frame->nb_samples);
        }
        if (got <= 0) return;
        int startIndex = 0;
        if (!d.Started)
        {
            d.Started = true;
            long ts = frame->best_effort_timestamp;
            double t = ts != ffmpeg.AV_NOPTS_VALUE ? ts * ffmpeg.av_q2d(d.TimeBase) : from;
            if (t < from) d.SkipFrames = (long)Math.Round((from - t) * Rate);
            else for (long i = (long)Math.Round((t - from) * Rate); i > 0; i--) { d.Samples.Enqueue(0); d.Samples.Enqueue(0); }
        }
        if (d.SkipFrames > 0)
        {
            int skip = (int)Math.Min(d.SkipFrames, got);
            d.SkipFrames -= skip;
            startIndex = skip;
        }
        for (int i = startIndex * 2; i < got * 2; i++) d.Samples.Enqueue(tmp[i]);
    }
}
