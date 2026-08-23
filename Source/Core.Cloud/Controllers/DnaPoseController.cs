using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Rig;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Animation.CurveExpression;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

using Core.Cloud.Objects;

using Microsoft.AspNetCore.Mvc;

using System.Globalization;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: DNA Poses                                                                                                 */
/*                                                                                                                                  */
/* What each of a rig's controls does to the face, worked out here rather than by RigLogic.                                         */
/*                                                                                                                                  */
/* The part of a DNA that moves joints is a matrix per joint group: a row per joint attribute the group drives, a column per control */
/* that drives it. Driving one control on its own and leaving the rest alone is very nearly that control's column, so a pose per     */
/* control is close to a column read and nothing here needs the rig to run.                                                         */
/*                                                                                                                                  */
/* Heads cooked with optimized cooking on have no behavior layer left at all, only the compiled rig, so that gets read instead. Its  */
/* correctives do have to be evaluated rather than ignored, since one reading a single control would fire along with that control.   */
/*                                                                                                                                  */
/* Backporting asks for something else again: poses named by an older head's curves, each driving a handful of this rig's controls   */
/* at once. Several controls together is where the correctives really do fire, so those poses have to be evaluated rather than added */
/* up from single control ones, and that is why the mapping is resolved here rather than by whoever asked.                          */
/*                                                                                                                                  */
/* That is what lets an engine with no RigLogic still end up with the face: the poses arrive as numbers and are built into a pose    */
/* asset, which is the only shape those engines can animate a MetaHuman head in.                                                     */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    private sealed record DnaPoseJoint(int Index, float[] Values);
    private sealed record DnaPose(string Name, DnaPoseJoint[] Joints);

    /* A corrective: what it is worth, and the controls whose product it is. Named here because a DNA
     * does not name them, and whatever drives the poses has to be able to say which one it means. */
    private sealed record DnaCorrective(string Name, int Index, float Weight, string[] Inputs, string Expression);

    /* A curve the poses need that no animation carries, written so the engine can work it out */
    private sealed record DnaDriver(string Name, string Expression);

    /* A joint's three translations, three rotations and three scales, which is how a DNA writes
     * them whatever the rig does with them later */
    private const int DnaJointAttributes = 9;

    /* One pose per raw control, or one per curve of an older head when a mapping is named */
    [HttpGet("export/dnaposes")]
    public ActionResult GetDnaPoses(string? path, string? mapping, bool exact = false)
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

        if (FindDnaSource(profile.Provider, path) is not { } source) return NotFoundResponse;

        UDNA.ReadRig(source.Stream, out var definition, out var behavior);

        if (definition is null) return NotFoundResponse;
        var joints = definition.JointNames;
        var controls = definition.RawControlNames;

        if (joints.Length == 0 || controls.Length == 0) return NotFoundResponse;

        /* Whichever of the two the head still carries, asked the same question: drive these controls
         * by these amounts, and say what the joints did */
        if (!TryBuildDnaEvaluator(behavior, source.RigLogic, joints.Length, controls.Length, out var evaluate, out var inputCount, out var psds))
        {
            return NotFoundResponse;
        }

        /* The correctives, named after the controls they multiply so a curve can be written for each */
        var correctives = new DnaCorrective[psds.Length];

        for (var index = 0; index < psds.Length; index++)
        {
            var names = psds[index].Inputs
                .Select(input => input < controls.Length ? controls[input] : $"control{input}")
                .ToArray();

            var leaf = names.Select(name => name.SubstringAfterLast('.')).ToArray();
            var name = $"CTRL_psd.{string.Join('_', leaf)}";

            /* What PSDNetImpl::calculate does, written so the engine's own evaluator can do it:
             * every input clamped, multiplied together, scaled, and held at one. */
            var factors = names.Select(n => $"clamp({n.Replace('.', '_')}, 0, 1)");
            var weight = psds[index].Weight;

            var body = weight == 1.0f
                ? string.Join(" * ", factors)
                : $"{weight.ToString("R", CultureInfo.InvariantCulture)} * {string.Join(" * ", factors)}";

            correctives[index] = new DnaCorrective(name, controls.Length + index, weight, names, $"min(1, {body})");
        }

        /* Two names the same is a name that cannot be driven, so any repeat takes its index instead */
        {
            var seen = new Dictionary<string, int>();

            for (var index = 0; index < correctives.Length; index++)
            {
                var name = correctives[index].Name;

                if (seen.TryGetValue(name, out var count))
                {
                    seen[name] = count + 1;
                    correctives[index] = correctives[index] with { Name = $"{name}_{count + 1}" };
                }
                else
                {
                    seen[name] = 0;
                }
            }
        }

        /* What to bake: an older head's curves where a mapping was named, otherwise the rig's own
         * controls one at a time. A mapping that cannot be read, or none of whose controls this rig
         * has, falls back to the controls rather than to nothing: a head with no poses at all is a
         * worse answer than one posed by its own rig. */
        var drivers = new List<DnaDriver>();

        /* Exact asks for poses the rig can be rebuilt out of rather than approximated by. With a
         * mapping those are the older head's curves plus what it takes to answer them exactly;
         * without one they are the columns of the joint matrix, named by the rig's own controls. */
        List<(string Name, Dictionary<int, float> Drive)> plan =
            exact && !string.IsNullOrWhiteSpace(mapping)
                ? BuildBackportExactPlan(mapping, controls, correctives, controls.Length, out drivers)
                : exact || string.IsNullOrWhiteSpace(mapping)
                    ? []
                    : BuildBackportPlan(mapping, controls);

        var backported = plan.Count != 0;

        if (!backported)
        {
            plan = BuildControlPlan(controls);

            if (exact)
                foreach (var corrective in correctives)
                {
                    plan.Add((corrective.Name, new Dictionary<int, float> { [corrective.Index] = 1.0f }));
                    drivers.Add(new DnaDriver(corrective.Name, corrective.Expression));
                }
        }

        if (plan.Count == 0) return NotFoundResponse;

        var poses = new List<DnaPose>(plan.Count);

        foreach (var (name, drive) in plan)
        {
            var inputs = new float[inputCount];

            foreach (var (control, amount) in drive)
                if (control < inputs.Length) inputs[control] = amount;

            var byJoint = new Dictionary<int, float[]>();

            foreach (var (output, value) in evaluate(inputs, !exact))
            {
                var joint = output / DnaJointAttributes;

                if (value == 0.0f || joint < 0 || joint >= joints.Length) continue;

                if (!byJoint.TryGetValue(joint, out var values)) byJoint[joint] = values = new float[DnaJointAttributes];

                values[output % DnaJointAttributes] = value;
            }

            if (byJoint.Count == 0) continue;

            poses.Add(new DnaPose(name, [.. byJoint.OrderBy(pair => pair.Key).Select(pair => new DnaPoseJoint(pair.Key, pair.Value))]));
        }

        /* The pose the differences above are from, laid out the same way they are so the two
         * compose attribute by attribute. A DNA states no neutral scale, which is a scale of one. */
        var translations = definition.NeutralJointTranslations;
        var rotations = definition.NeutralJointRotations;

        var neutral = new List<float[]>(joints.Length);

        for (var index = 0; index < joints.Length; index++)
        {
            neutral.Add([
                translations.XS.Length > index ? translations.XS[index] : 0.0f,
                translations.YS.Length > index ? translations.YS[index] : 0.0f,
                translations.ZS.Length > index ? translations.ZS[index] : 0.0f,
                rotations.XS.Length > index ? rotations.XS[index] : 0.0f,
                rotations.YS.Length > index ? rotations.YS[index] : 0.0f,
                rotations.ZS.Length > index ? rotations.ZS[index] : 0.0f,
                1.0f,
                1.0f,
                1.0f
            ]);
        }

        /* Which joint each one hangs off, so a root can be told from the rest of them */
        var parents = definition.JointHierarchy;

        return new JsonResult(new { joints, parents, controls, backported, exact, drivers, attributes = DnaJointAttributes, neutral, poses });
    }

    /* Drive these controls by these amounts, and say what every joint attribute did.
     *
     * The behavior layer is a plain matrix and multiplies out directly. The compiled rig a cooked
     * head carries instead writes rotations as quaternions, spreading a joint over ten slots with
     * the fourth component left for the evaluator to fill, so everything from the scales onward
     * comes back one slot high and is put back before it leaves here. */
    private sealed record DnaPsd(float Weight, int[] Inputs);

    private static bool TryBuildDnaEvaluator(RawBehavior? behavior, byte[] rigLogic, int jointCount, int controlCount,
        out Func<float[], bool, Dictionary<int, float>> evaluate, out int inputCount, out DnaPsd[] psds)
    {
        if (behavior is not null && behavior.Joints.JointGroups.Length != 0)
        {
            var groups = behavior.Joints.JointGroups;

            /* The correctives, built the way PSDNetFactory does: every entry of the matrix sharing a
             * row belongs to one corrective, its columns are what that corrective reads, and its
             * values multiply together into the one weight the corrective carries.
             *
             * These matter the moment more than one control is driven at once, which is exactly what
             * a backported pose does. Leaving them out gets single control poses right and every
             * combination of them subtly wrong. */
            var psdCount = behavior.Controls.PSDCount;
            var psdWeight = new float[psdCount];
            var psdInputs = new List<ushort>[psdCount];

            {
                var matrix = behavior.Controls.PSDs;

                for (var entry = 0; entry < matrix.Rows.Length && entry < matrix.Columns.Length && entry < matrix.Values.Length; entry++)
                {
                    var psd = matrix.Rows[entry] - controlCount;

                    if (psd < 0 || psd >= psdCount) continue;

                    if (psdInputs[psd] is null)
                    {
                        psdInputs[psd] = [];
                        psdWeight[psd] = 1.0f;
                    }

                    psdInputs[psd].Add(matrix.Columns[entry]);
                    psdWeight[psd] *= matrix.Values[entry];
                }
            }

            inputCount = controlCount + psdCount;

            foreach (var group in groups)
                foreach (var input in group.InputIndices)
                    inputCount = Math.Max(inputCount, input + 1);

            var rawControlCount = controlCount;

            psds = new DnaPsd[psdCount];

            for (var psd = 0; psd < psdCount; psd++)
                psds[psd] = new DnaPsd(psdWeight[psd], psdInputs[psd] is { } reads ? [.. reads.Select(read => (int) read)] : []);

            evaluate = (inputs, correct) =>
            {
                if (!correct) return Multiply(inputs);

                /* Read from a snapshot, so one corrective never reads another's output */
                var clamped = new float[inputs.Length];

                for (var index = 0; index < inputs.Length; index++)
                    clamped[index] = Math.Clamp(inputs[index], 0.0f, 1.0f);

                for (var psd = 0; psd < psdCount; psd++)
                {
                    if (psdInputs[psd] is not { } reads) continue;

                    var value = psdWeight[psd];

                    foreach (var index in reads)
                        if (index < clamped.Length) value *= clamped[index];

                    var output = rawControlCount + psd;

                    if (output < inputs.Length) inputs[output] = Math.Min(1.0f, value);
                }

                return Multiply(inputs);
            };

            return true;

            Dictionary<int, float> Multiply(float[] inputs)
            {
                var deltas = new Dictionary<int, float>();

                foreach (var group in groups)
                {
                    var columns = group.InputIndices.Length;
                    var rows = group.OutputIndices.Length;

                    if (columns == 0 || rows == 0 || group.Values.Length < rows * columns) continue;

                    for (var column = 0; column < columns; column++)
                    {
                        var weight = group.InputIndices[column] < inputs.Length ? inputs[group.InputIndices[column]] : 0.0f;

                        if (weight == 0.0f) continue;

                        for (var row = 0; row < rows; row++)
                        {
                            var value = group.Values[row * columns + column] * weight;

                            if (value == 0.0f) continue;

                            var attribute = group.OutputIndices[row];

                            deltas[attribute] = deltas.GetValueOrDefault(attribute) + value;
                        }
                    }
                }

                return deltas;
            }
        }

        var state = RigLogicState.Read(rigLogic);

        if (!state.IsValid || jointCount == 0)
        {
            evaluate = (_, _) => [];
            inputCount = 0;
            psds = [];

            return false;
        }

        var slots = state.JointAttributeCount / jointCount;

        if (slots < DnaJointAttributes)
        {
            evaluate = (_, _) => [];
            inputCount = 0;
            psds = [];

            return false;
        }

        inputCount = state.InputCount;

        psds = new DnaPsd[state.Psds.Length];

        for (var psd = 0; psd < state.Psds.Length; psd++)
        {
            var reads = new List<int>();

            for (var i = state.Psds[psd].Offset; i < state.Psds[psd].Offset + state.Psds[psd].Size; i++)
                if (i >= 0 && i < state.PsdInputIndices.Length) reads.Add(state.PsdInputIndices[i]);

            psds[psd] = new DnaPsd(state.Psds[psd].Weight, [.. reads]);
        }

        evaluate = (inputs, correct) =>
        {
            if (correct) state.ApplyCorrectives(inputs);

            var deltas = new Dictionary<int, float>();

            foreach (var (output, value) in state.GetJointDeltas(inputs))
            {
                var slot = output % slots;

                if (slots > DnaJointAttributes && slot == 6) continue;

                var attribute = output / slots * DnaJointAttributes + (slots > DnaJointAttributes && slot > 6 ? slot - 1 : slot);

                deltas[attribute] = deltas.GetValueOrDefault(attribute) + value;
            }

            return deltas;
        };

        return true;
    }

    /* An older head's curves, and everything else the rig needs to answer them exactly.
     *
     * The joints are the matrix times the whole input vector, so anything driving that vector is a
     * pose. Three things drive it here, and all three are read straight off the matrix with the
     * correctives left switched off, since the correctives arrive as poses of their own:
     *
     *   the older head's curves   the controls that curve moves, before anything clamps them
     *   the correctives           one column each, driven by the product they stand for
     *   the overflow              one column each, negated, driven by whatever ran past one
     *
     * The overflow is what makes this exact rather than close. Poses scale and add, so two curves
     * driving one control hand it the sum where the rig hands it the sum clamped: subtracting that
     * control's own column by however far the sum ran over lands on the same face the rig does. */
    private List<(string Name, Dictionary<int, float> Drive)> BuildBackportExactPlan(
        string mapping, string[] controls, DnaCorrective[] correctives, int rawControlCount, out List<DnaDriver> drivers)
    {
        var plan = new List<(string, Dictionary<int, float>)>();

        drivers = [];

        var path = mapping.SubstringBefore('.');
        var profile = FindBaseProfileForPath(path, found: out var found);

        if (!found) return plan;

        if (LoadExportOfType<UCurveExpressionsDataAsset>(profile.Provider, path) is not { } asset ||
            asset.ExpressionData?.ExpressionMap is not { } map)
        {
            return plan;
        }

        var byName = new Dictionary<string, int>(controls.Length);

        for (var control = 0; control < controls.Length; control++)
            byName[controls[control].Replace('.', '_')] = control;

        /* Every expression the mapping writes for a control this rig has, with the text it was
         * compiled from, since the driving curves are written in terms of that same text */
        var written = new List<(int Control, string Name, FExpressionObject Expression, string Text)>();
        var sources = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (target, expression) in map)
        {
            if (!byName.TryGetValue(target.Text, out var control)) continue;

            written.Add((control, target.Text, expression, CurveExpressionText.Write(expression)));

            foreach (var element in expression.Expression)
                if (element.TryGet<FName>(out var constant)) sources.Add(constant.Text);
        }

        if (written.Count == 0) return plan;

        /* One pose an older curve: that curve the whole way up, and what it leaves the controls at */
        foreach (var source in sources)
        {
            var drive = new Dictionary<int, float>();

            foreach (var (control, _, expression, _) in written)
            {
                var value = CurveExpressionText.Evaluate(expression,
                    name => string.Equals(name, source, StringComparison.Ordinal) ? 1.0f : 0.0f);

                if (float.IsFinite(value) && value != 0.0f) drive[control] = value;
            }

            if (drive.Count != 0) plan.Add((source, drive));
        }

        /* One pose a corrective, driven by the product it stands for, written out of the older
         * head's curves so the whole thing reads from one place */
        var text = written.ToDictionary(entry => entry.Name, entry => entry.Text);

        foreach (var corrective in correctives)
        {
            var factors = new List<string>();
            var known = true;

            foreach (var input in corrective.Inputs)
            {
                if (!text.TryGetValue(input.Replace('.', '_'), out var expression)) { known = false; break; }

                factors.Add($"clamp({expression}, 0, 1)");
            }

            /* A corrective reading a control the mapping never writes can never fire */
            if (!known || factors.Count == 0) continue;

            var body = corrective.Weight == 1.0f
                ? string.Join(" * ", factors)
                : $"{corrective.Weight.ToString("R", CultureInfo.InvariantCulture)} * {string.Join(" * ", factors)}";

            plan.Add((corrective.Name, new Dictionary<int, float> { [corrective.Index] = 1.0f }));
            drivers.Add(new DnaDriver(corrective.Name, $"min(1, {body})"));
        }

        /* One pose a control that clamps, negated, driven by however far its sum ran past one */
        foreach (var (control, name, _, entry) in written)
        {
            if (Unclamped(entry) is not { } inner) continue;

            var over = $"CTRL_over.{name.SubstringAfter('_').Replace('_', '.').SubstringAfterLast('.')}";

            plan.Add((over, new Dictionary<int, float> { [control] = -1.0f }));
            drivers.Add(new DnaDriver(over, $"({inner}) - clamp(({inner}), 0, 1)"));
        }

        return plan;
    }

    /* What a clamped expression says before it clamps, or nothing when it does not clamp */
    private static string? Unclamped(string expression)
    {
        var text = expression.Trim();

        if (!text.StartsWith("clamp(", StringComparison.Ordinal) || !text.EndsWith(")", StringComparison.Ordinal)) return null;

        var body = text["clamp(".Length..^1];
        var depth = 0;

        for (var index = 0; index < body.Length; index++)
        {
            if (body[index] == '(') depth++;
            else if (body[index] == ')') depth--;
            else if (body[index] == ',' && depth == 0) return body[..index].Trim();
        }

        return null;
    }

    /* The rig's own controls, one at a time. A control is named with a dot between its group and
     * itself, which reads as a path everywhere a curve name is typed. */
    private static List<(string Name, Dictionary<int, float> Drive)> BuildControlPlan(string[] controls)
    {
        var plan = new List<(string, Dictionary<int, float>)>(controls.Length);

        for (var control = 0; control < controls.Length; control++)
            plan.Add((controls[control], new Dictionary<int, float> { [control] = 1.0f }));

        return plan;
    }

    /* An older head's poses, read out of the mapping that says what each of its curves is made of.
     *
     * A weight per control per curve, worked out by running the compiled expression rather than
     * reading the text it was written as, so a curve driven by three controls in different amounts
     * arrives as those three amounts. Driving the rig with exactly those is what makes the pose. */
    private List<(string Name, Dictionary<int, float> Drive)> BuildBackportPlan(string mapping, string[] controls)
    {
        var plan = new List<(string, Dictionary<int, float>)>();

        /* The mapping's own profile, not the head's. A mapping covers a whole family of heads and
         * lives in the base game where the head is off in a feature plugin, and only the profile
         * that owns a path can resolve the virtual form of it that the setting is written in. */
        var path = mapping.SubstringBefore('.');
        var profile = FindBaseProfileForPath(path, found: out var found);

        if (!found) return plan;

        if (LoadExportOfType<UCurveExpressionsDataAsset>(profile.Provider, path) is not { } asset ||
            asset.ExpressionData?.ExpressionMap is not { } map)
        {
            return plan;
        }

        /* A DNA writes a control as group and name with a dot between, and a mapping writes the same
         * control with an underscore, so they only meet once one is spelled the other's way */
        var byName = new Dictionary<string, int>(controls.Length);

        for (var control = 0; control < controls.Length; control++)
            byName[controls[control].Replace('.', '_')] = control;

        foreach (var (target, expression) in map)
        {
            var constants = expression.Expression
                .Select(element => element.TryGet<FName>(out var name) ? name.Text : null)
                .Where(name => name is not null)
                .Distinct()
                .ToArray()!;

            var drive = new Dictionary<int, float>();

            foreach (var (name, weight) in WeighConstants(expression, constants))
            {
                /* A mapping covers a whole family of heads, so a curve naming a control this DNA has
                 * not got is the mapping being broader than this face rather than a fault */
                if (!byName.TryGetValue(name, out var control)) continue;

                if (weight == 0.0f) continue;

                drive[control] = weight;
            }

            /* A curve none of whose controls this rig has is a pose that would come out as the
             * neutral, which is worse than not having it: it would overwrite whatever plays under */
            if (drive.Count == 0) continue;

            plan.Add((target.Text, drive));
        }

        plan.Sort((left, right) => string.CompareOrdinal(left.Item1, right.Item1));

        return plan;
    }

    /* The DNA stream, and the compiled rig where the cook kept one apart from it */
    private sealed record DnaSource(byte[] Stream, byte[] RigLogic);

    /* A head keeps its DNA one of two ways, and both have to be looked for.
     *
     * Newer ones put it in a package of its own as a UDNA and hang a user data off the mesh naming
     * it, which the package's exports lead to. Older ones keep it in the mesh, as a UDNAAsset whose
     * bytes are the stream, and that one is only reachable through the mesh's asset user data:
     * looking at exports alone finds nothing and the head silently gets no poses. */
    private static DnaSource? FindDnaSource(Core.Resources.Framework.Base.BaseProvider provider, string path)
    {
        if (provider.TryLoadPackage(path, out var package))
        {
            foreach (var export in package.GetExports())
            {
                if (FindDnaObject(export) is { } found) return found;
            }
        }

        if (LoadExportOfType<USkeletalMesh>(provider, path) is { AssetUserData: not null } skeletalMesh)
        {
            foreach (var userData in skeletalMesh.AssetUserData)
            {
                if (!userData.TryLoad(out var loaded) || loaded is null) continue;

                if (FindDnaObject(loaded) is { } found) return found;
            }
        }

        return null;
    }

    /* The DNA on an export, or the one its user data names */
    private static DnaSource? FindDnaObject(UObject export, int depth = 0)
    {
        if (depth > 4) return null;

        if (export is UDNA standalone)
        {
            var stream = standalone.ReadStream();

            return stream is { Length: > 0 } ? new DnaSource(stream, standalone.ReadRigLogicState()) : null;
        }

        /* The legacy shape keeps the rig in the DNA's own behavior layer, so there is no separate
         * blob and nothing for the compiled rig reader to do */
        if (export is UDNAAsset direct)
        {
            return direct.DNAData is { } data && data.Value.Length > 0 ? new DnaSource(data.Value, []) : null;
        }

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
