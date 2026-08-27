global using static Core.Services.Framework.AppServices;
global using static Core.Resources.Globals;

using Avalonia.Platform.Storage;

namespace Core;

public static class Globals
{
    public const bool IsReadyToMeshExport = false;

    public static readonly FilePickerFileType MappingsFileType = new(".USMAP Files") { Patterns = [ "*.usmap" ] };
    public static readonly FilePickerFileType UnrealProjectFileType = new(".UPROJECT Files") { Patterns = [ "*.uproject" ] };

    /* Name of the plugin as it sits inside an Unreal project's Plugins folder */
    public const string UnrealPluginName = "Reflection";

    /* Releases here are GitHub source archives, so they are compiled after being extracted */
    public const string UnrealPluginRepository = $"JsonAsAsset/{UnrealPluginName}";

    public const bool ShowVersion = true;
    public const bool RedactProfiles = false;
    
    public static string LaunchProfileArg = string.Empty;
}
