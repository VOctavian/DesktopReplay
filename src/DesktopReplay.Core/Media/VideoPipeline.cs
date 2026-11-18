using System.Runtime.InteropServices;
using DesktopReplay.Core.Interop;
using DesktopReplay.Core.Settings;
using FFmpeg.AutoGen;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11VideoDevice = Vortice.Direct3D11.ID3D11VideoDevice;
using ID3D11VideoContext = Vortice.Direct3D11.ID3D11VideoContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace DesktopReplay.Core.Media;

public sealed record VideoConfig(int Fps, int OutputHeight, VideoCodec Codec, long BitrateBps, bool CaptureCursor, bool HdrToSdr, bool GpuPriorityBoost = true);

public sealed record Screenshot(int Width, int Height, byte[] Bgra, int MonitorNumber);

/// <summary>
/// One monitor: Windows.Graphics.Capture → D3D11 (exclusion masks, scale, BGRA→NV12 on GPU) → NVENC via FFmpeg.
/// Frames are produced at a constant rate; if the screen did not change the last frame is repeated.
/// </summary>
public sealed unsafe class VideoPipeline : IDisposable
{
    private readonly MonitorInfo _monitor;
    private readonly VideoConfig _cfg;
    private readonly int _streamIndex;
    private readonly long _t0Us;
    private readonly Action<EncodedPacket> _sink;
    private readonly Func<Native.RECT[]> _exclusions;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11DeviceContext1? _context1;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private ID3D11VideoProcessorEnumerator? _vpEnum;
    private ID3D11VideoProcessor? _vp;
    private ID3D11Texture2D? _latest;       // last captured frame (BGRA)
    private ID3D11Texture2D? _work;         // latest + exclusion masks
    private ID3D11RenderTargetView? _workRtv;
    private ID3D11VideoProcessorInputView? _vpIn;
    private ID3D11Texture2D? _nv12;
    private ID3D11VideoProcessorOutputView? _vpOut;
    private readonly object _texLock = new();
    private int _srcW, _srcH;
    private bool _hasFrame;
    private Direct3D11CaptureFrame? _pendingFrame;  // newest frame from Windows, not yet copied
    private bool _workMasked;                       // _work currently has exclusion masks painted on it
    private bool _sizeChanged;
    private Format _captureFormat = Format.B8G8R8A8_UNorm;

