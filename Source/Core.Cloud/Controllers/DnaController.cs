using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Rig;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

using Core.Cloud.Objects;

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
     * being told what that is.
     *
     * A static constructor rather than a field initializer: a field nothing ever reads is one the
     * runtime is free to never initialize, which left the registration undone and every standalone
     * DNA coming back as a bare UObject. */
    static CloudApiController()
    {
        ObjectTypeRegistry.RegisterClass("DNA", typeof(UDNA));
    }

    /* The mesh's DNA, as the bytes RigLogic reads. Cooked packages keep the behavior layer and
     * write an empty placeholder where the geometry was. */
    [HttpGet("export/dna")]
    public ActionResult GetDna(string? path, bool? riglogic, bool? legacy)
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

        /* Every export in the package, rather than only what the mesh's AssetUserData names: cooking
         * drops that property, so on most heads the DNAAssetUserData sits in the package with
         * nothing pointing at it. This covers that, a DNA asked for directly, and a mesh that does
         * still carry the reference. */
        var wantRigLogic = riglogic is true;
        var wantLegacy = legacy is true;

        if (profile.Provider.TryLoadPackage(path, out var package))
        {
            foreach (var export in package.GetExports())
            {
                if (FindDnaBytes(export, wantRigLogic) is { } bytes)
                {
                    return RigLogicResult(bytes, export.Name, wantRigLogic, wantLegacy, export);
                }
            }
        }

        if (LoadExportOfType<USkeletalMesh>(profile.Provider, path) is { AssetUserData: not null } skeletalMesh)
        {
            foreach (var userData in skeletalMesh.AssetUserData)
            {
                if (!userData.TryLoad(out var loaded) || loaded is null) continue;

                if (FindDnaBytes(loaded, wantRigLogic) is { } bytes)
                {
                    return RigLogicResult(bytes, loaded.Name, wantRigLogic, wantLegacy, loaded);
                }
            }
        }

        return NotFoundResponse;
    }

    /* Four things can be asked for here:
     *
     *   (nothing)                the DNA as the package holds it, which on a UE6 head is the
     *                            definition layer and no rig
     *   legacy                   that same DNA with the behavior layer put back and written at the
     *                            file version an older RigLogic reads. This is the one an engine
     *                            before 6.0 can actually load a face out of.
     *   riglogic                 the baked rig on its own, as UE6 dumped it
     *   riglogic + legacy        that dump rewritten the way 5.7's RigLogicLib reads one */
    private ActionResult RigLogicResult(byte[] bytes, string name, bool rigLogic, bool legacy, UObject export)
    {
        if (rigLogic)
        {
            if (!legacy)
            {
                return File(bytes, "application/octet-stream", $"{name}.riglogic");
            }

            if (RigLogicTranscoder.ToLegacy(bytes) is not { } transcoded)
            {
                return NotTranscodable();
            }

            return File(transcoded, "application/octet-stream", $"{name}.riglogic");
        }

        if (!legacy)
        {
            return File(bytes, "application/octet-stream", $"{name}.dna");
        }

        /* Only a DNA that had its rig taken out of it has one to put back */
        if (FindDna(export) is not { RigLogicSize: > 0 } dna)
        {
            return File(bytes, "application/octet-stream", $"{name}.dna");
        }

        if (RigLogicDnaBuilder.Rebuild(dna.ReadFullStream(), dna.DnaSize) is not { } rebuilt)
        {
            return NotTranscodable();
        }

        return File(rebuilt, "application/octet-stream", $"{name}.dna");
    }

    private ActionResult NotTranscodable() => Conflict(new
    {
        errorCode = "cloud.dna.rig_not_transcodable",
        errorMessage = "This rig is not in a layout that can be rewritten for an older engine",
        numericErrorCode = 1006
    });

    /* The UDNA behind whatever was handed in, however many references deep it sits */
    private static UDNA? FindDna(UObject userData, int depth = 0)
    {
        if (depth > 4) return null;

        if (userData is UDNA standalone) return standalone;

        if (userData.TryGetValue<UObject>(out var referenced, "DNAAsset") && referenced is not null)
        {
            return FindDna(referenced, depth + 1);
        }

        if (userData.TryGetValue<FPackageIndex>(out var index, "DNAAsset") &&
            index.TryLoad<UObject>(out var loaded) && loaded is not null)
        {
            return FindDna(loaded, depth + 1);
        }

        return null;
    }

    /* The user data either is the DNA, or names one sitting in a package of its own. Which class it
     * comes back as depends on how the package spelled it, so the reference is followed as whatever
     * it is and asked the same question again. */
    private static byte[]? FindDnaBytes(UObject userData, bool rigLogic, int depth = 0)
    {
        if (depth > 4) return null;

        if (userData is UDNA standalone)
        {
            var bytes = rigLogic ? standalone.ReadRigLogicState() : standalone.ReadStream();

            return bytes is { Length: > 0 } ? bytes : null;
        }

        /* The legacy shape keeps the rig in the DNA's own behavior layer, so there is no separate
         * blob to ask for */
        if (userData is UDNAAsset direct)
        {
            if (rigLogic) return null;

            return direct.DNAData is { } data && data.Value.Length > 0 ? data.Value : null;
        }

        if (userData.TryGetValue<UObject>(out var referenced, "DNAAsset") && referenced is not null)
        {
            return FindDnaBytes(referenced, rigLogic, depth + 1);
        }

        if (userData.TryGetValue<FPackageIndex>(out var index, "DNAAsset") &&
            index.TryLoad<UObject>(out var loaded) && loaded is not null)
        {
            return FindDnaBytes(loaded, rigLogic, depth + 1);
        }

        return null;
    }
}
