using DesktopReplay.Core.Library;
using DesktopReplay.Core.Media;
using FFmpeg.AutoGen;

namespace DesktopReplay.Core.Engine;

/// <summary>
/// Engine checks that run without a GPU or a desktop (CI): generates a synthetic multi-track video
/// and exercises remux, exact cuts, Shorts, Discord/GIF export, waveforms, thumbnails and the packet buffer.
/// Screen capture and NVENC need real hardware and are covered by --selftest instead.
/// </summary>
public static unsafe class CiTest
{
    private const int W = 640, H = 360, Fps = 30, Seconds = 20, Rate = 48000;
    private static readonly string[] Titles = { "Mix", "Game", "Mic" };

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var report = new List<string>();
        int failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            report.Add($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
            if (!ok) failures++;
        }
        void Step(string name, Action a)
        {
            try { a(); }
            catch (Exception ex) { Check(name, false, ex.GetType().Name + ": " + ex.Message); }
        }

        FF.Init();
        string mkv = Path.Combine(dir, "source.mkv"), mp4 = Path.Combine(dir, "source.mp4");

        Step("generate", () =>
        {
            Generate(mkv);
            Check("generate synthetic video", File.Exists(mkv), $"{new FileInfo(mkv).Length / 1024} KB");
        });

        Step("remux", () =>
        {
            FileMuxer.Remux(mkv, mp4);
            var i = MediaProbe.Probe(mp4);
            Check("remux MKV → MP4 keeps all streams", i.Audio.Count == 3 && i.Width == W, $"{i.Width}x{i.Height}, {i.Audio.Count} audio");
            Check("duration", Math.Abs(i.Duration - Seconds) < 0.2, $"{i.Duration:F2}s");
            Check("track titles", i.Audio.Select(a => a.Title).SequenceEqual(Titles), string.Join(",", i.Audio.Select(a => a.Title)));
        });

        var info = File.Exists(mp4) ? MediaProbe.Probe(mp4) : null;
        List<AudioTrackEdit> Tracks(Func<int, bool>? include = null) =>
            info!.Audio.Select((a, i) => new AudioTrackEdit(a.Index, a.Title, include?.Invoke(i) ?? true, 1, 0, Seconds)).ToList();

        Step("cut", () =>
        {
            var out1 = Path.Combine(dir, "cut.mp4");
            ClipExporter.Export(new ExportRequest(mp4, out1, 2.5, 7.0, false, 0.5, Tracks(i => i != 1), false, "Merged"), null, CancellationToken.None);
            var (frames, fps) = CountFrames(out1);
            var o = MediaProbe.Probe(out1);
            Check("exact cut: 4.5 s = 135 frames", frames == 135, $"{frames} frames, {fps:F2} fps");
            Check("removed track is gone", o.Audio.Count == 2, $"{o.Audio.Count} audio");
        });

        Step("shorts", () =>
        {
            var out2 = Path.Combine(dir, "shorts.mp4");
            ClipExporter.Export(new ExportRequest(mp4, out2, 1, 6, true, 0.0, Tracks(), true, "Merged"), null, CancellationToken.None);
            var o = MediaProbe.Probe(out2);
            Check("shorts crop is 9:16", o.Width == 202 && o.Height == H, $"{o.Width}x{o.Height}");
            Check("tracks merged into one", o.Audio.Count == 1 && o.Audio[0].Title == "Merged", $"{o.Audio.Count} audio");
        });

        Step("discord", () =>
        {
            var out3 = Path.Combine(dir, "discord.mp4");
            ClipExporter.Export(new ExportRequest(mp4, out3, 0, Seconds, false, 0.5, Tracks(), false, "Merged", ExportPreset.Discord, 2_000_000), null, CancellationToken.None);
            long size = new FileInfo(out3).Length;
            var o = MediaProbe.Probe(out3);
            Check("discord preset fits the size limit", size <= 2_000_000, $"{size / 1024} KB of 1953 KB");
            Check("discord preset: H.264 + one audio track", o.VideoCodec == "h264" && o.Audio.Count == 1, $"{o.VideoCodec}, {o.Audio.Count} audio");
        });

        Step("gif", () =>
        {
            var out4 = Path.Combine(dir, "clip.gif");
            ClipExporter.Export(new ExportRequest(mp4, out4, 2, 4, false, 0.5, Tracks(), false, "Merged", ExportPreset.Gif), null, CancellationToken.None);
            var o = MediaProbe.Probe(out4);
            Check("gif preset", o.VideoCodec == "gif" && o.Width == 480 && o.Audio.Count == 0, $"{o.VideoCodec} {o.Width}x{o.Height}");
        });

