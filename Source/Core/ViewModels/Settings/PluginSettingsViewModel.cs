using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using FluentAvalonia.UI.Controls;

using Newtonsoft.Json;

using Core.Framework.Models;
using Core.Models.Plugins;
using Core.Extensions;
using Core.Services;
using Core.Windows;

namespace Core.ViewModels.Settings;

public partial class PluginSettingsViewModel : ViewModelBase
{
    [ObservableProperty] private ObservableCollection<UnrealProject> _unrealProjects = [];

    [JsonIgnore] public string UnrealProjectCountLabel => UnrealProjects.Count > 0 ? $"{UnrealProjects.Count} Project" + (UnrealProjects.Count > 1 ? "s" : string.Empty) : "";
    [JsonIgnore] public bool HasUnrealProjects => UnrealProjects.Count > 0;
    [JsonIgnore] public bool HasNoUnrealProjects => UnrealProjects.Count == 0;

    public PluginSettingsViewModel()
    {
        Track(UnrealProjects);
    }

    public override async Task Initialize()
    {
        /* Deserialized projects only carry their path, the rest is read off disk */
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var project in UnrealProjects.ToArray())
            {
                project.Refresh();
            }
        });

        await base.Initialize();
    }

    partial void OnUnrealProjectsChanged(ObservableCollection<UnrealProject> value)
    {
        Track(value);
        NotifyUnrealProjectsChanged();
    }

    private void Track(ObservableCollection<UnrealProject> projects)
    {
        projects.CollectionChanged -= OnUnrealProjectsCollectionChanged;
        projects.CollectionChanged += OnUnrealProjectsCollectionChanged;
    }

    private void OnUnrealProjectsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyUnrealProjectsChanged();
    }

    private void NotifyUnrealProjectsChanged()
    {
        OnPropertyChanged(nameof(UnrealProjectCountLabel));
        OnPropertyChanged(nameof(HasUnrealProjects));
        OnPropertyChanged(nameof(HasNoUnrealProjects));
    }

    [RelayCommand]
    public async Task AddUnrealProject()
    {
        if (await App.BrowseFileDialog(fileTypes: Globals.UnrealProjectFileType) is not { } path) return;

        var filePath = NormalizePath(path);

        if (!File.Exists(filePath))
        {
            Info.Message("Invalid Project", "The selected .uproject file no longer exists.", InfoBarSeverity.Error);

            return;
        }

        if (UnrealProjects.Any(project => project.FilePath.Equals(filePath, System.StringComparison.OrdinalIgnoreCase)))
        {
            Info.Message("Already Registered", $"{Path.GetFileNameWithoutExtension(filePath)} is already in the list.", InfoBarSeverity.Warning);

            return;
        }

        var unrealProject = new UnrealProject { FilePath = filePath };
        unrealProject.Refresh();

        UnrealProjects.Add(unrealProject);
    }

    [RelayCommand]
    public void RemoveUnrealProject(UnrealProject project)
    {
        UnrealProjects.Remove(project);
    }

    /* Fetches the latest release, drops it into the project's Plugins folder and
     * compiles it. Long running, so it reports through the project's own status */
    [RelayCommand]
    public async Task InstallPlugin(UnrealProject project)
    {
        if (project.IsBusy) return;

        if (!await EnsureEngine(project)) return;

        OpenBuildLog(project);

        await UnrealPlugin.Install(project);
    }

    /* Brought to the front rather than opened twice, so a reinstall reuses the window */
    [RelayCommand]
    public void OpenBuildLog(UnrealProject project)
    {
        if (OpenLogs.TryGetValue(project, out var existing))
        {
            existing.Activate();

            return;
        }

        var window = new BuildLogWindow(project);

        OpenLogs[project] = window;
        window.Closed += (_, _) => OpenLogs.Remove(project);

        window.CenterToScreen(MainWM.Window);
        window.Show();
    }

    private Dictionary<UnrealProject, BuildLogWindow> OpenLogs { get; } = new();

    /* Engines that are on disk but registered nowhere cannot be found from the project
     * alone, so the one time that happens the user points at it and it is remembered */
    private async Task<bool> EnsureEngine(UnrealProject project)
    {
        if (project.EngineDirectory is not null) return true;

        Info.Message("Engine Not Found", $"Select the Unreal Engine folder to build {project.Name} with.", InfoBarSeverity.Warning);

        if (await App.BrowseFolderDialog() is not { } selected) return false;

        var engine = NormalizePath(selected);

        if (UnrealEngineInstall.BatchFile(engine, "Build.bat") is null)
        {
            Info.Message("Not an Engine Folder", @"That folder has no Engine\Build\BatchFiles\Build.bat.", InfoBarSeverity.Error);

            return false;
        }

        project.EngineOverride = engine;

        return true;
    }

    /* Hands the .uproject to the shell, which launches it through the version selector */
    [RelayCommand]
    public void OpenUnrealProject(UnrealProject project)
    {
        if (!project.Exists)
        {
            Info.Message("Missing Project", "The .uproject file could not be found on disk.", InfoBarSeverity.Error);

            return;
        }

        AppService.OpenLink(project.FilePath);
    }

    /* The storage provider hands paths back URI style, "/D:/Game/Game.uproject" */
    private static string NormalizePath(string path)
    {
        var trimmed = path.Length > 2 && path[0] == '/' && path[2] == ':' ? path[1..] : path;

        return Path.GetFullPath(trimmed);
    }
}
