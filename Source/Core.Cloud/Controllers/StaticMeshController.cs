using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.Utils;

using Microsoft.AspNetCore.Mvc;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: Static Mesh                                                                                               */
/*                                                                                                                                  */
/* The cooked geometry of a static mesh, vertex for vertex, with its material slots. Nothing goes through an exchange format, so     */
/* vertex order and splits are the ones the game shipped.                                                                           */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    private sealed record StaticSection(int MaterialIndex, int FirstIndex, int NumTriangles, int MinVertexIndex, int MaxVertexIndex, bool EnableCollision, bool CastShadow);

    /* Flat parallel arrays, one entry per cooked vertex */
    private sealed record StaticVertices(int Count, int NumTexCoords, float[] Positions, float[] Normals, float[] Tangents, float[] Signs, float[] UVs, uint[] Colors);

    private sealed record StaticLod(int Index, float ScreenSize, uint[] Indices, List<StaticSection> Sections, StaticVertices Vertices);

    /* Slot name and the material it points at, so the importer can rebuild the slots in order */
    private sealed record StaticSlot(string SlotName, string ImportedSlotName, string? Material);

    [HttpGet("export/staticmesh")]
    public ActionResult GetStaticMesh(string? path)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new
        {
            errorCode = "cloud.staticmesh.no_path",
            errorMessage = "No asset supplied",
            numericErrorCode = 1006
        });

        path = path.SubstringBefore('.');

        var profile = FindBaseProfileForPath(path, found: out var found);
        if (!found) return NotFoundResponse;

        profile.Provider.TryLoadPackageObject(path, export: out var localObject);

        if (localObject is not UStaticMesh { RenderData: { } renderData } staticMesh)
        {
            return NotFoundResponse;
        }

        var slots = new List<StaticSlot>();

        foreach (var material in staticMesh.StaticMaterials)
        {
            slots.Add(new StaticSlot(
                material.MaterialSlotName.Text,
                material.ImportedMaterialSlotName?.Text ?? material.MaterialSlotName.Text,
                material.MaterialInterface?.Name));
        }

        var lods = new List<StaticLod>();

        for (var lodIndex = 0; lodIndex < renderData.LODs.Length; lodIndex++)
        {
            var screenSize = lodIndex < renderData.ScreenSize.Length ? renderData.ScreenSize[lodIndex] : 0.0f;

            if (BuildStaticLod(renderData.LODs[lodIndex], lodIndex, screenSize) is { } lod)
            {
                lods.Add(lod);
            }
        }

        return new JsonResult(new
        {
            name = staticMesh.Name,
            slots,
            lods
        });
    }

    private static StaticLod? BuildStaticLod(FStaticMeshLODResources lodResources, int lodIndex, float screenSize)
    {
        if (lodResources.SkipLod) return null;

        var positionBuffer = lodResources.PositionVertexBuffer;
        var vertexBuffer = lodResources.VertexBuffer;

        if (positionBuffer is null || vertexBuffer is null) return null;
        if (lodResources.IndexBuffer?.Buffer is not { Length: > 0 } indices) return null;

        var count = positionBuffer.Verts.Length;
        if (count == 0) return null;

        /* The header count and what the vertices actually carry can disagree, and writing to the
         * larger of the two leaves channels empty at the far end */
        var vertexTexCoords = vertexBuffer.UV.Length > 0 ? vertexBuffer.UV[0].UV.Length : 0;
        var numTexCoords = Math.Max(Math.Min(vertexBuffer.NumTexCoords, vertexTexCoords), 1);
        var colors = lodResources.ColorVertexBuffer?.Data ?? [];

        var positions = new float[count * 3];
        var normals = new float[count * 3];
        var tangents = new float[count * 3];
        var signs = new float[count];
        var uvs = new float[count * numTexCoords * 2];
        var vertexColors = new uint[count];

        for (var index = 0; index < count; index++)
        {
            var position = positionBuffer.Verts[index];

            positions[index * 3 + 0] = (float)position.X;
            positions[index * 3 + 1] = (float)position.Y;
            positions[index * 3 + 2] = (float)position.Z;

            /* Sign carries the bitangent handedness, which is lost if only the two vectors travel */
            signs[index] = 1.0f;

            if (index < vertexBuffer.UV.Length)
            {
                var item = vertexBuffer.UV[index];

                if (item.Normal.Length > 2)
                {
                    var normal = (FVector)item.Normal[2];
                    var tangent = (FVector)item.Normal[0];

                    normals[index * 3 + 0] = (float)normal.X;
                    normals[index * 3 + 1] = (float)normal.Y;
                    normals[index * 3 + 2] = (float)normal.Z;

                    tangents[index * 3 + 0] = (float)tangent.X;
                    tangents[index * 3 + 1] = (float)tangent.Y;
                    tangents[index * 3 + 2] = (float)tangent.Z;

                    /* W of the packed normal is the handedness, stored as a signed byte */
                    signs[index] = item.Normal[2].W < 0 ? -1.0f : 1.0f;
                }

                for (var uv = 0; uv < numTexCoords && uv < item.UV.Length; uv++)
                {
                    uvs[(index * numTexCoords + uv) * 2 + 0] = item.UV[uv].U;
                    uvs[(index * numTexCoords + uv) * 2 + 1] = item.UV[uv].V;
                }
            }

            /* Packed RGBA, unpacked by component at the other end */
            var color = index < colors.Length ? colors[index] : new FColor(255, 255, 255, 255);

            vertexColors[index] = ((uint)color.R << 24) | ((uint)color.G << 16) | ((uint)color.B << 8) | color.A;
        }

        var sections = new List<StaticSection>(lodResources.Sections.Length);

        foreach (var section in lodResources.Sections)
        {
            sections.Add(new StaticSection(
                section.MaterialIndex,
                section.FirstIndex,
                section.NumTriangles,
                section.MinVertexIndex,
                section.MaxVertexIndex,
                section.bEnableCollision,
                section.bCastShadow));
        }

        return new StaticLod(lodIndex, screenSize, indices, sections, new StaticVertices(count, numTexCoords, positions, normals, tangents, signs, uvs, vertexColors));
    }
}
