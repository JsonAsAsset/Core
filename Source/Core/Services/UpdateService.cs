using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Serilog;

using FluentAvalonia.UI.Controls;

using SharpCompress.Archives;
using SharpCompress.Common;

using Core.API.Models.GitHub.Responses;
using Core.Extensions;
using Core.Framework;
using Core.WindowModels;
using Core.Windows;

namespace Core.Services;

public class UpdateService : IService
{
    private Version? CurrentVersion;
    private Version? LastSavedVersion;

    private GitHubReleaseResponse? LatestRelease;
    private Version? LatestReleaseVersion;
    private bool ShowAllModels = true;

    private void ShowModel()
    {
        if (!ShowAllModels)
        {
            if (CurrentVersion is null) return;

            if (CurrentVersion <= LastSavedVersion
                && Settings.Application.Version != string.Empty) return;

            /* Only suppress once the latest release is actually known, or an offline launch
             * would hide this permanently */
        }

        var win = new GalleryWindow
        {
            Height = 470
        };

        win.CenterToScreen(MainWM.Window);
        win.Show();

        win.WM.Title = "What's New";
        win.WM.Tag = true;
        win.WM.TagType = TagType.New;
        win.WM.SecondaryButtonEnabled = false;
        win.WM.Description =
            "Fortnite's high resolution textures now load at full size. They stream in as you need them, instead of falling back to the smaller copy that ships with the game.\n\n" +
            "Vector fields, volume textures and cubemaps are supported.\n\n" +
            "You can also search your profiles now, and sort them by version or by when you last used them.";
    }

    /* GitHub release names are free text, and System.Version only accepts two to four numeric
     * components and throws on anything else. Take the leading numeric run so "v1.5.2",
     * "1.5.2 Hotfix" and "1.5.1.4.2" all resolve instead of taking the update check down. */
    private static bool TryParseVersion(string? value, out Version? version)
    {
        version = null;

        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim().TrimStart('v', 'V');

        var match = Regex.Match(text, @"^\d+(\.\d+)*");
        if (!match.Success) return false;

        var parts = match.Value.Split('.');

        var normalized = parts.Length switch
        {
            1 => $"{parts[0]}.0",
            > 4 => string.Join('.', parts.Take(4)),
            _ => match.Value
        };

        return Version.TryParse(normalized, out version);
    }

    private async Task UpdateVersioning()
    {
        if (!TryParseVersion(VERSION, out CurrentVersion))
        {
            Log.Warning("Could not parse the running version '{Version}'", VERSION);
        }

        TryParseVersion(Settings.Application.Version, out LastSavedVersion);

        LatestRelease = await RestAPI.GitHub.GetLatestRelease();

        if (LatestRelease is not null && !TryParseVersion(LatestRelease.Name, out LatestReleaseVersion))
        {
            Log.Warning("Could not parse the latest release name '{Name}'", LatestRelease.Name);
        }
    }
    
    public async void Initialize()
    {
        if (MainWM.Window == null) return;

        try
        {
            await RunUpdateCheck();
        }
        catch (Exception ex)
        {
            /* async void, so anything escaping here surfaces as an unhandled dispatcher
             * exception and abandons the rest of the check */
            Log.Warning(ex, "Update check failed");
        }
    }

    private async Task RunUpdateCheck()
    {
        await UpdateVersioning();

        var isOutdated = CurrentVersion is not null
                         && LatestReleaseVersion is not null
                         && CurrentVersion < LatestReleaseVersion;

        if (LatestRelease is not null && (isOutdated || ShowAllModels))
        {
            var win = new GalleryWindow
            {
                Height = 658
            };

            win.CenterToScreen(MainWM.Window);
            win.Show();
        
            win.WM.Title = $"{LatestRelease.Name} is now available!";
            win.WM.PrimaryButtonText = "Update";
            win.WM.Tag = true;
            win.WM.OnPrimaryButtonClick += () =>
            {
                var asset = LatestRelease.Assets.FirstOrDefault();
                if (asset != null)
                {
                    _ = DownloadAndInstall(LatestRelease.Name, asset.DownloadUrl);
                }
            };
            win.WM.TagType = TagType.Update;
            win.WM.Description = "Get the latest features and improvements in the new version.";
        }
        
        ShowModel();

        if (CurrentVersion is null) return;

        var isDevelopmental = LatestReleaseVersion is not null
                              && CurrentVersion > LatestReleaseVersion
                              && Settings.Application.Version != CurrentVersion.ToString();

        if (isDevelopmental || ShowAllModels)
        {
#if !DEBUG
                var win = new GalleryWindow();

                win.CenterToScreen(MainWM.Window);
                win.Show();
        
                win.WM.Title = $"{VERSION}";
                win.WM.Tag = true;
                win.WM.TagType = TagType.Developmental;
                win.WM.PrimaryButtonEnabled = false;
                win.WM.SecondaryButtonText = "Got it";
                win.WM.Description = $"You are running a developmental build of {APP_NAME}.\n\nThis issued version may be unstable.";
#endif
        }

        Settings.Application.Version = CurrentVersion.ToString();
    }

    private static async Task DownloadAndInstall(string versionName, string downloadUrl)
    {
        try
        {
            MainWM.Window.Hide();
            var installationFolder = new DirectoryInfo(Path.Combine(InstallationFolder.ToString(), versionName));
            
            if (!installationFolder.Exists)
            {
                installationFolder.Create();
            }
            
            var fileName = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
            var installPath = Path.Combine(installationFolder.FullName, fileName);
            
            var downloaded = await RestAPI.DownloadFileAsync(downloadUrl, installPath, _ => { });

            if (downloaded is null)
            {
                throw new InvalidOperationException("Download returned no data.");
            }
            
            using var archive = ArchiveFactory.Open(installPath);
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                entry.WriteToDirectory(
                    installationFolder.FullName,
                    new ExtractionOptions { ExtractFullPath = true, Overwrite = true }
                );
            }
            
            var exe = installationFolder
                .EnumerateFiles("*.exe", SearchOption.AllDirectories)
                .OrderByDescending(f => f.Length)
                .FirstOrDefault();

            if (exe != null && exe.Exists)
            {
                try
                {
                    Program.ReleaseMutex();

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = exe.FullName
                    };
                    Process.Start(startInfo);
                }
                catch (Exception launchEx)
                {
                    AppService.OpenLink(installationFolder.FullName);
                    throw new InvalidOperationException($"Failed to launch the new executable:\n{launchEx.Message}");
                }
            }
            else
            {
                AppService.OpenLink(installationFolder.FullName);
            }
            
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Update Failed",
                Content = $"Could not download or install the update:\n\n{ex.Message}",
                CloseButtonText = "Dismiss"
            };

            _ = dialog.ShowAsync();
        }
    }
}