        Step("waveform", () =>
        {
            var w = Waveform.Compute(mp4, 200);
            Check("waveform for every track", w.Count == 3);
            // the "Game" track is silent in the first half and loud in the second
            var game = w[info!.Audio[1].Index];
            float first = game.Take(90).Max(), second = game.Skip(110).Max();
            Check("waveform follows the signal", first < 0.05 && second > 0.3, $"first half {first:F2}, second half {second:F2}");
        });

        Step("thumbnails", () =>
        {
            var strip = MediaLibrary.Filmstrip(mp4, 6, 160);
            Check("filmstrip frames decoded", strip.Count(b => b != null) == 6, $"{strip.Count(b => b != null)}/6");
        });

        Step("packet store", () =>
        {
            var bufDir = Path.Combine(dir, "buffer");
            using (var store = new PacketStore(bufDir, TimeSpan.FromSeconds(30), true))
            {
                for (int i = 0; i < 300; i++)
                    store.Add(new EncodedPacket { Stream = i % 2, Pts = i * 33_333L, Dts = i * 33_333L, Duration = 33_333, Key = i % 60 == 0, Data = new byte[1000 + i] });
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (store.LatestUs < 299 * 33_333L && DateTime.UtcNow < deadline) Thread.Sleep(20);
                Thread.Sleep(200);
                var back = store.ReadSince(0).ToList();
                Check("buffer round trip", back.Count == 300 && back[299].Data.Length == 1299 && back.Count(p => p.Key) == 5, $"{back.Count} packets");
                store.Paused = true;
                store.Add(new EncodedPacket { Stream = 0, Pts = 400 * 33_333L, Dts = 400 * 33_333L, Data = new byte[10] });
                Thread.Sleep(200);
                Check("paused buffer keeps old data, skips new", store.ReadSince(0).Count() == 300);
            }
        });

        Step("disk", () =>
        {
            long free = Storage.FreeBytes(dir);
            Check("free space query", free > 0, $"{free >> 20} MB");
            Check("free space for a missing folder uses its parent", Storage.FreeBytes(Path.Combine(dir, "no", "such")) > 0);
        });

        report.Insert(0, failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
        File.WriteAllLines(Path.Combine(dir, "ci-report.txt"), report);
        return failures == 0 ? 0 : 1;
    }

    private static void Drain(AVCodecContext* c, AVStream* st, AVFormatContext* fmt, AVPacket* pkt)
    {
        while (ffmpeg.avcodec_receive_packet(c, pkt) >= 0)
        {
            ffmpeg.av_packet_rescale_ts(pkt, c->time_base, st->time_base);
            pkt->stream_index = st->index;
            ffmpeg.av_interleaved_write_frame(fmt, pkt);
        }
    }

