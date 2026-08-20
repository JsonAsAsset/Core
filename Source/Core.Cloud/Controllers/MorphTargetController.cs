using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.Utils;

using Microsoft.AspNetCore.Mvc;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: Morph Targets                                                                                             */
/*                                                                                                                                  */
/* What every morph moves, per LOD. A cook keeps the deltas quantized in the GPU morph buffers rather than as the vertex data the    */
/* editor imports, so the export carries how many vertices a morph touched and nothing about where they go. Decoded back here, the   */
/* deltas key off the same vertex indices the LOD model serves, which is what lets them land on a rebuilt mesh.                      */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    /* Flattened the same way the LOD model's streams are: one index per delta, three floats each
     * of position and normal alongside it */
    private sealed record MorphLod(int Index, int Count, uint[] SourceIndices, float[] PositionDeltas, float[] TangentZDeltas, int[] SectionIndices);

    private sealed record MorphTarget(string Name, List<MorphLod> Lods);

    /* The cooked morph deltas, one entry per morph target the mesh names */
    [HttpGet("export/morphtargets")]
    public ActionResult GetMorphTargets(string? path)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new
        {
            errorCode = "cloud.morphtargets.no_path",
            errorMessage = "No asset supplied",
            numericErrorCode = 1005
        });

        path = path.SubstringBefore('.');

        var profile = FindBaseProfileForPath(path, found: out var found);
        if (!found) return NotFoundResponse;

        if (LoadExportOfType<USkeletalMesh>(profile.Provider, path) is not { } skeletalMesh)
        {
            return NotFoundResponse;
        }

        /* Decodes the GPU buffers, or the compressed CPU data, into deltas on the morphs themselves */
        skeletalMesh.PopulateMorphTargetVerticesData();

        var morphs = new List<MorphTarget>(skeletalMesh.MorphTargets.Length);

        foreach (var morphTargetIndex in skeletalMesh.MorphTargets)
        {
            if (!morphTargetIndex.TryLoad<UMorphTarget>(out var morphTarget)) continue;

            var lods = new List<MorphLod>(morphTarget.MorphLODModels.Length);

            for (var lodIndex = 0; lodIndex < morphTarget.MorphLODModels.Length; lodIndex++)
            {
                if (BuildMorphLod(morphTarget.MorphLODModels[lodIndex], lodIndex) is { } lod)
                {
                    lods.Add(lod);
                }
            }

            if (lods.Count == 0) continue;

            morphs.Add(new MorphTarget(morphTarget.Name, lods));
        }

        return new JsonResult(new { morphs });
    }

    private static MorphLod? BuildMorphLod(FMorphTargetLODModel lodModel, int lodIndex)
    {
        if (lodModel.Vertices is not { Length: > 0 } vertices) return null;

        var sourceIndices = new uint[vertices.Length];
        var positionDeltas = new float[vertices.Length * 3];
        var tangentZDeltas = new float[vertices.Length * 3];

        for (var index = 0; index < vertices.Length; index++)
        {
            var delta = vertices[index];

            sourceIndices[index] = delta.SourceIdx;

            positionDeltas[index * 3 + 0] = (float)delta.PositionDelta.X;
            positionDeltas[index * 3 + 1] = (float)delta.PositionDelta.Y;
            positionDeltas[index * 3 + 2] = (float)delta.PositionDelta.Z;

            tangentZDeltas[index * 3 + 0] = (float)delta.TangentZDelta.X;
            tangentZDeltas[index * 3 + 1] = (float)delta.TangentZDelta.Y;
            tangentZDeltas[index * 3 + 2] = (float)delta.TangentZDelta.Z;
        }

        return new MorphLod(lodIndex, vertices.Length, sourceIndices, positionDeltas, tangentZDeltas, lodModel.SectionIndices ?? []);
    }
}
