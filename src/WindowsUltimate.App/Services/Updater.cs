using System.Windows;
using WindowsUltimate.App.Dialogs;
using WindowsUltimate.App.Localization;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Updates;

namespace WindowsUltimate.App.Services;

/// <summary>Asks the user before updating; never downloads or installs anything on its own.</summary>
public static class Updater
{
    private static bool _busy;

    /// <summary>Background check at startup. Silent when up to date, offline, or when the user turned checks off.</summary>
    public static async Task CheckAtStartupAsync()
    {
#if DEBUG
        await Task.CompletedTask; // local development builds are versioned 1.0.0 and would always be "out of date"
#else
        if (!AppServices.Settings.CheckForUpdates || !UpdateService.IsConfigured) return;
        await Task.Delay(TimeSpan.FromSeconds(3));
        await CheckAsync(interactive: false);
#endif
    }

    /// <summary>Checks GitHub Releases. When interactive, also reports "up to date" and errors.</summary>
    public static async Task CheckAsync(bool interactive)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!UpdateService.IsConfigured)
            {
                if (interactive) Notifier.Warning(Loc.Get("UpdatesNotConfigured"));
                return;
            }
            UpdateInfo? update;
            try { update = await UpdateService.CheckAsync(); }
            catch (Exception ex)
            {
                Log.Warn("Update check failed: " + ex.Message);
                if (interactive) Notifier.Warning(Loc.Get("UpdateCheckFailed"));
                return;
            }

            if (update is null)
            {
                if (interactive) Notifier.Success(string.Format(Loc.Get("UpToDate"), UpdateService.CurrentVersion.ToString(3)));
                return;
            }
            await OfferAsync(update);
        }
        finally { _busy = false; }
    }

    private static async Task OfferAsync(UpdateInfo update)
    {
        var details = new List<string>();
        if (update.Published is { } date) details.Add(string.Format(Loc.Get("UpdatePublished"), date.ToLocalTime().ToString("g")));
        if (update.InstallerBytes > 0) details.Add(string.Format(Loc.Get("UpdateSize"), Format.Bytes(update.InstallerBytes)));
        details.AddRange(update.Notes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(12));

        if (!ConfirmDialog.Show(Loc.Get("UpdateAvailableTitle"),
                string.Format(Loc.Get("UpdateAvailableMessage"), update.Version.ToString(3), UpdateService.CurrentVersion.ToString(3)),
                details, Loc.Get("UpdateNow")))
            return;

        try
        {
            Notifier.Info(Loc.Get("UpdateDownloading"));
            var path = await UpdateService.DownloadAsync(update);
            UpdateService.StartInstaller(path);
            Application.Current.Shutdown(); // the installer replaces the files, then relaunches the app
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            Notifier.Error(Loc.Get("UpdateFailed") + " " + ex.Message);
        }
    }
}
