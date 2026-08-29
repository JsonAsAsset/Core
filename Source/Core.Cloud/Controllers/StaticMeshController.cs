using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.Utils;

using Core.Resources.Framework.Base;

using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Options;

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

    /* Nanite says the geometry came out of the cluster stream rather than off a cooked LOD, so the
     * far end knows whether it is holding the mesh or the fallback */
    private sealed record StaticLod(int Index, float ScreenSize, uint[] Indices, List<StaticSection> Sections, StaticVertices Vertices, bool Nanite = false);

    /* Slot name and the material it points at, so the importer can rebuild the slots in order */
    private sealed record StaticSlot(string SlotName, string ImportedSlotName, string? Material);

    /* The mesh a caller named, or the first one where they named none */
    private static UStaticMesh? FindStaticMesh(BaseProvider provider, string path, string? exportName)
    {
        if (!string.IsNullOrWhiteSpace(exportName) && provider.TryLoadPackage(path, out var package))
        {
            foreach (var lazyExport in package.ExportsLazy)
            {
                if (lazyExport.Value is UStaticMesh named &&
                    string.Equals(named.Name, exportName, StringComparison.OrdinalIgnoreCase))
                {
                    return named;
                }
            }
        }

        return LoadExportOfType<UStaticMesh>(provider, path);
    }

    [HttpGet("export/staticmesh")]
    public ActionResult GetStaticMesh(string? path, string? export_name)
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

        /* Which mesh of the package is wanted.
         *
         * A package holding one mesh needs nothing said: it is the only one there. An HLOD proxy
         * keeps a mesh per thing it stands in for, four of them under the one name, and answering
         * with whichever comes first hands the same geometry back four times. */
        var staticMesh = FindStaticMesh(profile.Provider, path, export_name);

        if (staticMesh is not { RenderData: { } renderData })
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

        /* The geometry a Nanite mesh actually is.
         *
         * A Nanite mesh keeps what it draws in a stream of clusters, and what it keeps beside that
         * as an ordinary LOD is the fallback -- a cut-down mesh, for where Nanite will not draw.
         * Read off the LODs alone, a Nanite mesh comes back as its own fallback and the mesh
         * somebody modelled is left in the stream.
         *
         * The stream is readable: it is clusters of vertices and triangles, and put back together
         * they are the mesh. So it is read and handed over first, and the fallback follows it. */
        if (renderData.NaniteResources is { PageStreamingStates.Length: > 0 })
        {
            if (BuildNaniteLod(staticMesh) is { } nanite)
            {
                for (var index = 0; index < lods.Count; index++)
                {
                    lods[index] = lods[index] with { Index = index + 1 };
                }

                lods.Insert(0, nanite);
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

    /* Read back out of the cluster stream, as the one LOD it makes */
    private static StaticLod? BuildNaniteLod(UStaticMesh staticMesh)
    {
        MeshLodDto<MeshVertex>? read;

        try
        {
            /* Only the Nanite, since the LODs beside it have been read already */
            var whole = new StaticMeshDto(staticMesh, EMeshQuality.All, ENaniteMeshFormat.NaniteOnly);

            read = whole.LODs.FirstOrDefault(one => one.IsNanite);
        }
        catch (Exception)
        {
            /* A stream this build cannot read is not worth taking the mesh down over: the fallback
             * is still there, and comes back as it did before */
            return null;
        }

        if (read is not { Vertices.Length: > 0, Indices.Length: > 0 }) return null;

        var count = read.Vertices.Length;

        /* However many the clusters carried, and never none */
        var numTexCoords = Math.Max(read.ExtraUvs.Length > 0 ? read.ExtraUvs.Length + 1 : 1, 1);

        var positions = new float[count * 3];
        var normals = new float[count * 3];
        var tangents = new float[count * 3];
        var signs = new float[count];
        var uvs = new float[count * numTexCoords * 2];
        var colors = new uint[count];

        for (var index = 0; index < count; index++)
        {
            var vertex = read.Vertices[index];

            positions[index * 3 + 0] = (float)vertex.Position.X;
            positions[index * 3 + 1] = (float)vertex.Position.Y;
            positions[index * 3 + 2] = (float)vertex.Position.Z;

            normals[index * 3 + 0] = (float)vertex.Normal.X;
            normals[index * 3 + 1] = (float)vertex.Normal.Y;
            normals[index * 3 + 2] = (float)vertex.Normal.Z;

            tangents[index * 3 + 0] = (float)vertex.Tangent.X;
            tangents[index * 3 + 1] = (float)vertex.Tangent.Y;
            tangents[index * 3 + 2] = (float)vertex.Tangent.Z;

            /* Sign carries the bitangent handedness, which is lost if only the two vectors travel */
            signs[index] = vertex.Tangent.W < 0 ? -1.0f : 1.0f;

            uvs[index * numTexCoords * 2 + 0] = vertex.Uv.U;
            uvs[index * numTexCoords * 2 + 1] = vertex.Uv.V;

            /* The rest of them are kept apart from the vertex, one array to a channel */
            for (var channel = 1; channel < numTexCoords && channel - 1 < read.ExtraUvs.Length; channel++)
            {
                var held = read.ExtraUvs[channel - 1];

                if (index >= held.Length) continue;

                uvs[(index * numTexCoords + channel) * 2 + 0] = held[index].U;
                uvs[(index * numTexCoords + channel) * 2 + 1] = held[index].V;
            }

            /* Packed RGBA, unpacked by component at the other end */
            var color = read.VertexColors is { Length: > 0 } held2 && index < held2[0].Colors.Length
                ? held2[0].Colors[index]
                : new FColor(255, 255, 255, 255);

            colors[index] = ((uint)color.R << 24) | ((uint)color.G << 16) | ((uint)color.B << 8) | color.A;
        }

        var sections = new List<StaticSection>(read.Sections.Length);

        foreach (var section in read.Sections)
        {
            if (!section.IsValid) continue;

            sections.Add(new StaticSection(
                section.MaterialIndex,
                section.FirstIndex,
                section.NumFaces,
                0,
                count - 1,
                true,
                section.CastShadow));
        }

        if (sections.Count == 0) return null;

        return new StaticLod(0, 1.0f, read.Indices, sections, new StaticVertices(count, numTexCoords, positions, normals, tangents, signs, uvs, colors), true);
    }
}
