using System;
using System.IO;

using CommunityToolkit.Mvvm.ComponentModel;

using Newtonsoft.Json;

using Serilog;

using Core.Framework;
using Core.ViewModels.Settings;

namespace Core.Services;

public partial class SettingsService : ObservableObject, IService
{
    [ObservableProperty] private ApplicationSettingsViewModel _application = new();
    [ObservableProperty] private UpdatesSettingsViewModel _updates = new();
    [ObservableProperty] private ConnectionsSettingsViewModel _connections = new();
    [ObservableProperty] private CloudSettingsViewModel _cloud = new();
    [ObservableProperty] private ModelSettingsViewModel _model = new();
    [ObservableProperty] private PluginSettingsViewModel _plugin = new();

    [ObservableProperty] private DebugSettingsViewModel _debug = new();
    
    private static readonly DirectoryInfo DirectoryPath = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        CODENAME));
    
#if DEBUG
    private static readonly FileInfo FilePath = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        CODENAME,
        "Settings.Debug.json"));
#else
    private static readonly FileInfo FilePath = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        CODENAME,
        "Settings.json"));
#endif

    public SettingsService()
    {
        DirectoryPath.Create();
    }

    public void Load()
    {
        if (!FilePath.Exists) return;
        
        try
        {
            var settings = JsonConvert.DeserializeObject<SettingsService>(File.ReadAllText(FilePath.FullName));
            if (settings is null) return;

            foreach (var property in settings.GetType().GetProperties())
            {
                if (!property.CanWrite) continue;
                
                var value = property.GetValue(settings);
                property.SetValue(this, value);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load settings: {e}");
        }
    }
    
    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath.FullName, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save settings: {e}");
        }
    }
}
