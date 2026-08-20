using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.Utils;

using Microsoft.AspNetCore.Mvc;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: Reference Skeleton                                                                                        */
/*                                                                                                                                  */
/* The pose a mesh is bound at. A mesh carries its own, and it is not the one its skeleton asset carries: characters share a         */
/* skeleton and each is built at its own proportions. Everything the mesh is skinned with is measured against this pose, so an       */
/* import that substitutes the skeleton's deforms the moment anything moves a bone away from it.                                     */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    private sealed record ReferenceBone(
        string Name,
        int ParentIndex,
        float[] Translation,
        float[] Rotation,
        float[] Scale);

    /* The mesh's own bind pose, bone for bone, in the order the mesh indexes them */
    [HttpGet("export/refskeleton")]
    public ActionResult GetReferenceSkeleton(string? path)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new
        {
            errorCode = "cloud.refskeleton.no_path",
            errorMessage = "No asset supplied",
            numericErrorCode = 1005
        });

        path = path.SubstringBefore('.');

        var profile = FindBaseProfileForPath(path, found: out var found);
        if (!found) return NotFoundResponse;

        if (LoadExportOfType<USkeletalMesh>(profile.Provider, path) is not { } skeletalMesh || skeletalMesh.ReferenceSkeleton is not { } referenceSkeleton)
        {
            return NotFoundResponse;
        }

        var info = referenceSkeleton.FinalRefBoneInfo;
        var pose = referenceSkeleton.FinalRefBonePose;

        if (info is not { Length: > 0 } || pose is null || pose.Length != info.Length)
        {
            return NotFoundResponse;
        }

        var bones = new List<ReferenceBone>(info.Length);

        for (var index = 0; index < info.Length; index++)
        {
            var transform = pose[index];

            bones.Add(new ReferenceBone(
                info[index].Name.Text,
                info[index].ParentIndex,
                [(float)transform.Translation.X, (float)transform.Translation.Y, (float)transform.Translation.Z],
                [(float)transform.Rotation.X, (float)transform.Rotation.Y, (float)transform.Rotation.Z, (float)transform.Rotation.W],
                [(float)transform.Scale3D.X, (float)transform.Scale3D.Y, (float)transform.Scale3D.Z]));
        }

        return new JsonResult(new { bones });
    }
}
