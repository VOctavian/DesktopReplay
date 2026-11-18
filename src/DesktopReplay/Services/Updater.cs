using DesktopReplay.Core;
using Velopack;
using Velopack.Sources;

namespace DesktopReplay.Services;

/// <summary>Auto-update from GitHub Releases (Velopack packages built by build/release.ps1).</summary>
public sealed class Updater
{
    public const string RepoUrl = "https://github.com/VOctavian/DesktopReplay";

    private readonly UpdateManager _mgr = new(new GithubSource(RepoUrl, null, false));
    private UpdateInfo? _pending;

    public event Action<string>? UpdateReady;

    public bool IsInstalled => _mgr.IsInstalled;

    public async Task CheckAsync(bool interactive, Action<string>? status = null)
    {
        if (!_mgr.IsInstalled)
        {
            status?.Invoke(Loc.T("A.NotInstalled"));
            return;
        }
        try
        {
            if (_pending != null)
            {
                if (interactive) _mgr.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
                return;
            }
            status?.Invoke(Loc.T("A.Checking"));
            var info = await _mgr.CheckForUpdatesAsync();
            if (info == null)
            {
                status?.Invoke(Loc.T("A.UpToDate"));
                return;
            }
            var version = info.TargetFullRelease.Version.ToString();
            status?.Invoke(Loc.F("A.Downloading", version));
            await _mgr.DownloadUpdatesAsync(info);
            _pending = info;
            Log.Info($"Update {version} downloaded");
            status?.Invoke(Loc.F("N.UpdateReady", version));
            UpdateReady?.Invoke(version);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            status?.Invoke(ex.Message);
        }
    }

    /// <summary>Installs a downloaded update when the app exits normally.</summary>
    public void ApplyOnExit()
    {
        if (_pending == null) return;
        try { _mgr.WaitExitThenApplyUpdates(_pending.TargetFullRelease, silent: true, restart: false); }
        catch (Exception ex) { Log.Warn($"Apply update failed: {ex.Message}"); }
    }
}