    private static (int Frames, double Fps) CountFrames(string path)
    {
        AVFormatContext* f = null;
        FF.Check(ffmpeg.avformat_open_input(&f, path, null, null), "open");
        try
        {
            ffmpeg.avformat_find_stream_info(f, null);
            int vs = ffmpeg.av_find_best_stream(f, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            var pkt = ffmpeg.av_packet_alloc();
            int n = 0;
            long first = long.MaxValue, last = long.MinValue;
            while (ffmpeg.av_read_frame(f, pkt) >= 0)
            {
                if (pkt->stream_index == vs)
                {
                    n++;
                    first = Math.Min(first, pkt->pts);
                    last = Math.Max(last, pkt->pts);
                }
                ffmpeg.av_packet_unref(pkt);
            }
            ffmpeg.av_packet_free(&pkt);
            double span = (last - first) * ffmpeg.av_q2d(f->streams[vs]->time_base);
            return (n, n > 1 ? (n - 1) / span : 0);
        }
        finally { ffmpeg.avformat_close_input(&f); }
    }

    // ---------- synthetic source ----------

    private static void Generate(string path)
    {
        AVFormatContext* fmt = null;
        FF.Check(ffmpeg.avformat_alloc_output_context2(&fmt, null, "matroska", path), "alloc");
        var vcodec = ffmpeg.avcodec_find_encoder_by_name("libx264");
        if (vcodec == null) vcodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MPEG4);
        var v = ffmpeg.avcodec_alloc_context3(vcodec);
        v->width = W; v->height = H; v->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        v->time_base = new AVRational { num = 1, den = Fps };
        v->framerate = new AVRational { num = Fps, den = 1 };
        v->gop_size = Fps * 2; v->max_b_frames = 0; v->bit_rate = 2_000_000;
        v->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        AVDictionary* vo = null;
        ffmpeg.av_dict_set(&vo, "preset", "ultrafast", 0);
        FF.Check(ffmpeg.avcodec_open2(v, vcodec, &vo), "open video encoder");
        ffmpeg.av_dict_free(&vo);
        var vst = ffmpeg.avformat_new_stream(fmt, null);
        ffmpeg.avcodec_parameters_from_context(vst->codecpar, v);
        vst->time_base = v->time_base;

        var acodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        var aud = new AVCodecContext*[Titles.Length];
        var ast = new AVStream*[Titles.Length];
        for (int t = 0; t < Titles.Length; t++)
        {
            var a = ffmpeg.avcodec_alloc_context3(acodec);
            a->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP; a->sample_rate = Rate; a->bit_rate = 128_000;
            ffmpeg.av_channel_layout_default(&a->ch_layout, 2);
            a->time_base = new AVRational { num = 1, den = Rate };
            a->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            FF.Check(ffmpeg.avcodec_open2(a, acodec, null), "open aac");
            aud[t] = a;
            var st = ffmpeg.avformat_new_stream(fmt, null);
            ffmpeg.avcodec_parameters_from_context(st->codecpar, a);
            st->time_base = a->time_base;
            ffmpeg.av_dict_set(&st->metadata, "title", Titles[t], 0);
            if (t == 0) st->disposition = ffmpeg.AV_DISPOSITION_DEFAULT;
            ast[t] = st;
        }
        FF.Check(ffmpeg.avio_open(&fmt->pb, path, ffmpeg.AVIO_FLAG_WRITE), "open file");
        FF.Check(ffmpeg.avformat_write_header(fmt, null), "header");

        var pkt = ffmpeg.av_packet_alloc();

        var vf = ffmpeg.av_frame_alloc();
        vf->width = W; vf->height = H; vf->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
        ffmpeg.av_frame_get_buffer(vf, 0);
        var af = ffmpeg.av_frame_alloc();
        af->nb_samples = aud[0]->frame_size; af->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP; af->sample_rate = Rate;
        ffmpeg.av_channel_layout_default(&af->ch_layout, 2);
        ffmpeg.av_frame_get_buffer(af, 0);

        long samplePos = 0;
        for (int i = 0; i < Fps * Seconds; i++)
        {
            ffmpeg.av_frame_make_writable(vf);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    vf->data[0][y * vf->linesize[0] + x] = (byte)((x + y + i * 4) & 0xFF); // moving gradient
            for (int y = 0; y < H / 2; y++)
                for (int x = 0; x < W / 2; x++)
                {
                    vf->data[1][y * vf->linesize[1] + x] = (byte)(128 + i % 64);
                    vf->data[2][y * vf->linesize[2] + x] = (byte)(128 - i % 64);
                }
            vf->pts = i;
            ffmpeg.avcodec_send_frame(v, vf);
            Drain(v, vst, fmt, pkt);

            // audio up to the end of this video frame
            long until = (long)(i + 1) * Rate / Fps;
            while (samplePos + af->nb_samples <= until)
            {
                for (int t = 0; t < Titles.Length; t++)
                {
                    ffmpeg.av_frame_make_writable(af);
                    var l = (float*)af->data[0];
                    var r = (float*)af->data[1];
                    for (int s = 0; s < af->nb_samples; s++)
                    {
                        double time = (samplePos + s) / (double)Rate;
                        // track 2 ("Game") is silent for the first half, then loud
                        double amp = t == 1 ? (time < Seconds / 2.0 ? 0 : 0.8) : 0.3;
                        float val = (float)(amp * Math.Sin(2 * Math.PI * (220 * (t + 1)) * time));
                        l[s] = val; r[s] = val;
                    }
                    af->pts = samplePos;
                    ffmpeg.avcodec_send_frame(aud[t], af);
                    Drain(aud[t], ast[t], fmt, pkt);
                }
                samplePos += af->nb_samples;
            }
        }
        ffmpeg.avcodec_send_frame(v, null); Drain(v, vst, fmt, pkt);
        for (int t = 0; t < Titles.Length; t++) { ffmpeg.avcodec_send_frame(aud[t], null); Drain(aud[t], ast[t], fmt, pkt); }
        ffmpeg.av_write_trailer(fmt);

        ffmpeg.av_frame_free(&vf);
        ffmpeg.av_frame_free(&af);
        ffmpeg.av_packet_free(&pkt);
        ffmpeg.avcodec_free_context(&v);
        for (int t = 0; t < Titles.Length; t++) { var a = aud[t]; ffmpeg.avcodec_free_context(&a); }
        ffmpeg.avio_closep(&fmt->pb);
        ffmpeg.avformat_free_context(fmt);
    }
}
