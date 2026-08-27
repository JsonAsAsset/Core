using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Threading;

using Serilog;

using Core.Framework;
using Core.Models.Enums;
using Core.Models.Plugins;

namespace Core.Services;

/* Installs the Unreal plugin into a registered project: pull the latest release,
 * drop it into the project's Plugins folder, then compile it against the engine
 * the project is associated with. Releases are source archives, so the compile
 * is what actually makes the plugin loadable */
public class UnrealPluginService : IService
{
    private static DirectoryInfo CacheFolder =>
        new(Path.Combine(InstallationFolder.ToString(), ".plugins", Globals.UnrealPluginName));

    public async Task<bool> Install(UnrealProject project)
    {
        if (!project.Exists)
        {
            Report(project, "The .uproject file could not be found on disk.");

            await SetState(project, EInstallState.Failed);

            return false;
        }

        await Dispatcher.UIThread.InvokeAsync(project.ClearBuildLog);

        await SetState(project, EInstallState.Working);

        var succeeded = false;

        try
        {
            var archive = await Download(project);

            if (archive is not null && await Extract(project, archive))
            {
                succeeded = await Compile(project);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Plugin install failed for {Project}", project.FilePath);

            Report(project, e.Message);
        }
        finally
        {
            await SetState(project, succeeded ? EInstallState.Succeeded : EInstallState.Failed);
        }

        return succeeded;
    }

    private static async Task<FileInfo?> Download(UnrealProject project)
    {
        Report(project, "Checking for the latest release...");

        var release = await RestAPI.GitHub.GetLatestRelease(Globals.UnrealPluginRepository);

        var asset = release?.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        if (release is null || asset is null)
        {
            Report(project, $"No downloadable release found on {Globals.UnrealPluginRepository}.");

            return null;
        }

        CacheFolder.Create();

        var destination = Path.Combine(CacheFolder.FullName, $"{release.Name}-{asset.Name}");

        /* Already pulled down, most likely by an install into another project */
        if (File.Exists(destination) && new FileInfo(destination).Length == asset.Size)
        {
            Report(project, $"Using cached {release.Name}...");

            return new FileInfo(destination);
        }

        Report(project, $"Downloading {release.Name}...");

        var file = await RestAPI.DownloadFileAsync(asset.DownloadUrl, destination,
            progress => Status(project, $"Downloading {release.Name}... {progress:P0}"));

        if (file is null)
        {
            Report(project, $"Failed to download {release.Name}.");
        }

        return file;
    }

    private static async Task<bool> Extract(UnrealProject project, FileInfo archive)
    {
        Report(project, "Extracting...");

        /* Pinned up front: PluginFolder re-reads the disk, and the delete below moves it */
        var target = project.PluginFolder;
        var descriptor = Path.Combine(target, $"{Globals.UnrealPluginName}.uplugin");

        /* Only ever clear a folder that is recognisably a previous install of this plugin,
         * so a name collision with something else under Plugins is never destroyed */
        if (Directory.Exists(target))
        {
            if (!IsReplaceable(target, descriptor))
            {
                Report(project, $"{target} already exists and is not a {Globals.UnrealPluginName} install.");

                return false;
            }

            /* Checked before anything is removed: a recursive delete that trips over a
             * locked binary halfway through leaves the existing install destroyed */
            if (FindLockedFile(target) is { } locked)
            {
                Report(project, $"{Path.GetFileName(locked)} is in use. Close the Unreal Editor and try again.");

                return false;
            }

            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(target);

        using (var zip = ZipFile.OpenRead(archive.FullName))
        {
            /* GitHub source archives nest everything under one commit-stamped folder */
            var roots = zip.Entries
                .Select(entry => entry.FullName.Split('/')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var prefix = roots.Length == 1 ? $"{roots[0]}/" : string.Empty;
            var targetRoot = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;

                var relative = entry.FullName;

                if (prefix.Length > 0 && relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    relative = relative[prefix.Length..];
                }

                if (relative.Length == 0) continue;

                var destination = Path.GetFullPath(Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar)));

                /* An entry whose path climbs back out of the plugin folder is not extracted */
                if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                entry.ExtractToFile(destination, overwrite: true);
            }
        }

        if (!File.Exists(descriptor))
        {
            Report(project, $"The release did not contain {Globals.UnrealPluginName}.uplugin.");

            return false;
        }

        await Dispatcher.UIThread.InvokeAsync(project.Refresh);

        return true;
    }

