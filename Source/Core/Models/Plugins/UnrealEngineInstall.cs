using System;
using System.IO;

using Microsoft.Win32;

namespace Core.Models.Plugins;

/* Turns a project's EngineAssociation into the engine on disk, the same two places
 * UnrealVersionSelector looks: launcher installs under EpicGames, source builds
 * under the per-user Builds key keyed by a generated identifier */
public static class UnrealEngineInstall
{
    public static bool IsSourceBuild(string? association)
        => !string.IsNullOrWhiteSpace(association) && Guid.TryParse(association.Trim('{', '}'), out _);

    public static string? ResolveDirectory(string? association)
    {
        if (string.IsNullOrWhiteSpace(association)) return null;

        var directory = IsSourceBuild(association)
            ? ResolveSourceBuild(association)
            : ResolveLauncherBuild(association);

        return Directory.Exists(directory) ? directory : null;
    }

    /* A source build that was never registered can still be reached when the project
     * sits inside the engine tree, which is how UE lays its own projects out */
    public static string? ResolveFromProject(string? projectFolder)
    {
        if (string.IsNullOrWhiteSpace(projectFolder)) return null;

        var directory = new DirectoryInfo(projectFolder);

        while (directory is not null)
        {
            if (BatchFile(directory.FullName, "Build.bat") is not null) return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    public static string? BatchFile(string? engineDirectory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(engineDirectory)) return null;

        var path = Path.Combine(engineDirectory, "Engine", "Build", "BatchFiles", fileName);

        return File.Exists(path) ? path : null;
    }

    /* The Builds key stores identifiers braced, the .uproject may or may not */
    private static string? ResolveSourceBuild(string association)
    {
        using var builds = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Builds");

        if (builds is null) return null;

        var bare = association.Trim('{', '}');

        foreach (var name in builds.GetValueNames())
        {
            if (!name.Trim('{', '}').Equals(bare, StringComparison.OrdinalIgnoreCase)) continue;

            return Normalize(builds.GetValue(name) as string);
        }

        return null;
    }

    private static string? ResolveLauncherBuild(string association)
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey($@"SOFTWARE\EpicGames\Unreal Engine\{association}");

            if (key?.GetValue("InstalledDirectory") is string directory) return Normalize(directory);
        }

        return null;
    }

    private static string? Normalize(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
}