    private Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    private AVBufferRef* _hwDevice;
    private AVBufferRef* _hwFrames;
    private AVCodecContext* _enc;
    private AVFrame* _frame;
    private AVPacket* _pkt;

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _forceKeyframe;
    private TaskCompletionSource<Screenshot>? _screenshotRequest;

    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }
    public StreamDescriptor? Stream { get; private set; }
    public MonitorInfo Monitor => _monitor;
    public string EncoderName { get; private set; } = "";
    public event Action<string>? Failed;

    public VideoPipeline(MonitorInfo monitor, VideoConfig cfg, int streamIndex, long t0Us, Action<EncodedPacket> sink, Func<Native.RECT[]> exclusions)
    {
        _monitor = monitor;
        _cfg = cfg;
        _streamIndex = streamIndex;
        _t0Us = t0Us;
        _sink = sink;
        _exclusions = exclusions;
    }

    public void Start()
    {
        CreateDevice();
        StartCapture();
        _srcW = _monitor.Width;
        _srcH = _monitor.Height;
        ComputeOutputSize();
        CreateTextures(_srcW, _srcH);
        CreateVideoProcessor();
        CreateEncoder();
        _running = true;
        _thread = new Thread(EncodeLoop) { Name = $"Video {_monitor.Number}", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
        Log.Info($"Video pipeline started: monitor {_monitor.Number} {_srcW}x{_srcH} → {OutputWidth}x{OutputHeight}@{_cfg.Fps} {EncoderName} {_cfg.BitrateBps / 1_000_000.0:F1} Mbps");
    }

    // ---------- D3D11 ----------

    private void CreateDevice()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1((uint)_monitor.AdapterIndex, out var adapter).CheckError();
        using (adapter)
        {
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                levels, out _device, out _context).CheckError();
        }
        using (var mt = _device!.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        // Keep capture/encode GPU work from being starved by a game running at 100% GPU.
        Native.SetGpuPriority(_cfg.GpuPriorityBoost);
        if (_cfg.GpuPriorityBoost)
        {
            try
            {
                using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
                dxgiDevice.SetGPUThreadPriority(7);
            }
            catch { }
        }
        _context1 = _context!.QueryInterface<ID3D11DeviceContext1>();
        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();
    }

    private void StartCapture()
    {
        _winrtDevice = CaptureInterop.CreateWinRtDevice(_device!);
        _item = CaptureInterop.CreateItemForMonitor(_monitor.Handle);
        var pixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
        if (_cfg.HdrToSdr && _monitor.IsHdr)
        {
            pixelFormat = DirectXPixelFormat.R16G16B16A16Float;
            _captureFormat = Format.R16G16B16A16_Float;
        }
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, pixelFormat, PoolBuffers, _item.Size);
        _session = _framePool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = _cfg.CaptureCursor; } catch { }
        try { _session.IsBorderRequired = false; } catch { }
        _framePool.FrameArrived += OnFrameArrived;
        _item.Closed += (_, _) => Failed?.Invoke($"Monitor {_monitor.Number} capture closed");
        _session.StartCapture();
    }

    private const int PoolBuffers = 3;

    /// <summary>
    /// Keeps a reference to the newest frame only. The GPU copy happens once per output tick on the
    /// encode thread — a 144 Hz game would otherwise cost 144 full-screen copies per second.
    /// </summary>
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            var frame = sender.TryGetNextFrame();
            if (frame == null) return;
            var size = frame.ContentSize;
            Direct3D11CaptureFrame? old;
            lock (_texLock)
            {
                if (_latest == null) { frame.Dispose(); return; }
                if (size.Width != _srcW || size.Height != _srcH)
                {
                    // Resolution changed (e.g. a game switched display mode). Re-create textures on the encode thread;
                    // the encoder output size stays the same so the replay buffer survives.
                    _srcW = size.Width;
                    _srcH = size.Height;
                    _sizeChanged = true;
                    frame.Dispose();
                    sender.Recreate(_winrtDevice, _captureFormat == Format.R16G16B16A16_Float ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, size);
                    return;
                }
                if (_sizeChanged) { frame.Dispose(); return; }
                old = _pendingFrame;
                _pendingFrame = frame;
                _hasFrame = true;
            }
            old?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"FrameArrived: {ex.Message}");
        }
    }

    private void ComputeOutputSize()
    {
        int h = _cfg.OutputHeight <= 0 || _cfg.OutputHeight >= _monitor.Height ? _monitor.Height : _cfg.OutputHeight;
        int w = (int)Math.Round(h * (double)_monitor.Width / _monitor.Height);
        OutputWidth = w & ~1;
        OutputHeight = h & ~1;
    }

    private void CreateTextures(int w, int h)
    {
        _vpIn?.Dispose(); _workRtv?.Dispose(); _work?.Dispose(); _latest?.Dispose();
        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = _captureFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        _latest = _device!.CreateTexture2D(desc);
        _work = _device.CreateTexture2D(desc);
        _workRtv = _device.CreateRenderTargetView(_work);
        _context1!.ClearView(_workRtv, new Color4(0, 0, 0, 1), Array.Empty<RawRect>());
        if (_vpEnum != null) CreateInputView();
    }

    private void CreateVideoProcessor()
    {
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)Math.Max(_srcW, 1),
            InputHeight = (uint)Math.Max(_srcH, 1),
            OutputWidth = (uint)OutputWidth,
            OutputHeight = (uint)OutputHeight,
            InputFrameRate = new Rational((uint)_cfg.Fps, 1),
            OutputFrameRate = new Rational((uint)_cfg.Fps, 1),
            Usage = VideoUsage.OptimalQuality,
        };
        _videoDevice!.CreateVideoProcessorEnumerator(ref content, out _vpEnum).CheckError();
        _videoDevice.CreateVideoProcessor(_vpEnum, 0, out _vp).CheckError();

        _nv12 = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)OutputWidth,
            Height = (uint)OutputHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        });
        _videoDevice.CreateVideoProcessorOutputView(_nv12, _vpEnum, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
        }, out _vpOut).CheckError();
        CreateInputView();

        using var vc1 = _videoContext!.QueryInterfaceOrNull<ID3D11VideoContext1>();
        var inputSpace = _captureFormat == Format.R16G16B16A16_Float ? ColorSpaceType.RgbFullG10NoneP709 : ColorSpaceType.RgbFullG22NoneP709;
        if (vc1 != null)
        {
            vc1.VideoProcessorSetStreamColorSpace1(_vp, 0, inputSpace);
            vc1.VideoProcessorSetOutputColorSpace1(_vp, ColorSpaceType.YcbcrStudioG22LeftP709);
        }
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_vp, 0, false);
        _videoContext.VideoProcessorSetStreamFrameFormat(_vp, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetOutputBackgroundColor(_vp, true, new VideoColor { YCbCr = new VideoColorYCbCrA { Y = 16 / 255f, Cb = 0.5f, Cr = 0.5f, A = 1 } });
        UpdateRects();
    }

    private void CreateInputView()
    {
        _vpIn?.Dispose();
        _videoDevice!.CreateVideoProcessorInputView(_work!, _vpEnum!, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        }, out _vpIn).CheckError();
    }

    /// <summary>Letterboxes the source into the fixed encoder size, preserving aspect ratio.</summary>
    private void UpdateRects()
    {
        double scale = Math.Min((double)OutputWidth / _srcW, (double)OutputHeight / _srcH);
        int w = (int)Math.Round(_srcW * scale), h = (int)Math.Round(_srcH * scale);
        int x = (OutputWidth - w) / 2, y = (OutputHeight - h) / 2;
        _videoContext!.VideoProcessorSetStreamSourceRect(_vp!, 0, true, new RawRect(0, 0, _srcW, _srcH));
        _videoContext.VideoProcessorSetStreamDestRect(_vp!, 0, true, new RawRect(x, y, x + w, y + h));
        _videoContext.VideoProcessorSetOutputTargetRect(_vp!, true, new RawRect(0, 0, OutputWidth, OutputHeight));
    }

    // ---------- Encoder ----------

    public static string EncoderFor(VideoCodec c) => c switch
    {
        VideoCodec.Hevc => "hevc_nvenc",
        VideoCodec.Av1 => "av1_nvenc",
        _ => "h264_nvenc",
    };

    private void CreateEncoder()
    {
        EncoderName = EncoderFor(_cfg.Codec);
        var codec = ffmpeg.avcodec_find_encoder_by_name(EncoderName);
        if (codec == null) throw new InvalidOperationException($"Encoder {EncoderName} not available");

        _hwDevice = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        var devCtx = (AVHWDeviceContext*)_hwDevice->data;
        var d3d = (AVD3D11VADeviceContext*)devCtx->hwctx;
        Marshal.AddRef(_device!.NativePointer); // FFmpeg releases it when the hw device is freed
        d3d->device = (FFmpeg.AutoGen.ID3D11Device*)_device.NativePointer;
        FF.Check(ffmpeg.av_hwdevice_ctx_init(_hwDevice), "av_hwdevice_ctx_init");

        _hwFrames = ffmpeg.av_hwframe_ctx_alloc(_hwDevice);
        var frames = (AVHWFramesContext*)_hwFrames->data;
        frames->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        frames->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        frames->width = OutputWidth;
        frames->height = OutputHeight;
        frames->initial_pool_size = 0;
        ((AVD3D11VAFramesContext*)frames->hwctx)->BindFlags = (uint)BindFlags.RenderTarget;
        FF.Check(ffmpeg.av_hwframe_ctx_init(_hwFrames), "av_hwframe_ctx_init");

        _enc = ffmpeg.avcodec_alloc_context3(codec);
        _enc->width = OutputWidth;
        _enc->height = OutputHeight;
        _enc->time_base = FF.TimeBaseUs;
        _enc->framerate = new AVRational { num = _cfg.Fps, den = 1 };
        _enc->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
        _enc->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFrames);
        _enc->gop_size = _cfg.Fps * 2;
        _enc->max_b_frames = 0;
        _enc->bit_rate = _cfg.BitrateBps;
        _enc->rc_max_rate = (long)(_cfg.BitrateBps * 1.5);
        _enc->rc_buffer_size = (int)Math.Min(int.MaxValue, _cfg.BitrateBps * 2);
        _enc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        _enc->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        _enc->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        _enc->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        _enc->color_range = AVColorRange.AVCOL_RANGE_MPEG;

        AVDictionary* opts = null;
        ffmpeg.av_dict_set(&opts, "preset", "p5", 0);
        ffmpeg.av_dict_set(&opts, "tune", "hq", 0);
        ffmpeg.av_dict_set(&opts, "rc", "vbr", 0);
        ffmpeg.av_dict_set(&opts, "spatial-aq", "1", 0);
        ffmpeg.av_dict_set(&opts, "forced-idr", "1", 0);
        if (_cfg.Codec == VideoCodec.H264) ffmpeg.av_dict_set(&opts, "profile", "high", 0);
        if (_cfg.Codec == VideoCodec.Hevc) ffmpeg.av_dict_set(&opts, "profile", "main", 0);
        int ret = ffmpeg.avcodec_open2(_enc, codec, &opts);
        ffmpeg.av_dict_free(&opts);
        FF.Check(ret, $"avcodec_open2 {EncoderName}");

        _frame = ffmpeg.av_frame_alloc();
        _pkt = ffmpeg.av_packet_alloc();
        Stream = new StreamDescriptor(_streamIndex, true, $"Monitor {_monitor.Number}", _monitor.Number, _enc);
    }

    // ---------- Loop ----------

    public void RequestKeyframe() => _forceKeyframe = true;

    public Task<Screenshot> CaptureScreenshotAsync()
    {
        var tcs = new TaskCompletionSource<Screenshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _screenshotRequest = tcs;
        return tcs.Task;
    }

    private void EncodeLoop()
    {
        Native.JoinMmcss("Capture");
        using var timer = new PreciseTimer();
        long frameUs = 1_000_000 / _cfg.Fps;
        long startUs = Clock.NowUs();
        long index = 0;
        // Stats, logged every 10 s.
        long statFrames = 0, statSkipped = 0, statWorkUs = 0, statMaxUs = 0, statLateUs = 0, statStart = startUs;
        try
        {
            while (_running)
            {
                long targetUs = startUs + index * 1_000_000 / _cfg.Fps;
                timer.WaitUntilUs(targetUs);
                if (!_running) break;

                long now = Clock.NowUs();
                // Fell behind (system hiccup): skip missed ticks instead of bursting.
                if (now - targetUs > frameUs * 2)
                {
                    long newIndex = (now - startUs) * _cfg.Fps / 1_000_000;
                    statSkipped += newIndex - index;
                    index = newIndex;
                    targetUs = startUs + index * 1_000_000 / _cfg.Fps;
                }
                index++;
                statLateUs = Math.Max(statLateUs, now - targetUs);

                if (!_hasFrame && !_sizeChanged) continue;
                long t1 = Clock.NowUs();
                ProcessFrame();
                EncodeFrame(targetUs - _t0Us);
                long work = Clock.NowUs() - t1;
                statFrames++;
                statWorkUs += work;
                statMaxUs = Math.Max(statMaxUs, work);

                if (now - statStart >= 10_000_000)
                {
                    double secs = (now - statStart) / 1e6;
                    Log.Info($"Video {_monitor.Number}: {statFrames / secs:F1} fps encoded, {statSkipped} ticks skipped, work avg {statWorkUs / Math.Max(1, statFrames) / 1000.0:F1} ms max {statMaxUs / 1000.0:F1} ms, max late {statLateUs / 1000.0:F1} ms");
                    statFrames = statSkipped = statWorkUs = statMaxUs = statLateUs = 0;
                    statStart = now;
                }
            }
            Flush();
        }
        catch (Exception ex)
        {
            Log.Error($"Video pipeline {_monitor.Number} crashed", ex);
            _running = false;
            Failed?.Invoke(ex.Message);
        }
    }

    private void ProcessFrame()
    {
        Direct3D11CaptureFrame? frame;
        lock (_texLock)
        {
            if (_sizeChanged)
            {
                _pendingFrame?.Dispose();
                _pendingFrame = null;
                CreateTextures(_srcW, _srcH);
                UpdateRects();
                _sizeChanged = false;
                _hasFrame = false;
                Log.Info($"Monitor {_monitor.Number} source size changed to {_srcW}x{_srcH}");
                return;
            }
            frame = _pendingFrame;
            _pendingFrame = null;
        }

        // A new frame from Windows: one GPU copy into _latest. Without a new frame the previous one is repeated.
        bool fresh = false;
        if (frame != null)
        {
            using (frame)
            using (var tex = CaptureInterop.GetTexture(frame.Surface))
                _context!.CopyResource(_latest, tex);
            fresh = true;
        }

        var rects = _exclusions();
        // _work = _latest + masks; it only needs refreshing when the picture or the masks change.
        if (fresh || _workMasked || rects.Length > 0) _context!.CopyResource(_work, _latest);
        _workMasked = false;
        if (rects.Length > 0)
        {
            var local = new List<RawRect>(rects.Length);
            var b = _monitor.Bounds;
            double sx = (double)_srcW / b.Width, sy = (double)_srcH / b.Height;
            foreach (var r in rects)
            {
                var i = Native.RECT.Intersect(r, b);
                if (i.IsEmpty) continue;
                local.Add(new RawRect((int)((i.Left - b.Left) * sx), (int)((i.Top - b.Top) * sy), (int)Math.Ceiling((i.Right - b.Left) * sx), (int)Math.Ceiling((i.Bottom - b.Top) * sy)));
            }
            if (local.Count > 0)
            {
                _context1!.ClearView(_workRtv!, new Color4(0, 0, 0, 1), local.ToArray());
                _workMasked = true;
            }
        }

        var tcs = Interlocked.Exchange(ref _screenshotRequest, null);
        if (tcs != null)
        {
            try { tcs.TrySetResult(ReadBackWork()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }

        _videoContext!.VideoProcessorBlt(_vp!, _vpOut!, 0, 1, new[]
        {
            new VideoProcessorStream { Enable = true, InputSurface = _vpIn! },
        });
    }

    private Screenshot ReadBackWork()
    {
        if (_captureFormat != Format.B8G8R8A8_UNorm)
        {
            // HDR capture: convert via the video processor output is NV12; for simplicity, screenshot the NV12-less path is not supported.
            throw new NotSupportedException("Screenshot in HDR capture mode is not supported");
        }
        var desc = _work!.Description;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = _device!.CreateTexture2D(desc);
        _context!.CopyResource(staging, _work);
        var map = _context.Map(staging, 0, MapMode.Read);
        try
        {
            int w = (int)desc.Width, h = (int)desc.Height;
            var data = new byte[w * h * 4];
            fixed (byte* dst = data)
            {
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy((byte*)map.DataPointer + y * map.RowPitch, dst + y * w * 4, w * 4, w * 4);
            }
            // Force opaque alpha.
            for (int i = 3; i < data.Length; i += 4) data[i] = 255;
            return new Screenshot(w, h, data, _monitor.Number);
        }
        finally { _context.Unmap(staging, 0); }
    }

    private void EncodeFrame(long ptsUs)
    {
        FF.Check(ffmpeg.av_hwframe_get_buffer(_hwFrames, _frame, 0), "av_hwframe_get_buffer");
        var dstTex = (IntPtr)_frame->data[0];
        uint dstIndex = (uint)(ulong)_frame->data[1];
        using (var dst = new ID3D11Texture2D(dstTex))
        {
            Marshal.AddRef(dstTex); // the wrapper releases on Dispose; FFmpeg keeps its own reference
            _context!.CopySubresourceRegion(dst, dstIndex, 0, 0, 0, _nv12!, 0);
        }
        _frame->pts = ptsUs;
        if (_forceKeyframe)
        {
            _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
            _forceKeyframe = false;
        }
        else _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;

        int ret = ffmpeg.avcodec_send_frame(_enc, _frame);
        ffmpeg.av_frame_unref(_frame);
        if (ret < 0 && ret != FF.EAGAIN) FF.Check(ret, "avcodec_send_frame");
        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            int ret = ffmpeg.avcodec_receive_packet(_enc, _pkt);
            if (ret == FF.EAGAIN || ret == ffmpeg.AVERROR_EOF) return;
            FF.Check(ret, "avcodec_receive_packet");
            var data = new byte[_pkt->size];
            Marshal.Copy((IntPtr)_pkt->data, data, 0, _pkt->size);
            _sink(new EncodedPacket
            {
                Stream = _streamIndex,
                Pts = _pkt->pts,
                Dts = _pkt->dts,
                Duration = _pkt->duration > 0 ? _pkt->duration : 1_000_000 / _cfg.Fps,
                Key = (_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0,
                Data = data,
            });
            ffmpeg.av_packet_unref(_pkt);
        }
    }

    private void Flush()
    {
        if (_enc == null) return;
        ffmpeg.avcodec_send_frame(_enc, null);
        try { Drain(); } catch { }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(3000);
        try { _session?.Dispose(); } catch { }
        try { _framePool?.Dispose(); } catch { }
        lock (_texLock) { _pendingFrame?.Dispose(); _pendingFrame = null; }
        _session = null;
        _framePool = null;
        lock (_texLock)
        {
            if (_enc != null) { var e = _enc; ffmpeg.avcodec_free_context(&e); _enc = null; }
            if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
            if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
            if (_hwFrames != null) { var b = _hwFrames; ffmpeg.av_buffer_unref(&b); _hwFrames = null; }
            if (_hwDevice != null) { var b = _hwDevice; ffmpeg.av_buffer_unref(&b); _hwDevice = null; }
            _vpIn?.Dispose(); _vpOut?.Dispose(); _vp?.Dispose(); _vpEnum?.Dispose();
            _nv12?.Dispose(); _workRtv?.Dispose(); _work?.Dispose(); _latest?.Dispose();
            _latest = null;
            _videoContext?.Dispose(); _videoDevice?.Dispose(); _context1?.Dispose();
            _context?.Dispose(); _device?.Dispose();
        }
        Stream?.Dispose();
        _winrtDevice?.Dispose();
    }
}
