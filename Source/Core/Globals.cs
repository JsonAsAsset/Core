global using static Core.Services.Framework.AppServices;
global using static Core.Resources.Globals;

using Avalonia.Platform.Storage;

namespace Core;

public static class Globals
{
    public const bool IsReadyToMeshExport = false;

    public static readonly FilePickerFileType MappingsFileType = new(".USMAP Files") { Patterns = [ "*.usmap" ] };

    public const bool ShowVersion = true;
    public const bool RedactProfiles = false;
    
    public static string LaunchProfileArg = string.Empty;
}
