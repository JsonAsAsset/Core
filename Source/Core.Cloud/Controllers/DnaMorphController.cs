using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.Utils;

using Core.Cloud.Objects;

using Microsoft.AspNetCore.Mvc;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: DNA Morph Targets                                                                                         */
/*                                                                                                                                  */
/* The same faces the DNA poses make, written as where every vertex ends up rather than as what the joints did.                      */
/*                                                                                                                                  */
/* A pose asset cannot hold this rig faithfully. It accumulates rotation by multiplying quaternions where the rig sums euler angles  */
/* and converts once, and those two stop agreeing as the angle grows: this face turns lips, eyelids and tongue by forty degrees on a */
/* single curve, which is where it shows. Translation and scale it accumulates by adding, and those stay exact.                      */
/*                                                                                                                                  */
/* Morph targets are vertex offsets, and offsets add. Skinning the posed skeleton once here and handing over the difference moves    */
/* the problem to where the arithmetic is linear, which is the whole reason to do it this way.                                       */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    /* Deliberately the shape the morph target export already speaks, so nothing new reads it */
    private sealed record DnaMorphLod(int Index, int Count, uint[] SourceIndices, float[] PositionDeltas, float[] TangentZDeltas, int[] SectionIndices);
    private sealed record DnaMorph(string Name, List<DnaMorphLod> Lods);

    /* A vertex only counts as moved once it moves further than the mesh's own precision */
    private const float DnaMorphThreshold = 0.0001f;

    /* One morph target per pose the DNA makes */
    [HttpGet("export/dnamorphs")]
    /* Every LOD by default: a morph that only exists on the first one stops deforming the moment
     * the head is far enough away to draw another */
    public ActionResult GetDnaMorphs(string? path, string? mapping, int lods = 0)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new
        {
            errorCode = "cloud.dnamorphs.no_path",
            errorMessage = "No asset supplied",
            numericErrorCode = 1005
        });

        path = path.SubstringBefore('.');

        var profile = FindBaseProfileForPath(path, found: out var found);
        if (!found) return NotFoundResponse;

        if (FindDnaSource(profile.Provider, path) is not { } source) return NotFoundResponse;

        UDNA.ReadRig(source.Stream, out var definition, out var behavior);

        if (definition is null) return NotFoundResponse;

        var joints = definition.JointNames;
        var controls = definition.RawControlNames;

        if (joints.Length == 0 || controls.Length == 0) return NotFoundResponse;

        if (LoadExportOfType<USkeletalMesh>(profile.Provider, path) is not { } skeletalMesh ||
            skeletalMesh.ReferenceSkeleton is not { } referenceSkeleton ||
            skeletalMesh.LODModels is not { Length: > 0 } lodModels)
        {
            return NotFoundResponse;
        }

        if (!TryBuildDnaEvaluator(behavior, source.RigLogic, joints.Length, controls.Length, out var evaluate, out var inputCount, out _))
        {
            return NotFoundResponse;
        }


        /* One morph an older head's curve where a mapping says what those curves do to this rig,
         * otherwise one a control. Read forward through the mapping, the same as the poses.
         * */
        var plan = new List<(string Name, Dictionary<int, float> Drive)>();

        if (!string.IsNullOrWhiteSpace(mapping) && TryReadMapping(mapping, controls, out var written, out var sources))
        {
            plan = BuildLegacyPlan(written, sources);
        }

        var backported = plan.Count != 0;

        if (!backported) plan = BuildControlPlan(controls);


        /* The skeleton the mesh is bound at, and where the DNA's joints sit in it */
        var boneInfo = referenceSkeleton.FinalRefBoneInfo;
        var bonePose = referenceSkeleton.FinalRefBonePose;

        var boneOfJoint = new int[joints.Length];

        for (var joint = 0; joint < joints.Length; joint++)
        {
            boneOfJoint[joint] = -1;

            for (var bone = 0; bone < boneInfo.Length; bone++)
                if (string.Equals(boneInfo[bone].Name.Text, joints[joint], StringComparison.OrdinalIgnoreCase))
                {
                    boneOfJoint[joint] = bone;
                    break;
                }
        }

        var rest = ComposeComponentSpace(boneInfo, bonePose, null);

        var morphs = new List<DnaMorph>(plan.Count);
        var lodCount = lods <= 0 ? lodModels.Length : Math.Clamp(lods, 1, lodModels.Length);

        /* Reused rather than made per joint. A buffer taken from the stack inside these loops is not
         * given back until the whole request returns, and a joint's worth of it times every joint
         * times every pose is far more stack than there is. */
        var attribute = new float[DnaJointAttributes];

        foreach (var (name, drive) in plan)
        {
            var inputs = new float[inputCount];

            foreach (var (control, amount) in drive)
                if (control < inputs.Length) inputs[control] = amount;

            var deltas = evaluate(inputs, true);

            var local = new FTransform?[boneInfo.Length];

            for (var joint = 0; joint < joints.Length; joint++)
            {
                var bone = boneOfJoint[joint];

                if (bone < 0 || bone >= bonePose.Length) continue;

                var moved = false;

                for (var slot = 0; slot < DnaJointAttributes; slot++)
                {
                    attribute[slot] = deltas.TryGetValue(joint * DnaJointAttributes + slot, out var value) ? value : 0.0f;

                    if (attribute[slot] != 0.0f) moved = true;
                }

                if (moved) local[bone] = ApplyDnaJoint(bonePose[bone], attribute);
            }

            var posed = ComposeComponentSpace(boneInfo, bonePose, local);

            var lodList = new List<DnaMorphLod>(lodCount);

            for (var lod = 0; lod < lodCount; lod++)
            {
                if (BuildDnaMorphLod(lodModels[lod], lod, rest, posed) is { } built) lodList.Add(built);
            }

            if (lodList.Count != 0) morphs.Add(new DnaMorph(name, lodList));
        }

        return new JsonResult(new { backported, morphs });
    }

    /* Every bone's place in the mesh, walked down the hierarchy. Local overrides stand in for the
     * bones a pose moved, and the rest keep the pose the mesh is bound at. */
    private static FTransform[] ComposeComponentSpace(FMeshBoneInfo[] boneInfo, FTransform[] bonePose, FTransform?[]? local)
    {
        var component = new FTransform[boneInfo.Length];

        for (var bone = 0; bone < boneInfo.Length; bone++)
        {
            var at = local is not null && local[bone] is { } moved ? moved : bonePose[bone];
            var parent = boneInfo[bone].ParentIndex;

            component[bone] = parent >= 0 && parent < bone ? at * component[parent] : at;
        }

        return component;
    }

    /* A joint read straight off the DNA: translation as it stands, rotation as a rotator of
     * (-ry, rz, -rx).
     *
     * Not the signs the RigLogic anim node uses. That node reads (x, -y, z) and (-ry, -rz, rx), and
     * only lands right because the importer feeding it negates translation Y, rotation X and
     * rotation Z on the way in. Nothing negates anything here, so the reading has to be the raw one
     * or every joint moves the opposite way it should. */
    private static FTransform ApplyDnaJoint(FTransform bind, ReadOnlySpan<float> delta)
    {
        var translation = bind.Translation + new FVector(delta[0], delta[1], delta[2]);

        var rotation = bind.Rotation * new FRotator(-delta[4], delta[5], -delta[3]).Quaternion();

        var scale = bind.Scale3D + new FVector(delta[6], delta[7], delta[8]);

        return new FTransform(rotation, translation, scale);
    }

    /* Where the skinned vertices land once the skeleton moves, as a delta each */
    private static DnaMorphLod? BuildDnaMorphLod(FStaticLODModel lodModel, int lodIndex, FTransform[] rest, FTransform[] posed)
    {
        var vertices = lodModel.VertexBufferGPUSkin?.VertsFloat as FSkelMeshVertexBase[]
            ?? lodModel.VertexBufferGPUSkin?.VertsHalf;

        if (vertices is null || vertices.Length == 0) return null;

        /* One matrix a bone: out of the pose the mesh is bound at, and into where the rig put it */
        var skin = new FMatrix[rest.Length];

        for (var bone = 0; bone < rest.Length; bone++)
            skin[bone] = rest[bone].ToMatrixWithScale().Inverse() * posed[bone].ToMatrixWithScale();

        var indices = new List<uint>();
        var offsets = new List<float>();
        var normals = new List<float>();
        var sections = new List<int>();

        for (var sectionIndex = 0; sectionIndex < lodModel.Sections.Length; sectionIndex++)
        {
            var section = lodModel.Sections[sectionIndex];
            var boneMap = section.BoneMap;

            if (boneMap is null || boneMap.Length == 0) continue;

            var first = (int) section.BaseVertexIndex;
            var last = Math.Min(first + section.NumVertices, vertices.Length);

            for (var vertex = first; vertex < last; vertex++)
            {
                if (vertices[vertex].Infs is not { } influences) continue;

                var position = (FVector) vertices[vertex].Pos;

                /* Normal[2] is the vertex normal; the other two are the tangent frame */
                var normal = vertices[vertex].Normal.Length > 2
                    ? (FVector) vertices[vertex].Normal[2]
                    : FVector.ZeroVector;

                var moved = FVector.ZeroVector;
                var turned = FVector.ZeroVector;
                var total = 0.0f;

                for (var i = 0; i < influences.BoneIndex.Length; i++)
                {
                    /* Left as they are stored rather than scaled, since the total divides them out
                     * below and that is the same answer whether they are eight bit or sixteen */
                    var weight = (float) influences.BoneWeight[i];

                    if (weight <= 0.0f) continue;

                    var mapped = influences.BoneIndex[i];

                    if (mapped >= boneMap.Length) continue;

                    var bone = boneMap[mapped];

                    if (bone >= skin.Length) continue;

                    var placed = skin[bone].TransformPosition(position);

                    moved += new FVector(placed.X, placed.Y, placed.Z) * weight;

                    /* The rig turns joints, so the normals turn with them. Carried by the direction
                     * alone rather than the full matrix, since a normal has no place to be. */
                    var faced = skin[bone].TransformVector(normal);

                    turned += new FVector(faced.X, faced.Y, faced.Z) * weight;

                    total += weight;
                }

                if (total <= 0.0f) continue;

                var delta = moved / total - position;

                if (Math.Abs(delta.X) < DnaMorphThreshold && Math.Abs(delta.Y) < DnaMorphThreshold && Math.Abs(delta.Z) < DnaMorphThreshold &&
                    (total <= 0.0f || (turned / total).GetSafeNormal().Equals(normal.GetSafeNormal(), DnaMorphThreshold)))
                {
                    continue;
                }

                var faceDelta = FVector.ZeroVector;

                if (!normal.IsNearlyZero() && !turned.IsNearlyZero())
                {
                    var was = normal.GetSafeNormal();
                    var now = (turned / total).GetSafeNormal();

                    faceDelta = now - was;
                }

                indices.Add((uint) vertex);
                offsets.Add((float) delta.X);
                offsets.Add((float) delta.Y);
                offsets.Add((float) delta.Z);
                normals.Add((float) faceDelta.X);
                normals.Add((float) faceDelta.Y);
                normals.Add((float) faceDelta.Z);
                sections.Add(sectionIndex);
            }
        }

        if (indices.Count == 0) return null;

        return new DnaMorphLod(lodIndex, indices.Count, [.. indices], [.. offsets], [.. normals], [.. sections]);
    }
}
