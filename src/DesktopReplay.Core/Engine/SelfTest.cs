using DesktopReplay.Core.Media;
using DesktopReplay.Core.Settings;
using FFmpeg.AutoGen;

namespace DesktopReplay.Core.Engine;

/// <summary>End-to-end check used by "DesktopReplay.exe --selftest": buffer → save → record → screenshot.</summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string outputDir, string? codec = null)
    {
        var report = new List<string>();
        void R(string s) { report.Add(s); Log.Info("[selftest] " + s); }
        int failures = 0;
        try
        {
            Directory.CreateDirectory(outputDir);
            var s = SettingsStore.Load().Clone();
            s.OutputFolder = outputDir;
            s.RecordingsFolder = Path.Combine(outputDir, "recordings");
            s.ScreenshotsFolder = Path.Combine(outputDir, "screenshots");
            s.BufferFolder = Path.Combine(outputDir, "buffer");
            s.ReplaySeconds = 12;
            if (Enum.TryParse<VideoCodec>(codec, true, out var vc)) s.Codec = vc;
            s.ReplayEnabled = true;
            s.MaxFolderSizeGb = 0;

            FF.Init();
            R($"FFmpeg {Version()}");
            foreach (var e in new[] { "h264_nvenc", "hevc_nvenc", "av1_nvenc" }) R($"{e}: {(FF.HasEncoder(e) ? "present" : "missing")}");
            foreach (var m in Monitors.Enumerate()) R($"Monitor {m.DisplayName} {m.DeviceName} adapter={m.AdapterName}");

            using var engine = new ReplayEngine(s);
            var notices = new List<Notice>();
            engine.Notify += n => { lock (notices) notices.Add(n); };
            await engine.StartAsync();
            if (engine.LastError != null) { R("START FAILED: " + engine.LastError); return 2; }
            R("Session: " + engine.VideoSummary);

            await Task.Delay(14000);
            R($"Buffered {engine.BufferedDuration.TotalSeconds:F1}s, {engine.BufferedBytes / 1024} KB");
            var saved = await engine.SaveReplayAsync();
            R("Replay: " + string.Join(", ", saved));
            if (saved.Count == 0) failures++;
            foreach (var f in saved) failures += Probe(f, R, 10);

            await engine.ToggleRecordingAsync();
            await Task.Delay(4000);
            await engine.ToggleRecordingAsync();
            List<string> rec;
            lock (notices) rec = notices.Where(n => n.Kind == NoticeKind.RecordingStopped).SelectMany(n => n.Paths).ToList();
            R("Recording: " + string.Join(", ", rec));
            if (rec.Count == 0) failures++;
            foreach (var f in rec) failures += Probe(f, R, 3);

            var shots = await engine.ScreenshotAsync();
            R("Screenshot: " + string.Join(", ", shots));
            if (shots.Count == 0) failures++;

            lock (notices) foreach (var n in notices.Where(n => n.Kind == NoticeKind.Error)) { R("ERROR notice: " + n.Detail); failures++; }
            await engine.ShutdownAsync();
        }
        catch (Exception ex)
        {
            R("EXCEPTION: " + ex);
            failures++;
        }
        R(failures == 0 ? "RESULT: OK" : $"RESULT: {failures} failure(s)");
        File.WriteAllLines(Path.Combine(outputDir, "selftest.txt"), report);
        return failures == 0 ? 0 : 1;
    }

    private static string Version() => ffmpeg.av_version_info();

    /// <summary>"DesktopReplay.exe --probe file.mp4": writes a stream/frame-rate report next to the file.</summary>
    public static int ProbeFile(string path)
    {
        FF.Init();
        var lines = new List<string>();
        int r = Probe(path, lines.Add, 0);
        File.WriteAllLines(path + ".probe.txt", lines);
        return r;
    }

    private static unsafe int Probe(string path, Action<string> r, double minSeconds)
    {
        AVFormatContext* ctx = null;
        try
        {
            if (ffmpeg.avformat_open_input(&ctx, path, null, null) < 0) { r($"  cannot open {path}"); return 1; }
            ffmpeg.avformat_find_stream_info(ctx, null);
            double dur = ctx->duration / (double)ffmpeg.AV_TIME_BASE;
            r($"  {Path.GetFileName(path)}: {new FileInfo(path).Length / 1024} KB, {dur:F2}s, {ctx->nb_streams} streams");
            for (int i = 0; i < ctx->nb_streams; i++)
            {
                var st = ctx->streams[i];
                var title = ffmpeg.av_dict_get(st->metadata, "title", null, 0);
                var handler = ffmpeg.av_dict_get(st->metadata, "handler_name", null, 0);
                var name = title != null ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)title->value)
                    : handler != null ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)handler->value) : "";
                var p = st->codecpar;
                double sdur = st->duration > 0 ? st->duration * ffmpeg.av_q2d(st->time_base) : 0;
                r($"    #{i} {ffmpeg.avcodec_get_name(p->codec_id)} {(p->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO ? $"{p->width}x{p->height}" : $"{p->sample_rate}Hz {p->ch_layout.nb_channels}ch")} '{name}' dur={sdur:F2}s");
            }
            // Count video frames and the largest gap between them.
            int vIndex = ffmpeg.av_find_best_stream(ctx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            var pkt = ffmpeg.av_packet_alloc();
            long frames = 0, lastPts = long.MinValue, maxGap = 0;
            var tb = ctx->streams[vIndex]->time_base;
            while (ffmpeg.av_read_frame(ctx, pkt) >= 0)
            {
                if (pkt->stream_index == vIndex)
                {
                    frames++;
                    if (lastPts != long.MinValue) maxGap = Math.Max(maxGap, pkt->pts - lastPts);
                    lastPts = pkt->pts;
                }
                ffmpeg.av_packet_unref(pkt);
            }
            ffmpeg.av_packet_free(&pkt);
            double vdur = ctx->streams[vIndex]->duration * ffmpeg.av_q2d(tb);
            r($"    video: {frames} frames, {frames / Math.Max(0.001, vdur):F2} fps, max gap {maxGap * ffmpeg.av_q2d(tb) * 1000:F0} ms");

            int expectedStreams = 6;
            if (ctx->nb_streams != expectedStreams) { r($"  expected {expectedStreams} streams"); return 1; }
            if (dur < minSeconds) { r($"  too short (< {minSeconds}s)"); return 1; }
            return 0;
        }
        finally
        {
            if (ctx != null) ffmpeg.avformat_close_input(&ctx);
        }
    }
}
