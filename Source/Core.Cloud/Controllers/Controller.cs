using CUE4Parse.GameTypes.FN.Assets.Exports.DataAssets;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Textures;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine.VectorField;
using CUE4Parse.UE4.Objects.Meshes;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.Utils;

using CUE4Parse.MappingsProvider;
using Microsoft.AspNetCore.Mvc;

using Newtonsoft.Json;

using Serilog;
using Core.Resources;
using Core.Resources.Convertors;
using Core.Resources.Framework.Base;
using Core.Resources.Utilities;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

[Route("api")]
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public partial class CloudApiController : ControllerBase
{
    /* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
    private static IEnumerable<BaseProfile> SecondaryBaseProfiles = [];
    private static BaseProfile? MainProfile;
    private static bool IsBaseProfileReady => MainProfile != null && MainProfile!.Provider.Files.Count > 0 && MainProfile.IsInitialized;
    /* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

    /* Responses */
    private static readonly JsonResult NotInitializedResponse =
        new(new
        {
            errorCode = "cloud.common.not_initialized",
            errorMessage = "Not initialized yet",
            numericErrorCode = 1000
        })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable
        };
    
    private static readonly JsonResult NotFoundResponse =
        new(new
        {
            errorCode = "cloud.common.not_found",
            errorMessage = "Not found",
            numericErrorCode = 1001
        })
        {
            StatusCode = StatusCodes.Status404NotFound
        };
    
    public static void SetProfile(BaseProfile profile)
    {
        if (profile is not null)
        {
            Log.Information($"[Core.Cloud]: MainProfile got updated to {profile.Name}");
        }

        MainProfile = profile;
        InvalidateValidationIndex();
    }

    public static void SetSecondaryProfile(BaseProfile profile)
    {
        InvalidateValidationIndex();

        if (profile == null)
        {
            SecondaryBaseProfiles = [];
            return;
        }

        var list = SecondaryBaseProfiles.ToList();

        if (list.Count > 0)
        {
            list[0] = profile;
        }
        else
        {
            list.Add(profile);
        }

        SecondaryBaseProfiles = list;
    }
    
    /* Metadata request to retrieve information about this process */
    [HttpGet("metadata")]
    public ActionResult Get()
    {
        if (!IsBaseProfileReady) return NotInitializedResponse;
        
        var enumName = Enum.GetName(typeof(EGame), MainProfile?.Version!);
        var underscore = enumName!.LastIndexOf('_');
        var minor_version = int.Parse(enumName[(underscore + 1)..]);

        return new JsonResult(new
        {
            name = MainProfile?.Provider.ProjectName,
            major_version = MainProfile?.Version >= EGame.GAME_UE5_0 ? 5 : 4,
            minor_version,
            /* Which mapping an older head's curves are written in terms of, said once here so
             * whoever needs it does not have to be told and cannot be told a different one */
            curve_mapping = DefaultCurveMapping,
            profile = MainProfile
        });
    }
    
    [HttpGet("status")]
    public ActionResult GetStatus()
    {
        if (!IsBaseProfileReady) return NotInitializedResponse;

        return new JsonResult(new
        {
            status = "Initialized"
        });
    }
    
    /* Request to retrieve all HLOD paths */
    [HttpGet("hlod/paths")]
    public ActionResult GetHLODPaths()
    {
        if (!IsBaseProfileReady) return NotInitializedResponse;
        
        List<string> paths = [];

        var provider = MainProfile!.Provider;

        /* Taken from the loaded profile rather than hardcoded, so this isn't tied to one title */
        var contentRoot = provider.ProjectName + "/Content";

        paths.AddRange(
            provider.Files.Values
                .Select(a => a?.PathWithoutExtension)
                .Where(p =>
                    p is not null &&
                    p.Contains("/HLOD/") &&
                    p.Contains("/Maps/") &&
                    p.Contains(contentRoot) &&
                    !p.EndsWith(".o"))
                .Distinct()!
        );

        return new JsonResult(new
        {
            paths
        });
    }
    
    /* Request to retrieve all HLOD paths */
    [HttpGet("plugin")]
    public ActionResult GetPlugin(string name)
    {
        if (!IsBaseProfileReady || MainProfile == null) return NotInitializedResponse;
        
        MainProfile.Provider.VirtualPaths.TryGetValue(name, out var path);

        return GetRawExport(path + "/" + name + ".uplugin");
    }

    public ActionResult GetRawExport(string path)
    {
        MainProfile!.Provider.TryGetGameFile(path, out var gameFile);
        if (gameFile == null) return NotFoundResponse;

        var data = MainProfile.Provider.SaveAsset(gameFile);
        using var stream = new MemoryStream(data);
        stream.Position = 0;
        using var reader = new StreamReader(stream);

        return new ContentResult
        {
            Content = reader.ReadToEnd(),
            ContentType = "application/json",
            StatusCode = 200
        };
    }
    
    /* Normal Export */
    [HttpGet("export")]
    public ActionResult GetExport(bool raw, string? path, string? export_name, string? export_type, bool? metadata, bool? save)
    {
        if (!IsBaseProfileReady || path is null) return NotInitializedResponse;

        if (path.EndsWith("uplugin") && MainProfile != null)
        {
            return GetRawExport(path);
        }
        
        var contentType = Request.Headers.ContentType;
        path = path.SubstringBefore('.');
        
        /* Find the profile that'll have this asset */
        var profile = FindBaseProfileForPath(path, found: out var found);
        if (!found) return NotFoundResponse;
        
        var provider = profile.Provider;
        provider.TryLoadPackageObject(path, export: out var localObject);
        provider.TryLoadPackage(path, out var package);

        /* One path can be mounted from several containers, and the one that wins is simply whichever
         * mounted last. Editor-side containers (UEFN ships one) carry the uncooked package: it reads
         * back fine, it just has no platform data, so the texture comes out 0x0 with no pixels.
         * Nothing below names a container, it only asks which copy actually cooked its pixels, so a
         * texture that only exists in that container is still served from it. */
        if (localObject is UTexture uncooked && !HasTextureData(uncooked) &&
            FindCookedTexture(provider, path, uncooked.Name) is { } cooked)
        {
            package = cooked.Package;
            localObject = cooked.Texture;
        }

        if (package is not null)
        {
            localObject ??= package.ExportsLazy[0].Value;

            if (!string.IsNullOrEmpty(export_name))
            {
                foreach (var export in package.ExportsLazy)
                {
                    var uObject = export.Value;
                    if (uObject is null || uObject.Name != export_name) continue;
                    
                    localObject = uObject;
                    
                    if (raw)
                    {
                        return new JsonResult(new
                        {
                            exports = (object[])[
                                localObject
                            ]
                        });
                    }
                }
            }

            if (!string.IsNullOrEmpty(export_type))
            {
                var exports = new List<UObject>();
                
                foreach (var export in package.ExportsLazy)
                {
                    var uObject = export.Value;
                    if (uObject is null || uObject.ExportType != export_type) continue;
                    
                    localObject = uObject;
                    exports.Add(uObject);
                }

                if (raw)
                {
                    return new JsonResult(new
                    {
                        exports
                    });
                }
            }
        }

        if (save is true)
        {
            switch (localObject)
            {
                case USoundWave wave:
                {
                    /* Raw hands back what the game cooked, decompressed unwraps the container the
                     * codec is packed in. Neither one decides what codec comes out. */
                    var shouldDecompress = MainProfile?.AudioFormat != EAudioFormatType.Raw;

                    wave.Decode(shouldDecompress, out var audioFormat, out var data);

                    if (data != null)
                    {
                        var ownerName = wave.Owner!.Name;
                        ownerName = ownerName.SubstringBeforeWithLast('/').TrimEnd('/');

                        var cleanOwner = ownerName.TrimStart('/').Replace("/", "\\");
                        var savePath = Path.Combine(Globals.AudioFilesFolder.FullName, cleanOwner);

                        /* Named for the codec it actually is: newer titles cook RAD Audio, and
                         * writing that as .ogg is how it ends up unreadable at the other end */
                        var extension = AudioUtilities.ExtensionFor(audioFormat);
                        var finalPath = Path.Combine(savePath, $"{Path.GetFileName(path)}.{extension}");

                        Directory.CreateDirectory(savePath);
                        System.IO.File.WriteAllBytes(finalPath, data);

                        /* Anything else only travels as far as a decoder for it is installed */
                        if (!AudioUtilities.IsReadable(audioFormat) && AudioUtilities.TryConvertToWav(finalPath, out var wavPath))
                        {
                            finalPath = wavPath;
                            extension = "wav";
                        }

                        Log.Information($"[Core.Cloud]: Saved {audioFormat} audio as {finalPath}");

                        return new JsonResult(new
                        {
                            file = finalPath,
                            format = extension
                        });
                    }

                    break;
                }
            }
        }

        /* Return a raw export */
        if (raw) return HandleRawExport(path, provider, package);
        if (metadata is true) return HandleExportMetadata(path, provider, package);

        /* Switch on Class Type */
        return localObject switch
        {
            /* Only intercepted when the caller asks for bytes, a json export of a vector field is
             * still just its properties */
            UVectorFieldStatic vectorField when contentType == "application/octet-stream" => ProcessVectorField(vectorField),
            UTexture texture => ProcessTexture(texture, contentType!),
            USoundWave wave => ProcessSoundWave(wave),
            _ => HandleRawExport(path, provider, package)
        };
    }

    /* Whether this copy of a texture was cooked with its pixels attached. Streaming virtual textures
     * keep theirs in the chunk table rather than in mips, everything else keeps at least one mip.
     * An uncooked package has neither, only the editor's source description of the image. */
    private static bool HasTextureData(UTexture texture)
    {
        var platformData = texture.PlatformData;

        if (platformData is { FirstMipToSerialize: >= 0, VTData: { } virtualTexture } && virtualTexture.IsInitialized())
        {
            return true;
        }

        return platformData.Mips.Length > 0;
    }

    /* The other containers mounting this same path, in mount order, until one of them has a cooked
     * copy of the export. Null when this path is uncooked everywhere it is mounted, which leaves the
     * caller with what it already had. */
    private static (IPackage Package, UTexture Texture)? FindCookedTexture(BaseProvider provider, string path, string exportName)
    {
        if (!provider.TryGetGameFile(path, out var mounted)) return null;

        /* Keyed off the file that was actually picked, so this asks for the duplicates of that one
         * rather than re-running path resolution and possibly landing somewhere else */
        if (!provider.Files.TryGetValues(mounted.Path, out var candidates) || candidates.Count < 2) return null;

        foreach (var candidate in candidates)
        {
            if (ReferenceEquals(candidate, mounted)) continue;
            if (!provider.TryLoadPackage(candidate, out var candidatePackage)) continue;

            var export = candidatePackage.GetExportOrNull(exportName, StringComparison.OrdinalIgnoreCase);
            if (export is not UTexture texture || !HasTextureData(texture)) continue;

            var container = candidate is VfsEntry { Vfs: { } vfs } ? vfs.Name : "disk";
            Log.Information($"[Core.Cloud]: {path} is uncooked in the container it mounted from, using the cooked copy in {container}");

            return (candidatePackage, texture);
        }

        return null;
    }

    /* Return a texture as a file / encoding */
    private ActionResult ProcessTexture(UTexture texture, string contentType)
    {
        if (texture.GetFirstMip()?.BulkData!.Data is { } mipData && contentType == "application/octet-stream")
        {
            return File(mipData, contentType);
        }

        var textureData = texture.Decode();
        if (textureData is null)
        {
            /* Named rather than written out.
             *
             * A texture handed to the serializer takes its owner with it, and its owner is the
             * package, and that is the provider the whole game is mounted in. What comes back is
             * then not a message about one texture but a walk of everything reachable from it, and
             * it does not end: sixteen gigabytes of it were enough to kill the caller outright.
             *
             * Which texture it was and that it could not be decoded is all there is to say. */
            return StatusCode(500, new
            {
                errored = true,
                exceptionstring = "Invalid texture data",
                texture = texture.Name,
                path = texture.Owner?.Name
            });
        }

        return File(textureData.Encode(ETextureFormat.Png, false, out _), "image/png");
    }

    /* Return the source volume of a vector field, one FFloat16Color per voxel */
    private ActionResult ProcessVectorField(UVectorFieldStatic vectorField)
    {
        var volumeData = vectorField.SourceData.Data;

        if (volumeData is null || volumeData.Length == 0)
        {
            return Conflict(new
            {
                errored = true,
                exceptionstring = "No source data on the vector field, returned raw export as json",
                exports = new[] { vectorField }
            });
        }

        return File(volumeData, "application/octet-stream");
    }

    /* Return a sound wave file format */
    private ActionResult ProcessSoundWave(USoundWave wave)
    {
        var shouldDecompress = MainProfile?.AudioFormat != EAudioFormatType.Raw;

        wave.Decode(shouldDecompress, out var audioFormat, out var data);

        if (data is null || string.IsNullOrEmpty(audioFormat))
        {
            return Conflict(new
            {
                errored = true,
                exceptionstring = "Invalid audio data, returned raw export as json",
                exports = new[] { wave }
            });
        }

        return File(data, AudioUtilities.MimeTypeFor(audioFormat));
    }

    /* Handle raw exports */
    /* Public on a controller means MVC treats it as an action and tries to bind its arguments, and
     * more than one of those binds from the body, which is refused when the routes are built */
    /* Types a cook empties out, so the companion's copy of the same object is the one to serve.
     * Everything else keeps what the cook wrote. */
    private static readonly string[] EditorCopyReplacesCooked = ["NiagaraValidationRuleSet", "NiagaraScript"];

    [NonAction]
    public ActionResult HandleRawExport(string path, BaseProvider provider, IPackage? package = null)
    {
        try
        {
            var objectPath = $"{path.SubstringBefore('.')}.o.uasset";

            /* Loading by path again would undo any choice the caller already made between containers
             * mounting this path, so its package is used when there is one */
            var pkg = package ?? provider.LoadPackage(path);
            var exports = pkg.GetExports().ToArray();
            var finalExports = new List<UObject>(exports);

            var mergedExports = new List<UObject>();

            /* Editor-only companions that are deliberately left out rather than merged */
            var skippedEditorExports = new List<UObject>();

            /* The segment beside a cooked package was written by the editor and counts through
             * the whole class, so it is read against the completed mappings and the cooked package
             * is not. The cooked exports above are already read by this point. */
            var wasUsing = provider.MappingsContainer;

            var editorSchema = EditorSchemaFor(provider);

            if (editorSchema is not null) provider.MappingsContainer = editorSchema;

            try
            {

            if (provider.TryLoadPackage(objectPath, out var editorAsset))
            {
                foreach (var export in exports)
                {
                    /* The companion usually holds its extra data beside the export, named for it.
                     *
                     * A few types keep their copy under the export's own name instead, because the
                     * cook emptied out what only the editor knew: a validation rule set comes out of
                     * a cook with its list of rules the right length and every entry null.
                     *
                     * Asked for by type rather than tried on everything, because for most exports
                     * the cooked value is the one to serve. */
                    var editorData = editorAsset.GetExportOrNull($"{export.Name}EditorOnlyData");

                    if (editorData is null && EditorCopyReplacesCooked.Contains(export.ExportType))
                    {
                        editorData = editorAsset.GetExportOrNull(export.Name);
                    }

                    if (editorData is null)
                    {
                        continue;
                    }

                    /* BuildingTextureData is read straight off the cooked export, whose texture
                     * and material references are the ones callers resolve against. Its editor
                     * companion describes the same slots the way the editor saw them, so merging
                     * it over the top replaces good references with editor-side ones */
                    if (export is UBuildingTextureData)
                    {
                        skippedEditorExports.Add(editorData);
                        continue;
                    }

                    /* Merged over rather than alongside. Both copies naming the same property is
                     * one property said twice, and what the cook emptied is the copy to drop. */
                    foreach (var property in editorData.Properties)
                    {
                        export.Properties.RemoveAll(existing => existing.Name.Text == property.Name.Text);
                    }

                    export.Properties.AddRange(editorData.Properties);

                    /* The editor copy, since that is what the sweep below decides about. Naming the
                     * cooked one leaves what was just folded in to be served again on its own. */
                    mergedExports.Add(editorData);
                }

                /* What the cooked package already has, so the same object is not served twice. Where
                 * there is no companion beside an asset, a provider can hand back the asset itself
                 * rather than nothing, and every export in it then arrives a second time. */
                var cookedNames = exports.Select(cooked => cooked.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                finalExports.AddRange(editorAsset.GetExports()
                    .Where(editorExport => !mergedExports.Contains(editorExport))
                    .Where(editorExport => !skippedEditorExports.Contains(editorExport))
                    .Where(editorExport => !cookedNames.Contains(editorExport.Name)));
            }

            }
            finally
            {
                provider.MappingsContainer = wasUsing;
            }

            mergedExports.Clear();

            var converters = new Dictionary<Type, JsonConverter>
                { { typeof(FColorVertexBuffer), new FColorVertexBufferCustomConverter() } };
            var settings = new JsonSerializerSettings
                { ContractResolver = new FColorVertexBufferCustomResolver(converters!) };

            var json = JsonConvert.SerializeObject(new
            {
                exports = finalExports
            }, Formatting.Indented, settings);

            return new ContentResult
            {
                Content = json,
                ContentType = "application/json",
                StatusCode = 200
            };
        }
        catch (Exception)
        {
            return NotFoundResponse;
        }
    }
    
    [NonAction]
    public ActionResult HandleExportMetadata(string path, BaseProvider provider, IPackage? package = null)
    {
        try
        {
            var json = JsonConvert.SerializeObject(package ?? provider.LoadPackage(path), Formatting.Indented);

            return new ContentResult
            {
                Content = json,
                ContentType = "application/json",
                StatusCode = 200
            };
        }
        catch (Exception)
        {
            /* ignored */
        }

        return NotFoundResponse;
    }
    
    /* The export an endpoint is about, which is not always the one named after the package holding
     * it: SK_StopAxe holds its mesh as SK_StopAxe_StopAxe, and a provider asked for the object by
     * the package's name comes back with nothing. Asked for by name first, so a package holding
     * several of a kind still answers with the one that was named, and otherwise the first export
     * of the kind wanted. */
    private static T? LoadExportOfType<T>(BaseProvider provider, string path) where T : UObject
    {
        provider.TryLoadPackageObject(path, export: out var localObject);

        if (localObject is T named) return named;

        if (provider.TryLoadPackage(path, out var package))
        {
            foreach (var lazyExport in package.ExportsLazy)
            {
                if (lazyExport.Value is T export) return export;
            }
        }

        return null;
    }

    /* If the path exists on the main profile, it'll check if other profiles specifically override the main profile, if so it'll pick that, else it'll give the one found initially
     * If the path doesn't exist on a main profile, it'll cycle through each profile to find one that has the asset existing */
    /* The completed mappings belonging to whichever profile mounted this provider */
    private static ITypeMappingsProvider? EditorSchemaFor(BaseProvider provider)
    {
        if (MainProfile is { } main && ReferenceEquals(main.Provider, provider))
        {
            return main.EditorMappings;
        }

        foreach (var profile in SecondaryBaseProfiles)
        {
            if (ReferenceEquals(profile.Provider, provider))
            {
                return profile.EditorMappings;
            }
        }

        return null;
    }

    private static BaseProfile FindBaseProfileForPath(string rawPath, out bool found)
    {
        var path = rawPath.SubstringBefore('.');
        found = false;
        
        if (MainProfile!.Provider.TryLoadPackage(path, package: out _))
        {
            found = true;

            foreach (var profile in SecondaryBaseProfiles)
            {
                if (!profile.IsInitialized) continue;
                if (!profile.Provider.TryLoadPackage(path, package: out var package)) continue;
                var assetType = package.GetExports().FirstOrDefault()?.ExportType;

                string[] EditorOnlyTypes =
                [
                    "Material",
                    "MaterialFunction",
                ];

                if (assetType is not null && EditorOnlyTypes.Contains(assetType, StringComparer.OrdinalIgnoreCase))
                {
                    return profile;
                }
            }
        }
        else
        {
            foreach (var profile in SecondaryBaseProfiles)
            {
                if (!profile.IsInitialized) continue;
                if (!profile.Provider.TryLoadPackage(path, package: out _)) continue;
            
                found = true;
                
                return profile;
            }
        }
        
        found = true;

        return MainProfile;
    }
}
