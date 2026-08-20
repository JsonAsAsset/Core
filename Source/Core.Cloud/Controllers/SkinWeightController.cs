using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.Utils;

using Microsoft.AspNetCore.Mvc;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: Skin Weights                                                                                              */
/*                                                                                                                                  */
/* The alternate skin weights a mesh was cooked with. A normal export only carries the profile entries; the weights live in the     */
/* LOD's cooked override tables and are not properties, so nothing reaches them through a json export.                              */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    /* One overridden vertex, naming bones by index into the LOD's bone table */
    private sealed record SkinWeightOverride(uint Vertex, int[] Bones, float[] Weights);

    /* Vertices is the LOD's own count, which says whether these belong to the target mesh */
    private sealed record SkinWeightLod(int Index, int Vertices, string[] Bones, List<SkinWeightOverride> Overrides);

    /* A profile that overrides nothing comes back with no LODs rather than being left out */
    private sealed record SkinWeightProfile(string Name, List<SkinWeightLod> Lods);

    /* Sparse: only the vertices that differ, keyed by vertex index into the LOD */
    [HttpGet("export/skinweights")]
    public ActionResult GetSkinWeights(string? path)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new
        {
            errorCode = "cloud.skinweights.no_path",
            errorMessage = "No asset supplied",
            numericErrorCode = 1004
        });

        path = path.SubstringBefore('.');

        var profile = FindBaseProfileForPath(path, found: out var found);
        if (!found) return NotFoundResponse;

        if (LoadExportOfType<USkeletalMesh>(profile.Provider, path) is not { LODModels: { } lodModels } skeletalMesh)
        {
            return NotFoundResponse;
        }

        var boneInfo = skeletalMesh.ReferenceSkeleton?.FinalRefBoneInfo ?? [];
        var profiles = new Dictionary<string, SkinWeightProfile>(StringComparer.OrdinalIgnoreCase);

        for (var lodIndex = 0; lodIndex < lodModels.Length; lodIndex++)
        {
            var lodModel = lodModels[lodIndex];

            /* Cooked out of this LOD, or a version that never stored any */
            if (lodModel.SkinWeightProfilesData is not { } profilesData) continue;

            foreach (var (profileName, overrideData) in profilesData.OverrideData)
            {
                if (BuildSkinWeightLod(lodModel, overrideData, boneInfo, lodIndex) is not { } lod) continue;

                var name = profileName.Text;

                if (!profiles.TryGetValue(name, out var entry))
                {
                    entry = new SkinWeightProfile(name, []);
                    profiles.Add(name, entry);
                }

                entry.Lods.Add(lod);
            }
        }

        return new JsonResult(new
        {
            profiles = profiles.Values
        });
    }

    /* The table is flat: a vertex maps to an offset, and its influences sit at that offset times
     * the influence count. Bone ids index the owning section's bone map, not the skeleton, so each
     * is resolved through its section and comes back out as a name. */
    private static SkinWeightLod? BuildSkinWeightLod(FStaticLODModel lodModel, FRuntimeSkinWeightProfileData overrideData, FMeshBoneInfo[] boneInfo, int lodIndex)
    {
        var vertexOffsets = overrideData.VertexIndexToInfluenceOffset;

        if (vertexOffsets is null || vertexOffsets.Count == 0) return null;
        if (overrideData.BoneIDs is not { Length: > 0 } boneIds) return null;
        if (overrideData.BoneWeights is not { Length: > 0 } boneWeights) return null;

        var influences = overrideData.NumWeightsPerVertex;
        if (influences == 0) return null;

        /* Widths are one or two bytes, derived from the buffer rather than from a version check */
        var entries = vertexOffsets.Count * influences;

        var boneIndexSize = boneIds.Length / entries;
        var boneWeightSize = boneWeights.Length / entries;

        if (boneIndexSize is not (1 or 2) || boneWeightSize is not (1 or 2)) return null;

        var boneNames = new List<string>();
        var boneNameIndices = new Dictionary<string, int>(StringComparer.Ordinal);

        var overrides = new List<SkinWeightOverride>(vertexOffsets.Count);

        foreach (var (vertexIndex, influenceOffset) in vertexOffsets)
        {
            var section = FindSectionForVertex(lodModel, vertexIndex);
            if (section is null) continue;

            var vertexBones = new List<int>(influences);
            var vertexWeights = new List<float>(influences);

            for (var influence = 0; influence < influences; influence++)
            {
                var slot = (int)(influenceOffset * influences) + influence;

                var weight = ReadPacked(boneWeights, slot, boneWeightSize);

                /* Every vertex is padded to the same influence count, so the tail is zeroes */
                if (weight == 0) continue;

                var boneMapIndex = ReadPacked(boneIds, slot, boneIndexSize);
                if (boneMapIndex >= section.BoneMap.Length) continue;

                var boneIndex = section.BoneMap[boneMapIndex];
                if (boneIndex >= boneInfo.Length) continue;

                var boneName = boneInfo[boneIndex].Name.Text;

                if (!boneNameIndices.TryGetValue(boneName, out var nameIndex))
                {
                    nameIndex = boneNames.Count;

                    boneNames.Add(boneName);
                    boneNameIndices.Add(boneName, nameIndex);
                }

                vertexBones.Add(nameIndex);

                /* Normalized on the wire: the editor arrays are a different width again */
                vertexWeights.Add(weight / (float)((1 << (boneWeightSize * 8)) - 1));
            }

            if (vertexBones.Count == 0) continue;

            overrides.Add(new SkinWeightOverride(vertexIndex, [.. vertexBones], [.. vertexWeights]));
        }

        if (overrides.Count == 0) return null;

        return new SkinWeightLod(lodIndex, lodModel.NumVertices, [.. boneNames], overrides);
    }

    /* The section holding a vertex, which is what its bone ids are numbered against */
    private static FSkelMeshSection? FindSectionForVertex(FStaticLODModel lodModel, uint vertexIndex)
    {
        foreach (var section in lodModel.Sections)
        {
            if (vertexIndex < section.BaseVertexIndex) continue;
            if (vertexIndex >= section.BaseVertexIndex + (uint)section.NumVertices) continue;

            return section;
        }

        return null;
    }

    /* One little endian value of ByteSize bytes at Slot */
    private static uint ReadPacked(byte[] buffer, int slot, int byteSize)
    {
        var offset = slot * byteSize;

        if (byteSize == 1) return buffer[offset];

        return (uint)(buffer[offset] | (buffer[offset + 1] << 8));
    }
}
