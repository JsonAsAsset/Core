using System;
using System.IO;

using CUE4Parse.UE4.Objects.Core.Misc;

namespace Core.Resources;

public static class Globals
{
    /* Format: 0.0.0.0 */
    public const string VERSION = "0.0.5.2";
    public const string COMMIT = "";
    
    public static bool IS_COMMIT_AVAILABLE => !string.IsNullOrEmpty(COMMIT);
    public static bool IS_COMMIT_UNAVAILABLE => !IS_COMMIT_AVAILABLE;

    /* Application Metadata */
    public const string CODENAME = "Core";
    
    public const string APP_NAME = "Core";
    public const string INSTANCE_NAME = $"{APP_NAME}.SingleInstance";

    /* GitHub Metadata */
    private const string AUTHOR_NAME = "JsonAsAsset";
    public const string GITHUB_REPO_NAME = APP_NAME;
    private const string AUTHOR_AND_GITHUB = $"{AUTHOR_NAME}/{GITHUB_REPO_NAME}";
    
    public const string GITHUB_API_LINK = $"https://api.github.com/repos/{AUTHOR_AND_GITHUB}";
    
    public const string GITHUB_LINK = $"https://github.com/{AUTHOR_AND_GITHUB}";
    public const string GITHUB_COMMIT_LINK = $"{GITHUB_LINK}/commit";
    
    /* Discord */
    public const string DISCORD_LINK = "https://discord.gg/eV9DF6sBsz";
    public const string DISCORD_ACTIVITY_ID = "1386505366061453533";
    
    /* General Links */
    public const string X_LINK = "https://x.com/Tectory";
    public const string DONATE_LINK = "https://ko-fi.com/Tectory";
    
    /* Other Constants */
    public static readonly FGuid ZERO_GUID = new();
    public const string EMPTY_CHAR = "0x0000000000000000000000000000000000000000000000000000000000000000";
    public const bool IS_DEBUG =
#if DEBUG
        true;
#else
        false;
#endif
    
    /* Application Folders */
    public static readonly string ApplicationDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string DataFolder = Path.Combine(ApplicationDataFolder, CODENAME);
    
    public static readonly DirectoryInfo ProfilesFolder = new(Path.Combine(DataFolder, ".profiles"));
    public static readonly DirectoryInfo InstallationFolder = new(Path.Combine(DataFolder, ".installations"));
    public static readonly DirectoryInfo RuntimeFolder = new(Path.Combine(DataFolder, ".runtime"));
    public static readonly DirectoryInfo ProfileInstallationFolder = new(Path.Combine(DataFolder, ".installation"));
    public static readonly DirectoryInfo OnDemandFolder = new(Path.Combine(RuntimeFolder.ToString(), ".on.demand"));
    public static readonly DirectoryInfo OnDemandChunksFolder = new(Path.Combine(OnDemandFolder.ToString(), ".chunks"));
    public static readonly DirectoryInfo OnDemandManifestFolder = new(Path.Combine(OnDemandFolder.ToString(), ".manifests"));
    public static readonly DirectoryInfo MappingsFolder = new(Path.Combine(ProfileInstallationFolder.ToString(), ".mappings"));
    public static readonly DirectoryInfo UEDBMappingsFolder = new(Path.Combine(MappingsFolder.ToString(), ".uedb"));
    public static readonly DirectoryInfo LogsFolder = new(Path.Combine(DataFolder, ".logs"));
    private static readonly DirectoryInfo ExportFolder = new(Path.Combine(RuntimeFolder.ToString(), ".export"));
    public static readonly DirectoryInfo AudioFilesFolder = new(Path.Combine(ExportFolder.ToString(), ".audio"));
    
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ProfilesFolder.FullName);
        Directory.CreateDirectory(InstallationFolder.FullName);
        Directory.CreateDirectory(RuntimeFolder.FullName);
        Directory.CreateDirectory(ProfileInstallationFolder.FullName);
        Directory.CreateDirectory(OnDemandFolder.FullName);
        Directory.CreateDirectory(OnDemandChunksFolder.FullName);
        Directory.CreateDirectory(OnDemandManifestFolder.FullName);
        Directory.CreateDirectory(MappingsFolder.FullName);
        Directory.CreateDirectory(UEDBMappingsFolder.FullName);
        Directory.CreateDirectory(LogsFolder.FullName);
        Directory.CreateDirectory(ExportFolder.FullName);
        Directory.CreateDirectory(AudioFilesFolder.FullName);
    }
}
