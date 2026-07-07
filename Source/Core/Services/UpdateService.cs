using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using FluentAvalonia.UI.Controls;
using SharpCompress.Archives;
using SharpCompress.Common;
using Core.API.Models.GitHub.Responses;
using Core.Extensions;
using Core.Framework;
using Core.WindowModels;
using Core.Windows;
using static System.Version;

namespace Core.Services;

public class UpdateService : IService
{
    private Version CurrentVersion = null!;
    private Version LastSavedVersion = null!;

    private GitHubReleaseResponse LatestRelease = null!;
    private Version LatestReleaseVersion = null!;
    private bool ShowAllModels = true;

    private void ShowModel()
    {
        if (!ShowAllModels)
        {
            if (CurrentVersion <= LastSavedVersion
                && Settings.Application.Version != string.Empty) return;
        
            if (CurrentVersion > LatestReleaseVersion) return;
        }
        
        var win = new GalleryWindow
        {
            Height = 678
        };

        win.CenterToScreen(MainWM.Window);
        win.Show();
        
        win.WM.Title = "Link Editor Only Profile";
        win.WM.Tag = true;
        win.WM.TagType = TagType.New;
        win.WM.SecondaryButtonEnabled = false;
        win.WM.Description = "Link two builds together so your main build can stay stripped of editor-only data while a linked profile provides the editor data it needs, such as materials and material functions.";
    }

    private async Task UpdateVersioning()
    {
        TryParse(VERSION, out CurrentVersion!);
        TryParse(Settings.Application.Version, out LastSavedVersion!);
        
        LatestRelease = (await RestAPI.GitHub.GetLatestRelease())!;
        if (LatestRelease is not null)
        {
            LatestReleaseVersion = new Version(LatestRelease.Name);
        }
    }
    
    public async void Initialize()
    {
        if (MainWM.Window == null) return;
        
        await UpdateVersioning();
        
        if (CurrentVersion < LatestReleaseVersion && CurrentVersion != null
            && LatestRelease is not null
            || ShowAllModels)
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

        if (LatestReleaseVersion != null && CurrentVersion != null || ShowAllModels)
        {
            if (CurrentVersion > LatestReleaseVersion
                && Settings.Application.Version != CurrentVersion.ToString() || ShowAllModels)
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
