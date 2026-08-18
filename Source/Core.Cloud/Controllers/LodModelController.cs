using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.Utils;

using Microsoft.AspNetCore.Mvc;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: LOD Model                                                                                                 */
/*                                                                                                                                  */
/* The cooked geometry of a skeletal mesh, vertex for vertex. Anything that goes through an exchange format has its vertices         */
/* re-derived on import, which breaks cloth binding, skin weight profiles and morph deltas: they all key off the original ones.      */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    /* Bones are named per section, since a section's influences index its own bone map */
    private sealed record LodSection(int MaterialIndex, int BaseIndex, int NumTriangles, int BaseVertexIndex, int NumVertices, string[] BoneMap);

    /* Influences are flattened: BonesPerVertex entries per vertex, indexed into the owning
     * section's bone map, with a matching weight normalized to one */
    private sealed record LodVertices(int Count, int BonesPerVertex, float[] Positions, float[] Normals, float[] Tangents, float[] Binormals, float[] UVs, int NumTexCoords, uint[] Colors, int[] Bones, float[] Weights);

    private sealed record LodModel(int Index, uint[] Indices, List<LodSection> Sections, LodVertices Vertices);

    /* The cooked geometry, one entry per LOD */
    [HttpGet("export/lodmodel")]
    public ActionResult GetLodModel(string? path)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new
        {
            errorCode = "cloud.lodmodel.no_path",
            errorMessage = "No asset supplied",
            numericErrorCode = 1005
        });

        path = path.SubstringBefore('.');

        var profile = FindBaseProfileForPath(path, found: out var found);
        if (!found) return NotFoundResponse;

        profile.Provider.TryLoadPackageObject(path, export: out var localObject);

        if (localObject is not USkeletalMesh { LODModels: { } lodModels } skeletalMesh)
        {
            return NotFoundResponse;
        }

        var boneInfo = skeletalMesh.ReferenceSkeleton?.FinalRefBoneInfo ?? [];
        var lods = new List<LodModel>();

        for (var lodIndex = 0; lodIndex < lodModels.Length; lodIndex++)
        {
            if (BuildLodModel(lodModels[lodIndex], boneInfo, lodIndex) is { } lod)
            {
                lods.Add(lod);
            }
        }

        return new JsonResult(new
        {
            bones = boneInfo.Select(Bone => Bone.Name.Text).ToArray(),
            lods
        });
    }

    private static LodModel? BuildLodModel(FStaticLODModel lodModel, FMeshBoneInfo[] boneInfo, int lodIndex)
    {
        var verts = GetVertices(lodModel);
        if (verts is null || verts.Length == 0) return null;

        /* Both widths are normalized into Buffer on the way in */
        if (lodModel.Indices?.Buffer is not { Length: > 0 } indices) return null;

        var sections = new List<LodSection>(lodModel.Sections.Length);

        foreach (var section in lodModel.Sections)
        {
            var boneMap = section.BoneMap
                .Select(BoneIndex => BoneIndex < boneInfo.Length ? boneInfo[BoneIndex].Name.Text : string.Empty)
                .ToArray();

            sections.Add(new LodSection(
                section.MaterialIndex,
                (int)section.BaseIndex,
                (int)section.NumTriangles,
                (int)section.BaseVertexIndex,
                section.NumVertices,
                boneMap));
        }

        /* Widest influence count in the LOD, so every vertex writes the same stride */
        var bonesPerVertex = 0;

        foreach (var vert in verts)
        {
            if (vert.Infs is { } infs) bonesPerVertex = Math.Max(bonesPerVertex, infs.BoneIndex.Length);
        }

        if (bonesPerVertex == 0) return null;

        var numTexCoords = Math.Max(lodModel.NumTexCoords, 1);
        var colors = lodModel.ColorVertexBuffer?.Data ?? [];

        var positions = new float[verts.Length * 3];
        var normals = new float[verts.Length * 3];
        var tangents = new float[verts.Length * 3];
        var binormals = new float[tangents.Length];
        var uvs = new float[verts.Length * numTexCoords * 2];
        var vertexColors = new uint[verts.Length];
        var bones = new int[verts.Length * bonesPerVertex];
        var weights = new float[verts.Length * bonesPerVertex];

        for (var index = 0; index < verts.Length; index++)
        {
            var vert = verts[index];

            positions[index * 3 + 0] = (float)vert.Pos.X;
            positions[index * 3 + 1] = (float)vert.Pos.Y;
            positions[index * 3 + 2] = (float)vert.Pos.Z;

            /* Normal[2] is the vertex normal, Normal[0] the U direction tangent and Normal[1] the
             * binormal. The binormal is not the cross of the other two: its sign is what says
             * which way round a mirrored UV shell is lit, and reconstructing it loses that. */
            if (vert.Normal.Length > 2)
            {
                var normal = (FVector)vert.Normal[2];
                var tangent = (FVector)vert.Normal[0];
                var binormal = (FVector)vert.Normal[1];

                normals[index * 3 + 0] = (float)normal.X;
                normals[index * 3 + 1] = (float)normal.Y;
                normals[index * 3 + 2] = (float)normal.Z;

                tangents[index * 3 + 0] = (float)tangent.X;
                tangents[index * 3 + 1] = (float)tangent.Y;
                tangents[index * 3 + 2] = (float)tangent.Z;

                binormals[index * 3 + 0] = (float)binormal.X;
                binormals[index * 3 + 1] = (float)binormal.Y;
                binormals[index * 3 + 2] = (float)binormal.Z;
            }

            for (var uv = 0; uv < numTexCoords; uv++)
            {
                if (uv >= vert.UVs.Length) break;

                uvs[(index * numTexCoords + uv) * 2 + 0] = vert.UVs[uv].U;
                uvs[(index * numTexCoords + uv) * 2 + 1] = vert.UVs[uv].V;
            }

            /* Packed RGBA, unpacked by component at the other end: FColor is BGRA in memory and
             * the two orders are indistinguishable once they are one number */
            var color = index < colors.Length ? colors[index] : new FColor(255, 255, 255, 255);

            vertexColors[index] = ((uint)color.R << 24) | ((uint)color.G << 16) | ((uint)color.B << 8) | color.A;

            if (vert.Infs is not { } influences) continue;

            /* Cooked either as bytes or as 16 bit, so the divisor comes off the flag */
            var scale = influences.bUse16BitBoneWeight ? 65535.0f : 255.0f;

            for (var influence = 0; influence < bonesPerVertex && influence < influences.BoneIndex.Length; influence++)
            {
                bones[index * bonesPerVertex + influence] = influences.BoneIndex[influence];
                weights[index * bonesPerVertex + influence] = influences.BoneWeight[influence] / scale;
            }
        }

        var vertices = new LodVertices(verts.Length, bonesPerVertex, positions, normals, tangents, binormals, uvs, numTexCoords, vertexColors, bones, weights);

        return new LodModel(lodIndex, indices, sections, vertices);
    }

    /* Full precision and half precision UVs land in different arrays */
    private static FSkelMeshVertexBase[]? GetVertices(FStaticLODModel lodModel)
    {
        var buffer = lodModel.VertexBufferGPUSkin;
        if (buffer is null) return null;

        if (buffer.VertsFloat is { Length: > 0 } floats) return floats;
        if (buffer.VertsHalf is { Length: > 0 } halves) return halves;

        return null;
    }
}
