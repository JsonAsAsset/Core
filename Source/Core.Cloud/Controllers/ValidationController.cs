using CUE4Parse.Utils;

using Microsoft.AspNetCore.Mvc;

using Core.Resources.Framework.Base;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: Validation */
/*                                                                                                                                  */
/* Resolves batches of Unreal package paths against the mounted game files, and for the ones that don't exist, suggests where a file */
/* of the same name actually lives. Used by Reflection's Validation tool to find assets sitting in the wrong folder.               */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

/* Request body of "/api/validate" */
public sealed class ValidationRequest
{
    /* Unreal package paths */
    public string[]? Paths { get; set; }

    /* Look up replacement locations for paths that don't exist */
    public bool Suggest { get; set; } = true;

    /* Upper limit of suggestions returned per path */
    public int MaxSuggestions { get; set; } = 4;
}

public partial class CloudApiController
{
    /* Asset name (lowercase, no extension) -> every package path holding that name */
    private static Dictionary<string, List<string>>? ValidationNameIndex;
    private static readonly object ValidationNameIndexLock = new();

    /* The name index is built off the mounted files, so any profile change throws it away */
    private static void InvalidateValidationIndex()
    {
        lock (ValidationNameIndexLock)
        {
            ValidationNameIndex = null;
        }
    }

    /* Validates a batch of package paths against the real game files */
    [HttpPost("validate")]
    public ActionResult PostValidate([FromBody] ValidationRequest? request)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        var paths = request?.Paths;
        if (paths is null) return BadRequest(new
        {
            errorCode = "cloud.validation.no_paths",
            errorMessage = "No paths supplied",
            numericErrorCode = 1002
        });

        var suggest = request!.Suggest;
        var maxSuggestions = Math.Clamp(request.MaxSuggestions, 0, 32);
        var index = suggest && maxSuggestions > 0 ? GetValidationNameIndex() : null;

        var results = new List<object>(paths.Length);

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            var packagePath = path.SubstringBefore('.');
            var resolved = ResolveValidationPath(packagePath, out var exists);

            string[] matches = [];

            if (!exists && index is not null)
            {
                matches = FindValidationMatches(index, packagePath, maxSuggestions);
            }

            results.Add(new
            {
                path = packagePath,
                resolved,
                exists,
                matches
            });
        }

        return new JsonResult(new
        {
            project_name = MainProfile.Provider.ProjectName,
            results
        });
    }

    /* Runs a path through every mounted profile, reporting where it landed and whether anything is actually there */
    private static string ResolveValidationPath(string packagePath, out bool exists)
    {
        var resolved = SafeFixPath(MainProfile!.Provider, packagePath) ?? packagePath;

        exists = MainProfile.Provider.TryGetGameFile(packagePath, out _);
        if (exists) return resolved;

        foreach (var profile in SecondaryBaseProfiles)
        {
            if (!profile.IsInitialized) continue;
            if (!profile.Provider.TryGetGameFile(packagePath, out _)) continue;

            exists = true;

            return SafeFixPath(profile.Provider, packagePath) ?? resolved;
        }

        return resolved;
    }

    /* FixPath indexes into the string directly, so anything malformed has to be caught here */
    private static string? SafeFixPath(BaseProvider provider, string packagePath)
    {
        try
        {
            return provider.FixPath(packagePath);
        }
        catch
        {
            return null;
        }
    }

    /* Every package path that shares the validated path's asset name, minus the path itself */
    private static string[] FindValidationMatches(Dictionary<string, List<string>> index, string packagePath, int maxSuggestions)
    {
        var assetName = packagePath.SubstringAfterLast('/');
        if (assetName.Length == 0 || !index.TryGetValue(assetName.ToLowerInvariant(), out var candidates))
        {
            return [];
        }

        var matches = new List<string>(Math.Min(maxSuggestions, candidates.Count));

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate, packagePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (matches.Contains(candidate, StringComparer.OrdinalIgnoreCase)) continue;

            matches.Add(candidate);

            if (matches.Count >= maxSuggestions) break;
        }

        return matches.ToArray();
    }

    /* Builds (once) a lookup of asset name -> package paths across every mounted profile */
    private static Dictionary<string, List<string>> GetValidationNameIndex()
    {
        lock (ValidationNameIndexLock)
        {
            if (ValidationNameIndex is not null) return ValidationNameIndex;

            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            IndexProviderFiles(index, MainProfile!.Provider);

            foreach (var profile in SecondaryBaseProfiles)
            {
                if (!profile.IsInitialized) continue;

                IndexProviderFiles(index, profile.Provider);
            }

            return ValidationNameIndex = index;
        }
    }

    private static void IndexProviderFiles(Dictionary<string, List<string>> index, BaseProvider provider)
    {
        var projectName = provider.ProjectName;

        foreach (var file in provider.Files.Values)
        {
            if (file is null || !file.IsUePackage) continue;

            var name = file.NameWithoutExtension;

            /* Editor-only side files ("Foo.o.uasset") are not assets of their own */
            if (name.EndsWith(".o", StringComparison.OrdinalIgnoreCase)) continue;

            var packagePath = ToPackagePath(file.PathWithoutExtension, projectName);
            if (packagePath is null) continue;

            var key = name.ToLowerInvariant();

            if (!index.TryGetValue(key, out var paths))
            {
                index[key] = paths = [];
            }

            paths.Add(packagePath);
        }
    }

    /* The inverse of the provider's FixPath: turns a path into the mounted game files back
     * into an Unreal package path */
    private static string? ToPackagePath(string gamePath, string projectName)
    {
        var root = gamePath.SubstringBefore('/');

        if (root.Equals("Engine", StringComparison.OrdinalIgnoreCase))
        {
            var tree = gamePath.SubstringAfter('/');

            return tree.StartsWith("Content/", StringComparison.OrdinalIgnoreCase)
                ? "/Engine/" + tree["Content/".Length..]
                : null;
        }

        if (!root.Equals(projectName, StringComparison.OrdinalIgnoreCase)) return null;

        var remainder = gamePath.SubstringAfter('/');

        /* The project's own content is what /Game maps onto */
        if (remainder.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
        {
            return "/Game/" + remainder["Content/".Length..];
        }

        /* Plugin content mounts under a root named after the plugin */
        var contentIndex = remainder.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (contentIndex < 0) return null;

        var mountName = remainder[..contentIndex].SubstringAfterLast('/');
        if (mountName.Length == 0) return null;

        return "/" + mountName + "/" + remainder[(contentIndex + "/Content/".Length)..];
    }
}
