using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Rig;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

using Microsoft.AspNetCore.Mvc;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: DNA                                                                                                       */
/*                                                                                                                                  */
/* A MetaHuman head carries its rig as a DNA hung off the mesh's asset user data, and the DNA itself is a bit stream written after   */
/* that export's properties rather than anything the export can describe. It is handed back whole, the way it sits in the package,   */
/* because the only thing that reads it is RigLogic's own reader.                                                                   */
/*                                                                                                                                  */
/* Some heads keep it a step further out: the user data is a DNAAssetUserData naming a DNA that lives in a package of its own,       */
/* alongside the mesh, so both shapes are followed.                                                                                 */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    /* A DNA in its own package is class 'DNA' rather than 'DNAAsset', and nothing reads it without
     * being told the two are the same thing */
    private static readonly bool DnaClassAliasRegistered = RegisterDnaClassAlias();

    private static bool RegisterDnaClassAlias()
    {
        ObjectTypeRegistry.RegisterClass("DNA", typeof(UDNAAsset));

        return true;
    }

    /* The mesh's DNA, as the bytes RigLogic reads. Cooked packages keep the behavior layer and
     * write an empty placeholder where the geometry was. */
    [HttpGet("export/dna")]
    public ActionResult GetDna(string? path)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new
        {
            errorCode = "cloud.dna.no_path",
            errorMessage = "No asset supplied",
            numericErrorCode = 1005
        });

        path = path.SubstringBefore('.');

        var profile = FindBaseProfileForPath(path, found: out var found);
        if (!found) return NotFoundResponse;

        if (LoadExportOfType<USkeletalMesh>(profile.Provider, path) is not { } skeletalMesh || skeletalMesh.AssetUserData is null)
        {
            return NotFoundResponse;
        }

        foreach (var userData in skeletalMesh.AssetUserData)
        {
            if (!userData.TryLoad(out var loaded) || loaded is null) continue;

            if (FindDnaBytes(loaded) is { } bytes)
            {
                return File(bytes, "application/octet-stream", $"{loaded.Name}.dna");
            }
        }

        return NotFoundResponse;
    }

    /* The user data either is the DNA, or names one sitting in a package of its own */
    private static byte[]? FindDnaBytes(UObject userData)
    {
        if (userData is UDNAAsset direct)
        {
            return direct.DNAData is { } data && data.Value.Length > 0 ? data.Value : null;
        }

        if (userData.TryGetValue<UDNAAsset>(out var referenced, "DNAAsset") && referenced is not null)
        {
            return referenced.DNAData is { } data && data.Value.Length > 0 ? data.Value : null;
        }

        if (userData.TryGetValue<FPackageIndex>(out var index, "DNAAsset") &&
            index.TryLoad<UDNAAsset>(out var loaded) && loaded is not null)
        {
            return loaded.DNAData is { } data && data.Value.Length > 0 ? data.Value : null;
        }

        return null;
    }
}
