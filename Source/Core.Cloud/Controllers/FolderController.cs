using CUE4Parse.Utils;

using Microsoft.AspNetCore.Mvc;

using Core.Resources.Framework.Base;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: Folder                                                                                                    */
/*                                                                                                                                  */
/* Lists every asset path under a folder of the mounted game files. Used by Reflection to reflect a whole folder without anything   */
/* in the project pointing at it first.                                                                                             */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    /* Every asset path under a folder, in the same form the export endpoint takes back */
    [HttpGet("folder/paths")]
    public ActionResult GetFolderPaths(string? path)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new
        {
            errorCode = "cloud.folder.no_path",
            errorMessage = "No folder supplied",
            numericErrorCode = 1003
        });

        var folder = ToGameFolderPath(path, MainProfile.Provider);
        if (folder is null) return NotFoundResponse;

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectFolderPaths(MainProfile.Provider, folder, paths, seen);

        /* A folder can be split across profiles, the same way a single path can be */
        foreach (var profile in SecondaryBaseProfiles)
        {
            if (!profile.IsInitialized) continue;

            CollectFolderPaths(profile.Provider, ToGameFolderPath(path, profile.Provider) ?? folder, paths, seen);
        }

        if (paths.Count == 0) return NotFoundResponse;

        paths.Sort(StringComparer.OrdinalIgnoreCase);

        return new JsonResult(new
        {
            path = folder,
            paths
        });
    }

    /* Everything mounted below Folder, subfolders included */
    private static void CollectFolderPaths(BaseProvider provider, string folder, List<string> paths, HashSet<string> seen)
    {
        var prefix = folder + "/";

        foreach (var file in provider.Files.Values)
        {
            if (file is null || !file.IsUePackage) continue;

            var packagePath = file.PathWithoutExtension;

            /* Editor-only side files ("Foo.o.uasset") are not assets of their own */
            if (packagePath.EndsWith(".o", StringComparison.OrdinalIgnoreCase)) continue;
            if (!packagePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            /* One package is several mounted files, so the path is what gets deduplicated */
            if (seen.Add(packagePath)) paths.Add(packagePath);
        }
    }

    /* Takes the folder however it was typed, editor form or the way the mounted files spell it,
     * and hands back the latter. Null when it names a root nothing is mounted under. */
    private static string? ToGameFolderPath(string rawFolder, BaseProvider provider)
    {
        var folder = rawFolder.Trim().Replace('\\', '/').Trim('/');
        if (folder.Length == 0) return null;

        var root = folder.SubstringBefore('/');
        var remainder = folder.Length > root.Length ? folder[(root.Length + 1)..] : string.Empty;

        /* Already a path into the mounted files, plugin folders and all */
        if (root.Equals(provider.ProjectName, StringComparison.OrdinalIgnoreCase)) return folder;

        if (root.Equals("Engine", StringComparison.OrdinalIgnoreCase))
        {
            return remainder.StartsWith("Content", StringComparison.OrdinalIgnoreCase)
                ? folder
                : CombineFolder("Engine/Content", remainder);
        }

        /* The editor's own root for the project's content */
        if (root.Equals("Game", StringComparison.OrdinalIgnoreCase))
        {
            return CombineFolder(provider.ProjectName + "/Content", remainder);
        }

        /* Anything else is a mount named after the plugin it came from */
        if (provider.VirtualPaths.TryGetValue(root, out var virtualPath))
        {
            return CombineFolder(virtualPath.Replace('\\', '/').Trim('/') + "/Content", remainder);
        }

        return null;
    }

    private static string CombineFolder(string root, string remainder)
    {
        return remainder.Length == 0 ? root : root + "/" + remainder;
    }
}
