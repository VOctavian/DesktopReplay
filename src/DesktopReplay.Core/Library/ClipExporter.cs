using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DesktopReplay.Core.Media;
using FFmpeg.AutoGen;

namespace DesktopReplay.Core.Library;

/// <param name="StreamIndex">Audio stream index in the source file.</param>
/// <param name="From">Start of the audible part, seconds on the source timeline.</param>
/// <param name="To">End of the audible part, seconds on the source timeline.</param>
public sealed record AudioTrackEdit(int StreamIndex, string Title, bool Include, double Volume, double From, double To);

public enum ExportPreset { Original, Discord, Gif }

/// <param name="ShortsX">Horizontal position of the 9:16 crop: 0 = left edge, 1 = right edge.</param>
/// <param name="TargetBytes">Discord preset: the file must not be larger than this.</param>
public sealed record ExportRequest(
    string Input, string Output, double From, double To,
    bool Shorts, double ShortsX, IReadOnlyList<AudioTrackEdit> Tracks, bool MergeAudio, string MergedTitle,
    ExportPreset Preset = ExportPreset.Original, long TargetBytes = 0);

/// <summary>
/// Frame-accurate cut: decodes, trims, optionally crops to 9:16 and mixes audio tracks, then re-encodes
/// (NVENC, same codec as the source, at the source bitrate).
/// </summary>
public static unsafe class ClipExporter
{
    private const int BUFFERSRC_FLAG_KEEP_REF = 8;

    private sealed class Input
    {
        public int Stream;
        public AVCodecContext* Dec;
        public AVFilterContext* Src;
        public string Label = "";
    }

    private sealed class Output
    {
        public AVFilterContext* Sink;
        public AVCodecContext* Enc;
        public AVStream* St;
        public string Label = "";
        public bool Done;
    }

