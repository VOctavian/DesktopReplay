using DesktopReplay.Core;
using DesktopReplay.Core.Engine;
using Velopack;

namespace DesktopReplay;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles install/update/uninstall hooks from the Velopack installer.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => AutoStart.Set(true))
            .OnBeforeUninstallFastCallback(_ => AutoStart.Set(false))
            .Run();

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };

        int pr = Array.IndexOf(args, "--probe");
        if (pr >= 0 && pr + 1 < args.Length) return SelfTest.ProbeFile(args[pr + 1]);

        int st = Array.IndexOf(args, "--selftest");
        if (st >= 0)
        {
            var dir = st + 1 < args.Length ? args[st + 1] : Path.Combine(Path.GetTempPath(), "DesktopReplaySelfTest");
            var codec = st + 2 < args.Length ? args[st + 2] : null;
            return Task.Run(() => SelfTest.RunAsync(dir, codec)).GetAwaiter().GetResult();
        }

        using var single = new SingleInstance("DesktopReplay-" + Environment.UserName);
        if (!single.IsFirst)
        {
            single.SignalFirst(); // the running instance opens its overlay
            return 0;
        }

        Log.Cleanup();
        Log.Info($"DesktopReplay {typeof(Program).Assembly.GetName().Version} starting ({string.Join(' ', args)})");
        Services.WindowHelper.AllowCapture = args.Contains("--debug-ui");
        var app = new App(single, startMinimized: args.Contains("--minimized"));
        app.InitializeComponent();
        return app.Run();
    }
}

/// <summary>Named mutex + event so a second launch just brings up the overlay of the first one.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _event;
    public bool IsFirst { get; }

    public SingleInstance(string name)
    {
        _mutex = new Mutex(true, name + "-mutex", out bool created);
        IsFirst = created;
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show");
    }

    public void SignalFirst() => _event.Set();

    public void Listen(Action onSignal)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try { _event.WaitOne(); }
                catch { return; }
                onSignal();
            }
        }) { IsBackground = true, Name = "SingleInstance" };
        t.Start();
    }

    public void Dispose()
    {
        if (IsFirst) try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