    private static async Task<bool> Compile(UnrealProject project)
    {
        if (UnrealEngineInstall.BatchFile(project.EngineDirectory, "Build.bat") is not { } build)
        {
            Report(project, UnrealEngineInstall.IsSourceBuild(project.EngineVersion)
                ? $"No engine registered for source build {project.EngineVersion}. Open the project once through Unreal to register it."
                : $"Unreal Engine {project.EngineVersion} is not installed on this machine.");

            return false;
        }

        if (FindEditorTarget(project) is not { } target)
        {
            Report(project, "The project has no C++ editor target, so the plugin cannot be compiled into it.");

            return false;
        }

        Report(project, $"Compiling {target}...");

        var arguments = $"{target} Win64 Development -Project=\"{project.FilePath}\" -WaitMutex";

        Append(project, BuildLogLine.Notice($"{build} {arguments}"));

        var exitCode = await Run(build, arguments, project.ProjectFolder, line =>
        {
            Log.Information("[{Plugin}] {Line}", Globals.UnrealPluginName, line);

            Append(project, BuildLogLine.Parse(line));

            /* UBT is chatty. Its per-file progress lines start with a [n/total] counter,
             * which is the part worth putting in front of the user */
            if (line.StartsWith('[') || line.StartsWith("Building", StringComparison.Ordinal))
            {
                Report(project, line.Trim());
            }
        });

        await Dispatcher.UIThread.InvokeAsync(project.Refresh);

        Report(project, exitCode == 0
            ? $"Installed {Globals.UnrealPluginName} v{project.PluginVersion}."
            : $"Compile failed with exit code {exitCode}, see the log for details.");

        return exitCode == 0;
    }

    /* A descriptor means a previous install of ours. Nothing but build output means a
     * previous install of ours whose replacement was interrupted, which is recoverable.
     * Anything else living under this name belongs to someone else and is left alone */
    private static bool IsReplaceable(string folder, string descriptor)
    {
        if (File.Exists(descriptor)) return true;

        string[] generated = ["Binaries", "Intermediate"];

        return Directory.EnumerateFileSystemEntries(folder)
            .All(entry => generated.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase));
    }

    /* Unreal holds the plugin's binaries open for as long as the editor is running */
    private static string? FindLockedFile(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return file;
            }
            catch (UnauthorizedAccessException)
            {
                return file;
            }
        }

        return null;
    }

    /* The editor target is the one the plugin's editor modules get compiled into */
    private static string? FindEditorTarget(UnrealProject project)
    {
        var source = Path.Combine(project.ProjectFolder, "Source");

        if (!Directory.Exists(source)) return null;

        const string suffix = ".Target.cs";

        foreach (var file in Directory.EnumerateFiles(source, $"*{suffix}"))
        {
            if (!File.ReadAllText(file).Contains("TargetType.Editor", StringComparison.Ordinal)) continue;

            return Path.GetFileName(file)[..^suffix.Length];
        }

        return null;
    }

    private static async Task<int> Run(string fileName, string arguments, string workingDirectory, Action<string> output)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) output(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) output(e.Data);
        };

        process.Start();

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        return process.ExitCode;
    }

    private static async Task SetState(UnrealProject project, EInstallState state)
        => await Dispatcher.UIThread.InvokeAsync(() => project.State = state);

    /* Status feeds a binding, and both the download and the compile report from
     * threads that are not the UI one */
    private static void Report(UnrealProject project, string status)
    {
        Log.Information("{Project}: {Status}", project.Name, status);

        Dispatcher.UIThread.Post(() =>
        {
            project.Status = status;
            project.AppendBuildLog(BuildLogLine.Notice(status));
        });
    }

    /* Download progress ticks many times a second, so it moves the row's status
     * without leaving a line behind in the log */
    private static void Status(UnrealProject project, string status)
        => Dispatcher.UIThread.Post(() => project.Status = status);

    private static void Append(UnrealProject project, BuildLogLine line)
        => Dispatcher.UIThread.Post(() => project.AppendBuildLog(line));
}