    public static Task ExportAsync(ExportRequest r, IProgress<double>? progress, CancellationToken ct) =>
        Task.Factory.StartNew(() => Export(r, progress, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public const long DiscordLimit = 20L * 1000 * 1000; // stays under 20 MB however Discord counts megabytes

    public static void Export(ExportRequest r, IProgress<double>? progress, CancellationToken ct)
    {
        if (r.Preset != ExportPreset.Discord) { ExportOnce(r, progress, ct, 1); return; }
        // size-targeted: if the encoder overshoots, retry with a proportionally lower budget
        double scale = 1;
        for (int attempt = 1; ; attempt++)
        {
            ExportOnce(r, progress, ct, scale);
            long size = new FileInfo(r.Output).Length;
            Log.Info($"Export (Discord) attempt {attempt}: {size / 1024} KB, limit {r.TargetBytes / 1024} KB");
            if (size <= r.TargetBytes || attempt == 3) return;
            scale *= r.TargetBytes / (double)size * 0.95;
        }
    }

    private static void ExportOnce(ExportRequest r, IProgress<double>? progress, CancellationToken ct, double budgetScale)
    {
        FF.Init();
        AVFormatContext* inFmt = null, outFmt = null;
        AVFilterGraph* graph = null;
        var inputs = new List<Input>();
        var outputs = new List<Output>();
        var pkt = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        var outPkt = ffmpeg.av_packet_alloc();
        bool ok = false;
        try
        {
            FF.Check(ffmpeg.avformat_open_input(&inFmt, r.Input, null, null), "open input");
            FF.Check(ffmpeg.avformat_find_stream_info(inFmt, null), "find_stream_info");
            double offset = inFmt->start_time != ffmpeg.AV_NOPTS_VALUE ? inFmt->start_time / (double)ffmpeg.AV_TIME_BASE : 0;

            AVCodec* vcodec = null;
            int vs = FF.Check(ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &vcodec, 0), "no video stream");
            var vst = inFmt->streams[vs];
            var fr = vst->avg_frame_rate.num > 0 ? vst->avg_frame_rate : vst->r_frame_rate;
            double fps = fr.num > 0 && fr.den > 0 ? fr.num / (double)fr.den : 30;
            double halfFrame = 0.5 / fps;

            var tracks = new List<AudioTrackEdit>();
            if (r.Preset != ExportPreset.Gif)
                foreach (var t in r.Tracks)
                    if (t.Include && t.StreamIndex < inFmt->nb_streams && inFmt->streams[t.StreamIndex]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                        tracks.Add(t);
            // Discord plays only the first audio track: keep one (or the merge of all)
            if (r.Preset == ExportPreset.Discord && !r.MergeAudio && tracks.Count > 1) tracks = tracks.Take(1).ToList();
            bool merge = r.MergeAudio || r.Preset == ExportPreset.Discord;

            long srcBitrate = vst->codecpar->bit_rate;
            if (srcBitrate <= 0 && inFmt->duration > 0)
                srcBitrate = (long)(new FileInfo(r.Input).Length * 8 / (inFmt->duration / (double)ffmpeg.AV_TIME_BASE) * 0.9);
            if (srcBitrate <= 0) srcBitrate = 20_000_000;
            string codecName = ffmpeg.avcodec_get_name(vst->codecpar->codec_id);

            // Discord: split the size budget between audio and video
            long discordAudio = 0, discordVideo = 0;
            if (r.Preset == ExportPreset.Discord)
            {
                double secs = Math.Max(0.5, r.To - r.From);
                double totalBps = r.TargetBytes * 8 * 0.95 * budgetScale / secs; // ~5% container overhead
                discordAudio = tracks.Count == 0 ? 0 : totalBps > 1_000_000 ? 128_000 : 96_000;
                discordVideo = (long)Math.Max(120_000, totalBps - discordAudio);
                // never above the source quality: short clips stay small (H.264 needs ~1.5x the bits of HEVC)
                discordVideo = Math.Min(discordVideo, (long)(srcBitrate * (codecName == "hevc" ? 1.5 : 1.1)));
            }
            var used = new HashSet<int> { vs };
            foreach (var t in tracks) used.Add(t.StreamIndex);
            for (int i = 0; i < inFmt->nb_streams; i++)
                if (!used.Contains(i)) inFmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;

            // ---- decoders ----
            inputs.Add(new Input { Stream = vs, Dec = OpenDecoder(vst, vcodec), Label = "vin" });
            foreach (var t in tracks)
            {
                var st = inFmt->streams[t.StreamIndex];
                inputs.Add(new Input { Stream = t.StreamIndex, Dec = OpenDecoder(st, ffmpeg.avcodec_find_decoder(st->codecpar->codec_id)), Label = $"a{t.StreamIndex}" });
            }

            // ---- filter graph ----
            graph = ffmpeg.avfilter_graph_alloc();
            var vin = inputs[0];
            var sar = vin.Dec->sample_aspect_ratio;
            vin.Src = CreateFilter(graph, "buffer", "vin",
                $"video_size={vin.Dec->width}x{vin.Dec->height}:pix_fmt={(int)vin.Dec->pix_fmt}:time_base={vst->time_base.num}/{vst->time_base.den}" +
                $":pixel_aspect={Math.Max(0, sar.num)}/{Math.Max(1, sar.den)}:frame_rate={fr.num}/{Math.Max(1, fr.den)}");
            var layout = stackalloc byte[128];
            foreach (var inp in inputs.Skip(1))
            {
                var d = inp.Dec;
                ffmpeg.av_channel_layout_describe(&d->ch_layout, layout, 128);
                var st = inFmt->streams[inp.Stream];
                inp.Src = CreateFilter(graph, "abuffer", inp.Label,
                    $"time_base={st->time_base.num}/{st->time_base.den}:sample_rate={d->sample_rate}" +
                    $":sample_fmt={ffmpeg.av_get_sample_fmt_name(d->sample_fmt)}:channel_layout={Marshal.PtrToStringUTF8((IntPtr)layout)}");
            }

            double from = r.From + offset, to = r.To + offset;
            var desc = new StringBuilder();
            desc.Append($"[vin]trim=start={Num(Math.Max(0, from - halfFrame))}:end={Num(to - halfFrame)},setpts=PTS-STARTPTS");
            if (r.Shorts)
            {
                int cw = Math.Min(vin.Dec->width, (int)Math.Round(vin.Dec->height * 9.0 / 16.0)) & ~1;
                int cx = (int)Math.Round((vin.Dec->width - cw) * Math.Clamp(r.ShortsX, 0, 1)) & ~1;
                desc.Append($",crop={cw}:{vin.Dec->height & ~1}:{cx}:0");
            }
            int outW = r.Shorts ? Math.Min(vin.Dec->width, (int)Math.Round(vin.Dec->height * 9.0 / 16.0)) & ~1 : vin.Dec->width;
            int outH = vin.Dec->height & ~1;
            if (r.Preset == ExportPreset.Gif)
            {
                // 15 fps, small size, one optimized palette for the whole clip
                string scale = r.Shorts ? (outH > 640 ? "-2:640" : "-2:-2") : (outW > 480 ? "480:-2" : "-2:-2");
                desc.Append($",fps=15,scale={scale}:flags=lanczos,split[g1][g2];[g1]palettegen=max_colors=256:stats_mode=diff[pal];" +
                            "[g2][pal]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle[vout]");
            }
            else
            {
                if (r.Preset == ExportPreset.Discord)
                {
                    // pick a resolution the bitrate can carry (~0.085 bits per pixel per frame for H.264)
                    double maxPixels = discordVideo / (0.085 * Math.Min(fps, 60));
                    double s = Math.Min(1, Math.Sqrt(maxPixels / ((double)outW * outH)));
                    int h = Math.Max(240, (int)Math.Round(outH * s)) & ~1;
                    if (h < outH) desc.Append($",scale=-2:{h}:flags=lanczos");
                }
                desc.Append(",format=yuv420p[vout]");
            }

            var audioLabels = new List<(string Label, string Title)>();
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                desc.Append($";[a{t.StreamIndex}]atrim=start={Num(from)}:end={Num(to)},asetpts=PTS-STARTPTS");
                if (Math.Abs(t.Volume - 1) > 0.001) desc.Append($",volume={Num(t.Volume)}");
                double tf = Math.Max(0, t.From - r.From), tt = t.To - r.From;
                if (tf > 0.01 || tt < r.To - r.From - 0.01)
                    desc.Append($",volume=volume=0:enable='not(between(t\\,{Num(tf)}\\,{Num(tt)}))'");
                desc.Append($",aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[t{i}]");
                audioLabels.Add(($"t{i}", t.Title));
            }
            if (merge && audioLabels.Count > 1)
            {
                desc.Append(';');
                foreach (var a in audioLabels) desc.Append($"[{a.Label}]");
                desc.Append($"amix=inputs={audioLabels.Count}:normalize=0:duration=longest,aformat=sample_fmts=fltp:channel_layouts=stereo[am]");
                audioLabels = new() { ("am", r.MergedTitle) };
            }

            outputs.Add(new Output { Label = "vout", Sink = CreateFilter(graph, "buffersink", "vout", null) });
            foreach (var a in audioLabels) outputs.Add(new Output { Label = a.Label, Sink = CreateFilter(graph, "abuffersink", "o" + a.Label, null) });

            AVFilterInOut* outsList = null, insList = null;
            foreach (var inp in Enumerable.Reverse(inputs)) outsList = InOut(inp.Label, inp.Src, outsList);
            foreach (var o in Enumerable.Reverse(outputs)) insList = InOut(o.Label, o.Sink, insList);
            Log.Info($"Export graph: {desc}");
            int pr = ffmpeg.avfilter_graph_parse_ptr(graph, desc.ToString(), &insList, &outsList, null);
            ffmpeg.avfilter_inout_free(&insList);
            ffmpeg.avfilter_inout_free(&outsList);
            FF.Check(pr, "avfilter_graph_parse");
            FF.Check(ffmpeg.avfilter_graph_config(graph, null), "avfilter_graph_config");

            // ---- output ----
            bool gif = r.Preset == ExportPreset.Gif;
            bool mp4 = !gif && !r.Output.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);
            FF.Check(ffmpeg.avformat_alloc_output_context2(&outFmt, null, gif ? "gif" : mp4 ? "mp4" : "matroska", r.Output), "alloc output");
            bool globalHeader = (outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0;

            outputs[0].Enc = gif ? OpenGifEncoder(outputs[0].Sink)
                : r.Preset == ExportPreset.Discord ? OpenVideoEncoder(outputs[0].Sink, "h264", discordVideo, fps, globalHeader, constantRate: true)
                : OpenVideoEncoder(outputs[0].Sink, codecName, srcBitrate, fps, globalHeader);
            for (int i = 1; i < outputs.Count; i++)
            {
                var srcSt = inFmt->streams[tracks[Math.Min(i - 1, tracks.Count - 1)].StreamIndex];
                long abr = r.Preset == ExportPreset.Discord ? discordAudio
                    : srcSt->codecpar->bit_rate > 0 ? Math.Clamp(srcSt->codecpar->bit_rate, 96_000, 320_000) : 192_000;
                outputs[i].Enc = OpenAudioEncoder(outputs[i].Sink, abr, globalHeader);
            }

            for (int i = 0; i < outputs.Count; i++)
            {
                var o = outputs[i];
                o.St = ffmpeg.avformat_new_stream(outFmt, null);
                FF.Check(ffmpeg.avcodec_parameters_from_context(o.St->codecpar, o.Enc), "params from encoder");
                o.St->time_base = o.Enc->time_base;
                if (i == 0)
                {
                    ffmpeg.av_dict_copy(&o.St->metadata, vst->metadata, 0);
                }
                else
                {
                    string title = audioLabels[i - 1].Title;
                    if (!string.IsNullOrEmpty(title))
                    {
                        ffmpeg.av_dict_set(&o.St->metadata, "title", title, 0);
                        ffmpeg.av_dict_set(&o.St->metadata, "handler_name", title, 0);
                    }
                    if (i == 1) o.St->disposition = ffmpeg.AV_DISPOSITION_DEFAULT;
                }
            }
            ffmpeg.av_dict_copy(&outFmt->metadata, inFmt->metadata, 0);
            ffmpeg.av_dict_set(&outFmt->metadata, "encoder", "DesktopReplay", 0);
            FF.Check(ffmpeg.avio_open(&outFmt->pb, r.Output, ffmpeg.AVIO_FLAG_WRITE), "open output");
            AVDictionary* opts = null;
            if (mp4) ffmpeg.av_dict_set(&opts, "movflags", "+faststart", 0);
            int hr = ffmpeg.avformat_write_header(outFmt, &opts);
            ffmpeg.av_dict_free(&opts);
            FF.Check(hr, "write_header");

            // ---- seek and run ----
            if (from > 1)
                ffmpeg.av_seek_frame(inFmt, -1, (long)((from - 0.2) * ffmpeg.AV_TIME_BASE), ffmpeg.AVSEEK_FLAG_BACKWARD);

            var lastSec = inputs.ToDictionary(i => i.Stream, _ => double.MinValue);
            double span = Math.Max(0.001, r.To - r.From);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int ret = ffmpeg.av_read_frame(inFmt, pkt);
                if (ret < 0) break;
                int si = pkt->stream_index;
                var inp = inputs.FirstOrDefault(i => i.Stream == si);
                if (inp != null)
                {
                    var tb = inFmt->streams[inp.Stream]->time_base;
                    long ts = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
                    if (ts != ffmpeg.AV_NOPTS_VALUE) lastSec[inp.Stream] = ts * ffmpeg.av_q2d(tb);
                    if (ffmpeg.avcodec_send_packet(inp.Dec, pkt) >= 0) PumpDecoder(inp, frame);
                    if (inp.Stream == vs) progress?.Report(Math.Clamp((lastSec[vs] - from) / span, 0, 1));
                }
                ffmpeg.av_packet_unref(pkt);
                DrainOutputs(outputs, outFmt, frame, outPkt, false);
                if (lastSec.Values.All(s => s > to + 0.5)) break;
            }

            // flush decoders and filter graph
            foreach (var inp in inputs)
            {
                ffmpeg.avcodec_send_packet(inp.Dec, null);
                PumpDecoder(inp, frame);
                ffmpeg.av_buffersrc_add_frame_flags(inp.Src, null, 0);
            }
            DrainOutputs(outputs, outFmt, frame, outPkt, true);
            foreach (var o in outputs) Encode(o, null, outFmt, outPkt);
            FF.Check(ffmpeg.av_write_trailer(outFmt), "write_trailer");
            progress?.Report(1);
            ok = true;
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.av_packet_free(&outPkt);
            ffmpeg.av_frame_free(&frame);
            foreach (var i in inputs) { var d = i.Dec; ffmpeg.avcodec_free_context(&d); }
            foreach (var o in outputs) if (o.Enc != null) { var e = o.Enc; ffmpeg.avcodec_free_context(&e); }
            if (graph != null) ffmpeg.avfilter_graph_free(&graph);
            if (inFmt != null) ffmpeg.avformat_close_input(&inFmt);
            if (outFmt != null)
            {
                if (outFmt->pb != null) ffmpeg.avio_closep(&outFmt->pb);
                ffmpeg.avformat_free_context(outFmt);
            }
            if (!ok) try { File.Delete(r.Output); } catch { }
        }
    }

