using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopReplay.Core.Settings;

public enum VideoCodec { H264, Hevc, Av1 }
public enum QualityPreset { Low, Medium, High, Ultra, Custom }
public enum MicMode { AlwaysOn, PushToTalk, PushToMute }
public enum ScreenshotFormat { Png, Jpg }

public sealed class HotkeySettings
{
    public string SaveReplay { get; set; } = "Alt+F10";
    public string ToggleReplay { get; set; } = "Alt+Shift+F10";
    public string ToggleRecording { get; set; } = "Alt+F9";
    public string Screenshot { get; set; } = "Alt+F1";
    public string Overlay { get; set; } = "Ctrl+Alt+Z";
}

public sealed class AudioSettings
{
    /// <summary>Endpoint id of the microphone; empty = system default communications/console capture device.</summary>
    public string MicDeviceId { get; set; } = "";
    public MicMode MicMode { get; set; } = MicMode.AlwaysOn;
    /// <summary>Key for push-to-talk / push-to-mute, e.g. "V", "Mouse4".</summary>
    public string PushKey { get; set; } = "";
    public bool NoiseSuppression { get; set; } = true;
    public int AudioBitrateKbps { get; set; } = 192;

    public double GameVolume { get; set; } = 1.0;
    public double MicVolume { get; set; } = 1.0;
    public double SystemVolume { get; set; } = 1.0;
    public double DiscordVolume { get; set; } = 1.0;

    public bool GameInMix { get; set; } = true;
    public bool MicInMix { get; set; } = true;
    public bool SystemInMix { get; set; } = true;
    public bool DiscordInMix { get; set; } = true;
}

public sealed class AppSettings
{
    public string Language { get; set; } = "ru";

    // Replay
    public bool ReplayEnabled { get; set; } = true;
    public int ReplaySeconds { get; set; } = 120;

    // Storage
    public string OutputFolder { get; set; } = AppPaths.DefaultOutput;
    public string BufferFolder { get; set; } = AppPaths.DefaultBuffer;
    public bool SubfolderPerGame { get; set; } = true;
    public double MaxFolderSizeGb { get; set; } = 100;
    public int WarnAtPercent { get; set; } = 75;
    public bool KeepMkv { get; set; }
    public ScreenshotFormat ScreenshotFormat { get; set; } = ScreenshotFormat.Png;

    // Video
    /// <summary>Device names (\.\DISPLAY1 ...). Empty = primary monitor.</summary>
    public List<string> Monitors { get; set; } = new();
    public int Fps { get; set; } = 30;
    /// <summary>Output height; 0 = native resolution.</summary>
    public int ResolutionHeight { get; set; }
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public QualityPreset Quality { get; set; } = QualityPreset.High;
    public int CustomBitrateMbps { get; set; } = 25;
    public bool CaptureCursor { get; set; } = true;
    public bool HdrToSdr { get; set; }
    public List<string> ExcludedProcesses { get; set; } = new();

    public AudioSettings Audio { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();

    // Notifications
    public bool NotifySound { get; set; } = true;
    public bool NotifyPopup { get; set; } = true;

    // System
    public bool AutoStart { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    public bool FirstRunDone { get; set; }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsStore.Options), SettingsStore.Options)!;

    /// <summary>True when the change requires the capture pipelines to restart (buffer is reset).</summary>
    public bool CaptureDiffers(AppSettings o) =>
        !Monitors.SequenceEqual(o.Monitors) || Fps != o.Fps || ResolutionHeight != o.ResolutionHeight ||
        Codec != o.Codec || Quality != o.Quality || CustomBitrateMbps != o.CustomBitrateMbps ||
        CaptureCursor != o.CaptureCursor || HdrToSdr != o.HdrToSdr || BufferFolder != o.BufferFolder ||
        Audio.MicDeviceId != o.Audio.MicDeviceId || Audio.AudioBitrateKbps != o.Audio.AudioBitrateKbps ||
        Audio.NoiseSuppression != o.Audio.NoiseSuppression;
}

public static class SettingsStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath { get; } = Path.Combine(AppPaths.Roaming, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load settings, using defaults", ex);
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
        }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(AppPaths.Roaming);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
        File.Move(tmp, FilePath, true);
    }
}
