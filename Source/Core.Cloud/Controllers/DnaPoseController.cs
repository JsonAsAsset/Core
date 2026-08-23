using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

using Core.Cloud.Objects;

using Microsoft.AspNetCore.Mvc;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: DNA Poses                                                                                                 */
/*                                                                                                                                  */
/* What each of a rig's controls does to the face, worked out here rather than by RigLogic.                                          */
/*                                                                                                                                  */
/* The part of a DNA that moves joints is a matrix per joint group: a row per joint attribute the group drives, a column per control */
/* that drives it. Driving one control on its own and leaving the rest alone is that control's column, so a pose per control is a    */
/* column read rather than an evaluation, and nothing here needs the rig to run.                                                     */
/*                                                                                                                                  */
/* That is what lets an engine with no RigLogic still end up with the face: the poses arrive as numbers and are built into a pose    */
/* asset, which is the only shape those engines can animate a MetaHuman head in.                                                     */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    private sealed record DnaPoseJoint(int Index, float[] Values);
    private sealed record DnaPose(string Name, DnaPoseJoint[] Joints);

    /* One pose per raw control, as what it does to the joints it touches */
    [HttpGet("export/dnaposes")]
    public ActionResult GetDnaPoses(string? path)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new
        {
            errorCode = "cloud.dnaposes.no_path",
            errorMessage = "No asset supplied",
            numericErrorCode = 1005
        });

        path = path.SubstringBefore('.');

        var profile = FindBaseProfileForPath(path, found: out var found);
        if (!found) return NotFoundResponse;

        if (!profile.Provider.TryLoadPackage(path, out var package)) return NotFoundResponse;

        UDNA? dna = null;

        foreach (var export in package.GetExports())
        {
            dna = FindDnaObject(export);

            if (dna is not null) break;
        }

        if (dna is null || !dna.ReadRig() || dna.Definition is null || dna.Behavior is null)
        {
            return NotFoundResponse;
        }

        var definition = dna.Definition;
        var joints = definition.JointNames;
        var controls = definition.RawControlNames;

        if (joints.Length == 0 || controls.Length == 0) return NotFoundResponse;

        /* Nine attributes a joint, or ten once rotations are written as quaternions. The rig says
         * which by how many rows it has for the joints it names. */
        var attributes = dna.Behavior.Joints.RowCount / Math.Max(1, joints.Length);
        if (attributes <= 0) return NotFoundResponse;

        /* Gathered per control, since a control's columns are spread across the groups */
        var byControl = new Dictionary<int, Dictionary<int, float[]>>();

        foreach (var group in dna.Behavior.Joints.JointGroups)
        {
            var columns = group.InputIndices.Length;
            var rows = group.OutputIndices.Length;

            if (columns == 0 || rows == 0 || group.Values.Length < rows * columns) continue;

            for (var column = 0; column < columns; column++)
            {
                var control = group.InputIndices[column];

                for (var row = 0; row < rows; row++)
                {
                    var value = group.Values[row * columns + column];

                    if (value == 0.0f) continue;

                    var output = group.OutputIndices[row];
                    var joint = output / attributes;
                    var attribute = output % attributes;

                    if (joint < 0 || joint >= joints.Length) continue;

                    if (!byControl.TryGetValue(control, out var jointValues))
                    {
                        byControl[control] = jointValues = new Dictionary<int, float[]>();
                    }

                    if (!jointValues.TryGetValue(joint, out var values))
                    {
                        jointValues[joint] = values = new float[attributes];
                    }

                    values[attribute] = value;
                }
            }
        }

        var poses = new List<DnaPose>(byControl.Count);

        foreach (var (control, jointValues) in byControl.OrderBy(pair => pair.Key))
        {
            if (control < 0 || control >= controls.Length) continue;

            poses.Add(new DnaPose(
                controls[control],
                [.. jointValues.OrderBy(pair => pair.Key).Select(pair => new DnaPoseJoint(pair.Key, pair.Value))]
            ));
        }

        /* The pose the differences above are from */
        var translations = definition.NeutralJointTranslations;
        var rotations = definition.NeutralJointRotations;

        var neutral = new List<float[]>(joints.Length);

        for (var index = 0; index < joints.Length; index++)
        {
            neutral.Add([
                translations.Xs.Length > index ? translations.Xs[index] : 0.0f,
                translations.Ys.Length > index ? translations.Ys[index] : 0.0f,
                translations.Zs.Length > index ? translations.Zs[index] : 0.0f,
                rotations.Xs.Length > index ? rotations.Xs[index] : 0.0f,
                rotations.Ys.Length > index ? rotations.Ys[index] : 0.0f,
                rotations.Zs.Length > index ? rotations.Zs[index] : 0.0f
            ]);
        }

        return new JsonResult(new { joints, attributes, neutral, poses });
    }

    /* The DNA on an export, or the one its user data names */
    private static UDNA? FindDnaObject(UObject export, int depth = 0)
    {
        if (depth > 4) return null;
        if (export is UDNA dna) return dna;

        if (export.TryGetValue<UObject>(out var referenced, "DNAAsset") && referenced is not null)
        {
            return FindDnaObject(referenced, depth + 1);
        }

        if (export.TryGetValue<FPackageIndex>(out var index, "DNAAsset") &&
            index.TryLoad<UObject>(out var loaded) && loaded is not null)
        {
            return FindDnaObject(loaded, depth + 1);
        }

        return null;
    }
}