    private static AVCodecContext* OpenDecoder(AVStream* st, AVCodec* codec)
    {
        if (codec == null) throw new InvalidOperationException("No decoder for stream " + st->index);
        var c = ffmpeg.avcodec_alloc_context3(codec);
        FF.Check(ffmpeg.avcodec_parameters_to_context(c, st->codecpar), "params to decoder");
        c->pkt_timebase = st->time_base;
        c->thread_count = 0;
        FF.Check(ffmpeg.avcodec_open2(c, codec, null), "open decoder");
        return c;
    }

    private static AVFilterContext* CreateFilter(AVFilterGraph* g, string filter, string name, string? args)
    {
        AVFilterContext* ctx = null;
        FF.Check(ffmpeg.avfilter_graph_create_filter(&ctx, ffmpeg.avfilter_get_by_name(filter), name, args, null, g), $"create {filter}");
        return ctx;
    }

    private static AVFilterInOut* InOut(string label, AVFilterContext* ctx, AVFilterInOut* next)
    {
        var io = ffmpeg.avfilter_inout_alloc();
        io->name = ffmpeg.av_strdup(label);
        io->filter_ctx = ctx;
        io->pad_idx = 0;
        io->next = next;
        return io;
    }

    private static AVCodecContext* OpenGifEncoder(AVFilterContext* sink)
    {
        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_GIF);
        if (codec == null) throw new InvalidOperationException("No GIF encoder");
        var c = ffmpeg.avcodec_alloc_context3(codec);
        c->width = ffmpeg.av_buffersink_get_w(sink);
        c->height = ffmpeg.av_buffersink_get_h(sink);
        c->pix_fmt = (AVPixelFormat)ffmpeg.av_buffersink_get_format(sink);
        c->time_base = ffmpeg.av_buffersink_get_time_base(sink);
        c->framerate = ffmpeg.av_buffersink_get_frame_rate(sink);
        FF.Check(ffmpeg.avcodec_open2(c, codec, null), "open gif encoder");
        Log.Info($"Export: GIF {c->width}x{c->height}");
        return c;
    }

    private static AVCodecContext* OpenVideoEncoder(AVFilterContext* sink, string srcCodec, long bitrate, double fps, bool globalHeader, bool constantRate = false)
    {
        var names = constantRate ? new[] { "h264_nvenc", "libx264" } : srcCodec == "hevc" ? new[] { "hevc_nvenc", "h264_nvenc", "libx264" } : new[] { "h264_nvenc", "hevc_nvenc", "libx264" };
        var tb = ffmpeg.av_buffersink_get_time_base(sink);
        var rate = ffmpeg.av_buffersink_get_frame_rate(sink);
        if (rate.num <= 0) rate = ffmpeg.av_d2q(fps, 1000);
        foreach (var name in names)
        {
            var codec = ffmpeg.avcodec_find_encoder_by_name(name);
            if (codec == null) continue;
            var c = ffmpeg.avcodec_alloc_context3(codec);
            c->width = ffmpeg.av_buffersink_get_w(sink);
            c->height = ffmpeg.av_buffersink_get_h(sink);
            c->pix_fmt = (AVPixelFormat)ffmpeg.av_buffersink_get_format(sink);
            c->sample_aspect_ratio = ffmpeg.av_buffersink_get_sample_aspect_ratio(sink);
            c->time_base = tb;
            c->framerate = rate;
            c->gop_size = (int)Math.Round(fps * 2);
            c->max_b_frames = 0;
            c->bit_rate = bitrate;
            c->rc_max_rate = constantRate ? bitrate : bitrate * 3 / 2;
            c->rc_buffer_size = (int)Math.Min(int.MaxValue, constantRate ? bitrate : bitrate * 2);
            c->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            c->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            c->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            if (globalHeader) c->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            AVDictionary* o = null;
            if (name.EndsWith("_nvenc"))
            {
                ffmpeg.av_dict_set(&o, "preset", "p5", 0);
                ffmpeg.av_dict_set(&o, "tune", "hq", 0);
                ffmpeg.av_dict_set(&o, "rc", constantRate ? "cbr" : "vbr", 0);
                ffmpeg.av_dict_set(&o, "spatial-aq", "1", 0);
            }
            else
            {
                ffmpeg.av_dict_set(&o, "preset", "veryfast", 0);
                if (constantRate) ffmpeg.av_dict_set(&o, "nal-hrd", "cbr", 0);
                else
                {
                    ffmpeg.av_dict_set(&o, "crf", "18", 0);
                    c->bit_rate = 0;
                }
            }
            int ret = ffmpeg.avcodec_open2(c, codec, &o);
            ffmpeg.av_dict_free(&o);
            if (ret >= 0)
            {
                Log.Info($"Export: video encoder {name}, {c->width}x{c->height}, {bitrate / 1000} kbps");
                return c;
            }
            Log.Warn($"Export: {name} unavailable: {FF.ErrorText(ret)}");
            ffmpeg.avcodec_free_context(&c);
        }
        throw new InvalidOperationException("No usable video encoder");
    }

    private static AVCodecContext* OpenAudioEncoder(AVFilterContext* sink, long bitrate, bool globalHeader)
    {
        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        var c = ffmpeg.avcodec_alloc_context3(codec);
        c->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        c->sample_rate = ffmpeg.av_buffersink_get_sample_rate(sink);
        FF.Check(ffmpeg.av_buffersink_get_ch_layout(sink, &c->ch_layout), "sink layout");
        c->time_base = new AVRational { num = 1, den = c->sample_rate };
        c->bit_rate = bitrate;
        if (globalHeader) c->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        FF.Check(ffmpeg.avcodec_open2(c, codec, null), "open aac");
        ffmpeg.av_buffersink_set_frame_size(sink, (uint)c->frame_size);
        return c;
    }

    private static void PumpDecoder(Input inp, AVFrame* frame)
    {
        while (ffmpeg.avcodec_receive_frame(inp.Dec, frame) >= 0)
        {
            frame->pts = frame->best_effort_timestamp;
            int ret = ffmpeg.av_buffersrc_add_frame_flags(inp.Src, frame, BUFFERSRC_FLAG_KEEP_REF);
            ffmpeg.av_frame_unref(frame);
            if (ret < 0 && ret != ffmpeg.AVERROR_EOF) FF.Check(ret, "buffersrc add");
        }
    }

    private static void DrainOutputs(List<Output> outputs, AVFormatContext* fmt, AVFrame* frame, AVPacket* pkt, bool final)
    {
        foreach (var o in outputs)
        {
            if (o.Done) continue;
            while (true)
            {
                int ret = ffmpeg.av_buffersink_get_frame(o.Sink, frame);
                if (ret == FF.EAGAIN) break;
                if (ret == ffmpeg.AVERROR_EOF) { o.Done = true; break; }
                FF.Check(ret, "buffersink get");
                frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
                Encode(o, frame, fmt, pkt);
                ffmpeg.av_frame_unref(frame);
            }
        }
    }

    private static void Encode(Output o, AVFrame* frame, AVFormatContext* fmt, AVPacket* pkt)
    {
        int s = ffmpeg.avcodec_send_frame(o.Enc, frame);
        if (s < 0 && s != ffmpeg.AVERROR_EOF) FF.Check(s, "send_frame");
        while (true)
        {
            int ret = ffmpeg.avcodec_receive_packet(o.Enc, pkt);
            if (ret == FF.EAGAIN || ret == ffmpeg.AVERROR_EOF) return;
            FF.Check(ret, "receive_packet");
            ffmpeg.av_packet_rescale_ts(pkt, o.Enc->time_base, o.St->time_base);
            pkt->stream_index = o.St->index;
            FF.Check(ffmpeg.av_interleaved_write_frame(fmt, pkt), "write packet");
        }
    }
}
