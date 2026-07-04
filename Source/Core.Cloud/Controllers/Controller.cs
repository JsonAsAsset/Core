using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Textures;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Meshes;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;

using Microsoft.AspNetCore.Mvc;

using Newtonsoft.Json;

using Serilog;
using Core.Resources;
using Core.Resources.Convertors;
using Core.Resources.Framework.Base;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

[Route("api")]
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public class CloudApiController : ControllerBase
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
    }
    
    public static void SetSecondaryProfile(BaseProfile profile)
    {
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

        paths.AddRange(
        MainProfile?.Provider!.Files.Values!
            .Select(a => a?.PathWithoutExtension)
            .Where(p =>
                p.Contains("/HLOD/") &&
                p.Contains("/Maps/") &&
                p.Contains("FortniteGame/Content") &&
                !p.EndsWith(".o"))
            .Distinct()
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
                    wave.Decode(true, out _, out var data);

                    if (data != null)
                    {
                        var ownerName = wave.Owner!.Name;
                        ownerName = ownerName.SubstringBeforeWithLast('/').TrimEnd('/');

                        Log.Information(Globals.AudioFilesFolder.FullName);

                        var cleanOwner = ownerName.TrimStart('/').Replace("/", "\\");
                        var savePath = Path.Combine(Globals.AudioFilesFolder.FullName, cleanOwner);
                        var finalPath = Path.Combine(savePath, Path.GetFileName(path) + ".ogg");
                        
                        Directory.CreateDirectory(savePath);
                        System.IO.File.WriteAllBytes(finalPath, data);

                        return new JsonResult(new
                        {
                            file = finalPath
                        });
                    }
                    
                    break;
                }
            }
        }

        /* Return a raw export */
        if (raw) return HandleRawExport(path, provider);
        if (metadata is true) return HandleExportMetadata(path, provider);

        /* Switch on Class Type */
        return localObject switch
        {
            UTexture texture => ProcessTexture(texture, contentType!),
            USoundWave wave => ProcessSoundWave(wave),
            UStaticMesh staticMesh => ProcessStaticMesh(staticMesh),
            _ => HandleRawExport(path, provider)
        };
    }
    
    private static JsonResult ProcessStaticMesh(UStaticMesh staticMesh)
    {
        var exporterOptions = new ExporterOptions
        {
            MeshFormat = EMeshFormat.ActorX,
            ExportMaterials = false,
            ExportMorphTargets = false
        };

        var exporter = new MeshExporter(staticMesh, exporterOptions);

        var input = staticMesh.GetPathName();
        input = input.Substring(0, input.LastIndexOf(".", StringComparison.Ordinal));
            
        var directory = Path.Combine(Globals.RuntimeFolder.FullName, input);
        var output = new FileInfo(directory + ".pskx");

        if (!output.Exists)
        {
            Directory.CreateDirectory(path: Path.GetDirectoryName(directory)!);
        }

        var newLod = exporter.MeshLods[0];
        var newFilePath = newLod.FileName[..newLod.FileName.LastIndexOf('/')];
        
        newFilePath = newFilePath[..newFilePath.LastIndexOf('/')] + "/" + staticMesh.Name + ".pskx";

        newLod = new Mesh(newFilePath, newLod.FileData, newLod.Materials);
        newLod.TryWriteToDir(new DirectoryInfo(Globals.RuntimeFolder.FullName), out var label, out var filePath);

        return new JsonResult(new
        {
            path = filePath.Replace("/", "\\")
        });
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
            return StatusCode(500, new
            {
                errored = true,
                exceptionstring = "Invalid texture data, exported as json",
                exports = new { texture }
            });
        }

        return File(textureData.Encode(ETextureFormat.Png, false, out _), "image/png");
    }

    /* Return a sound wave file format */
    private ActionResult ProcessSoundWave(USoundWave wave)
    {
        wave.Decode(true, out var audioFormat, out var data);
        
        if (data is null || string.IsNullOrEmpty(audioFormat))
        {
            return Conflict(new
            {
                errored = true,
                exceptionstring = "Invalid audio data, returned raw export as json",
                exports = new[] { wave }
            });
        }

        var mimeType = audioFormat.ToLower() switch
        {
            "wem" => "application/vnd.wwise.wem",
            "wav" => "audio/wav",
            "adpcm" => "audio/adpcm",
            "opus" => "audio/opus",
            _ => "audio/ogg"
        };

        return File(data, mimeType);
    }

    /* Handle raw exports */
    public ActionResult HandleRawExport(string path, BaseProvider provider)
    {
        try
        {
            var objectPath = $"{path.SubstringBefore('.')}.o.uasset";

            var pkg = provider.LoadPackage(path);
            var exports = pkg.GetExports().ToArray();
            var finalExports = new List<UObject>(exports);

            var mergedExports = new List<UObject>();
            if (provider.TryLoadPackage(objectPath, out var editorAsset))
            {
                foreach (var export in exports)
                {
                    var editorData = editorAsset.GetExportOrNull($"{export.Name}EditorOnlyData");
                    if (editorData is null)
                    {
                        continue;
                    }

                    export.Properties.AddRange(editorData.Properties);
                    mergedExports.Add(export);
                }

                finalExports.AddRange(editorAsset.GetExports()
                    .Where(editorExport => !mergedExports.Contains(editorExport)));
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
    
    public ActionResult HandleExportMetadata(string path, BaseProvider provider)
    {
        try
        {
            var json = JsonConvert.SerializeObject(provider.LoadPackage(path), Formatting.Indented);

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
    
    /* If the path exists on the main profile, it'll check if other profiles specifically override the main profile, if so it'll pick that, else it'll give the one found initially
     * If the path doesn't exist on a main profile, it'll cycle through each profile to find one that has the asset existing */
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
