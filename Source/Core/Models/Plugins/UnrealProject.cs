using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

using Avalonia;
using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;

using Core.Models.Enums;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Serilog;

namespace Core.Models.Plugins;

/* An Unreal Engine project registered as an install target for the plugin.
 * Only the path is persisted, everything else is read back off disk on load */
public partial class UnrealProject : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectFolder))]
    [NotifyPropertyChangedFor(nameof(Exists))]
    private string _filePath = string.Empty;

    [ObservableProperty] [property: JsonIgnore]
    private string _name = string.Empty;

    /* The raw EngineAssociation, which is a version for launcher installs and a
     * generated identifier for source builds */
    [ObservableProperty] [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(EngineVersionLabel))]
    [NotifyPropertyChangedFor(nameof(EngineDirectory))]
    private string _engineVersion = string.Empty;

    [ObservableProperty] [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(PluginVersionLabel))]
    private string _pluginVersion = string.Empty;

    [ObservableProperty] [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(PluginVersionLabel))]
    private bool _isPluginInstalled;

    /* Drives the row's colour and its pulse, keeps the buttons disabled mid-install,
     * and swaps the build path out for live progress */
    [ObservableProperty] [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(HasSucceeded))]
    [NotifyPropertyChangedFor(nameof(HasFailed))]
    private EInstallState _state = EInstallState.Idle;

    [JsonIgnore] public bool IsBusy => State == EInstallState.Working;
    [JsonIgnore] public bool HasSucceeded => State == EInstallState.Succeeded;
    [JsonIgnore] public bool HasFailed => State == EInstallState.Failed;

    /* Outlives the install so the closing line, success or failure, stays readable */
    [ObservableProperty] [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    [NotifyPropertyChangedFor(nameof(HasNoStatus))]
    private string _status = string.Empty;

    [JsonIgnore] public bool IsIdle => !IsBusy;

    /* Everything the toolchain said during the last install, kept so the log window
     * can be opened after the fact rather than only while the build is running */
    [JsonIgnore] public ObservableCollection<BuildLogLine> BuildLog { get; } = [];

    [JsonIgnore] public bool HasBuildLog => BuildLog.Count > 0;

    private const int MaxBuildLogLines = 10000;

    public void ClearBuildLog()
    {
        BuildLog.Clear();

        OnPropertyChanged(nameof(HasBuildLog));
    }

    public void AppendBuildLog(BuildLogLine line)
    {
        /* A long link step can run to tens of thousands of lines, and every one of them
         * is a live visual tree entry once the window is open */
        if (BuildLog.Count >= MaxBuildLogLines)
        {
            BuildLog.RemoveAt(0);
        }

        BuildLog.Add(line);

        if (BuildLog.Count == 1) OnPropertyChanged(nameof(HasBuildLog));
    }

    [JsonIgnore] public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    [JsonIgnore] public bool HasNoStatus => !HasStatus;

    [JsonIgnore] public string ProjectFolder => Path.GetDirectoryName(FilePath) ?? string.Empty;
    [JsonIgnore] public bool Exists => File.Exists(FilePath);

    /* Reinstall over an existing copy wherever it already sits, since projects are free
     * to nest plugins, and two copies under Plugins is a duplicate module to Unreal */
    [JsonIgnore] public string PluginFolder => FindDescriptor() is { } descriptor
        ? Path.GetDirectoryName(descriptor)!
        : Path.Combine(ProjectFolder, "Plugins", Globals.UnrealPluginName);

    [JsonIgnore] public string PluginDescriptor => Path.Combine(PluginFolder, $"{Globals.UnrealPluginName}.uplugin");

    /* Persisted, so an engine that is on disk but registered nowhere, which the
     * association cannot name either, only has to be pointed at once */
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineDirectory))]
    private string _engineOverride = string.Empty;

    [JsonIgnore] public string? EngineDirectory =>
        UnrealEngineInstall.BatchFile(EngineOverride, "Build.bat") is not null
            ? EngineOverride
            : UnrealEngineInstall.ResolveDirectory(EngineVersion)
              ?? UnrealEngineInstall.ResolveFromProject(ProjectFolder);

    [JsonIgnore] public string PluginVersionLabel => IsPluginInstalled ? $"v{PluginVersion}" : "Not Installed";

    [JsonIgnore] public string EngineVersionLabel => string.IsNullOrWhiteSpace(EngineVersion)
        ? "Unknown"
        : UnrealEngineInstall.IsSourceBuild(EngineVersion) ? "Source Build" : EngineVersion;

    [JsonIgnore] private Bitmap? _cachedIcon;
    [JsonIgnore] private bool _hasLoadedIcon;

    /* Null falls the view back onto the generic Unreal Engine mark */
    [JsonIgnore] public Bitmap? Icon
    {
        get
        {
            if (_hasLoadedIcon) return _cachedIcon;

            _hasLoadedIcon = true;
            _cachedIcon = LoadIcon();

            return _cachedIcon;
        }
    }

    [JsonIgnore] public bool HasIcon => Icon is not null;
    [JsonIgnore] public bool HasNoIcon => Icon is null;

    /* Re-reads the project off disk, everything displayed comes from here */
    public void Refresh()
    {
        Name = Path.GetFileNameWithoutExtension(FilePath);
        EngineVersion = ReadEngineVersion();

        var version = ReadPluginVersion();

        IsPluginInstalled = version is not null;
        PluginVersion = version ?? string.Empty;

        _hasLoadedIcon = false;
        _cachedIcon = null;

        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(HasNoIcon));
    }

    private string ReadEngineVersion()
    {
        var project = ReadJson(FilePath);

        if (project?["EngineAssociation"]?.Value<string>() is not { } association || string.IsNullOrWhiteSpace(association))
        {
            return string.Empty;
        }

        return association;
    }

    /* Version of our plugin, when it is already sitting in the project's Plugins folder */
    private string? ReadPluginVersion()
    {
        if (FindDescriptor() is not { } descriptor) return null;

        return ReadJson(descriptor)?["VersionName"]?.Value<string>() ?? string.Empty;
    }

    /* Anywhere under Plugins, since projects nest them into their own groupings */
    private string? FindDescriptor()
    {
        var pluginsFolder = Path.Combine(ProjectFolder, "Plugins");

        if (!Directory.Exists(pluginsFolder)) return null;

        try
        {
            return Directory
                .EnumerateFiles(pluginsFolder, $"{Globals.UnrealPluginName}.uplugin", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (Exception e)
        {
            Log.Warning($"Failed to search for a plugin descriptor in {pluginsFolder}: {e.Message}");

            return null;
        }
    }

    private static JObject? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JObject.Parse(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Log.Warning($"Failed to parse {path}: {e.Message}");

            return null;
        }
    }

    /* Project icons are authored far larger than the 44px they get drawn at, so they are
     * resampled once on load rather than squeezed down every frame */
    private const int IconRenderSize = 128;

    private static Bitmap Downscale(Bitmap original)
    {
        var longestEdge = Math.Max(original.PixelSize.Width, original.PixelSize.Height);

        if (longestEdge <= IconRenderSize) return original;

        var scale = (double)IconRenderSize / longestEdge;

        var scaled = original.CreateScaledBitmap(new PixelSize(
            Math.Max(1, (int)Math.Round(original.PixelSize.Width * scale)),
            Math.Max(1, (int)Math.Round(original.PixelSize.Height * scale))),
            BitmapInterpolationMode.HighQuality);

        original.Dispose();

        return scaled;
    }

    /* An override sitting beside the .uproject under the same name wins, otherwise
     * Unreal's own thumbnail, the one the launcher shows, gets picked up out of Saved */
    private Bitmap? LoadIcon()
    {
        string[] candidates =
        [
            Path.Combine(ProjectFolder, $"{Name}.png"),
            Path.Combine(ProjectFolder, "Saved", "AutoScreenshot.png")
        ];

        var iconPath = candidates.FirstOrDefault(File.Exists);

        if (iconPath is null) return null;

        try
        {
            using var stream = File.OpenRead(iconPath);

            return Downscale(new Bitmap(stream));
        }
        catch (Exception e)
        {
            Log.Warning($"Failed to load project icon {iconPath}: {e.Message}");

            return null;
        }
    }
}
