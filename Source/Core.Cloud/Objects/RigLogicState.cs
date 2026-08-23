/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Rig Logic State                                                                                                                  */
/*                                                                                                                                  */
/* The compiled rig a cooked MetaHuman head carries in place of its behavior layer. Cooking with optimized cooking on bakes RigLogic */
/* into the asset and empties the DNA's own bhvr section, so on those heads this is the only surviving description of the face.      */
/*                                                                                                                                  */
/* Written by RigLogicImpl::dump through terse, which is big endian with uint32 counts:                                              */
/*                                                                                                                                  */
/*     archive(config, *meta, *controls, *machineLearnedBehavior, *rbfBehavior, psdNet, *joints, blendShapes, animatedMaps)          */
/*                                                                                                                                  */
/* Only the joints are wanted, so everything ahead of them is read purely to be stepped over. The polymorphic members take their     */
/* type from meta's evaluator list and a null evaluator writes nothing at all, so that list has to be read rather than assumed.      */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Objects;

/* One block of the joint matrix: the rows it drives, the controls it reads, and where its values sit.
 *
 * RowCount is already the padded count, since the builder overwrites it once the padding is known,
 * so the values are exactly ColumnCount by RowCount. */
public sealed class RigLogicJointGroup
{
    public uint ValuesOffset;
    public uint InputIndicesOffset;
    public uint OutputIndicesOffset;
    public uint LodsOffset;
    public uint ValuesSize;
    public uint ColumnCount;
    public uint RowCount;

    /* Rows that carry a real joint attribute, the rest being padding the evaluator masks off */
    public uint LiveRowCount;
}

/* A corrective: the product of a few controls, scaled, standing in for a control of its own */
public sealed class RigLogicPsd
{
    public long Offset;
    public long Size;
    public float Weight;
}

/* How many rows of a group each LOD uses, and the block boundaries the evaluator rounds that to */
public sealed class RigLogicLodRegion
{
    public uint ColumnSize;
    public uint RowSize;
    public uint RowsPaddedToLastFullBlock;
    public uint RowsPaddedToSecondLastFullBlock;
}

public sealed class RigLogicState
{
    public ushort LodCount;
    public ushort GuiControlCount;
    public ushort RawControlCount;
    public ushort PsdControlCount;
    public ushort MlControlCount;
    public ushort RbfControlCount;
    public ushort JointGroupCount;
    public ushort JointAttributeCount;

    /* The correctives, and which controls each LOD lets drive and read them */
    public ushort[][] PsdInputLods = [];
    public ushort[][] PsdOutputLods = [];
    public ushort[] PsdInputIndices = [];
    public RigLogicPsd[] Psds = [];
    public ushort PsdMinIndex;

    /* Every non-zero value in the joint matrix, block packed */
    public float[] Values = [];

    /* Sub-matrix column to control, and row to joint attribute */
    public ushort[] InputIndices = [];
    public ushort[] OutputIndices = [];

    public RigLogicLodRegion[] LodRegions = [];
    public RigLogicJointGroup[] JointGroups = [];

    /* Rows per full block, and the multiple a trailing partial block pads up to. The builder takes
     * these from the widest vector the cooking machine had, so they are solved for rather than known. */
    public int BlockHeight;
    public int BlockPadding;

    public string? Error;

    public bool IsValid => Error is null && JointGroups.Length != 0;

    public static RigLogicState Read(byte[] blob)
    {
        var state = new RigLogicState();
        var at = 0;

        ushort U16() { var v = (ushort) ((blob[at] << 8) | blob[at + 1]); at += 2; return v; }
        uint U32() { var v = (uint) ((blob[at] << 24) | (blob[at + 1] << 16) | (blob[at + 2] << 8) | blob[at + 3]); at += 4; return v; }
        float F32() { var v = BitConverter.ToSingle([blob[at + 3], blob[at + 2], blob[at + 1], blob[at]], 0); at += 4; return v; }

        void SkipBytes(long count) => at = checked((int) (at + count));
        void SkipVector(int elementSize) => SkipBytes((long) U32() * elementSize);
        void SkipMatrix(int elementSize) { var rows = U32(); for (var r = 0; r < rows; r++) SkipVector(elementSize); }

        ulong U64() { ulong v = 0; for (var i = 0; i < 8; i++) v = (v << 8) | blob[at + i]; at += 8; return v; }

        ushort[] ReadU16Vector() { var n = U32(); var v = new ushort[n]; for (var i = 0; i < n; i++) v[i] = U16(); return v; }
        float[] ReadF32Vector() { var n = U32(); var v = new float[n]; for (var i = 0; i < n; i++) v[i] = F32(); return v; }
        ushort[][] ReadU16Matrix() { var n = U32(); var v = new ushort[n][]; for (var i = 0; i < n; i++) v[i] = ReadU16Vector(); return v; }

        try
        {
            /* Configuration: ten single byte settings then three pruning thresholds. The floating
             * point type is a build time choice and is not written. */
            SkipBytes(10 + 12);

            /* Coordinate system, rotation order, rotation direction */
            SkipBytes(12 + 4 + 12);

            /* RigMetadata's counts, in the order it declares them */
            state.LodCount = U16();
            state.GuiControlCount = U16();
            state.RawControlCount = U16();
            state.PsdControlCount = U16();
            state.MlControlCount = U16();
            state.RbfControlCount = U16();
            state.JointGroupCount = U16();
            state.JointAttributeCount = U16();
            U16();                                      /* blend shapes */
            U16();                                      /* animated maps */
            U16();                                      /* ml types */
            U16();                                      /* rbf solvers */
            U16();                                      /* twists */
            U16();                                      /* swings */

            /* Which concrete type each polymorphic member took when this was written, popped in the
             * order RigLogicImpl's factories run: ml, rbf, the four joint evaluators, blend shapes,
             * animated maps, and the psd net last. */
            var evaluators = ReadU16Vector();

            if (evaluators.Length < 9)
            {
                state.Error = $"expected at least 9 evaluators, got {evaluators.Length}";
                return state;
            }

            const ushort Concrete = 2;

            var machineLearned = evaluators[0];
            var rbf = evaluators[1];
            var bpcmJoints = evaluators[2];
            var psdNet = evaluators[8];

            /* Controls: the rows each LOD registers, the gui to raw mapping, the initial values */
            SkipMatrix(2);

            {
                var rangeMaps = U32();

                for (var m = 0; m < rangeMaps; m++)
                {
                    var ranges = U32();

                    for (var r = 0; r < ranges; r++)
                    {
                        SkipBytes(8);                   /* from, to */
                        SkipVector(2);                  /* rows */
                    }
                }

                SkipVector(2);                          /* intervals remaining */
                SkipVector(2);                          /* input indices */
                SkipVector(2);                          /* output indices */
                SkipVector(4);                          /* from values */
                SkipVector(4);                          /* to values */
                SkipVector(4);                          /* slope values */
                SkipVector(4);                          /* cut values */
                SkipBytes(4);                           /* input and output counts */
            }

            SkipVector(8);                              /* initial values: an index and a value each */

            /* A null evaluator writes nothing, which is the usual state of both on a face rig */
            if (machineLearned == Concrete)
            {
                state.Error = "machine learned behavior is present and is not supported";
                return state;
            }

            /* The mesh region counts sit outside the evaluator, so they are written either way */
            SkipVector(2);

            if (rbf == Concrete)
            {
                state.Error = "rbf behavior is present and is not supported";
                return state;
            }

            if (psdNet == Concrete)
            {
                state.PsdInputLods = ReadU16Matrix();
                state.PsdOutputLods = ReadU16Matrix();
                state.PsdInputIndices = ReadU16Vector();

                var psds = U32();
                state.Psds = new RigLogicPsd[psds];

                for (var p = 0; p < psds; p++)
                    state.Psds[p] = new RigLogicPsd { Offset = (long) U64(), Size = (long) U64(), Weight = F32() };

                state.PsdMinIndex = U16();
                U16();                                  /* the largest index, which the count implies */
            }

            if (bpcmJoints != Concrete)
            {
                state.Error = "the joints are not stored as a block packed matrix";
                return state;
            }

            state.Values = ReadF32Vector();
            state.InputIndices = ReadU16Vector();
            state.OutputIndices = ReadU16Vector();

            var regions = U32();
            state.LodRegions = new RigLogicLodRegion[regions];

            for (var r = 0; r < regions; r++)
            {
                var region = new RigLogicLodRegion { ColumnSize = U32() };

                SkipBytes(8);                           /* the column size aligned to 4 and to 8 */

                region.RowSize = U32();
                region.RowsPaddedToLastFullBlock = U32();
                region.RowsPaddedToSecondLastFullBlock = U32();

                state.LodRegions[r] = region;
            }

            SkipVector(2);                              /* output rotation indices */
            SkipVector(2);                              /* output rotation lods */

            var groups = U32();

            if (groups != state.JointGroupCount)
            {
                state.Error = $"read {groups} joint groups where the metadata says {state.JointGroupCount}";
                return state;
            }

            state.JointGroups = new RigLogicJointGroup[groups];

            for (var g = 0; g < groups; g++)
            {
                var group = new RigLogicJointGroup
                {
                    ValuesOffset = U32(),
                    InputIndicesOffset = U32(),
                    OutputIndicesOffset = U32(),
                    LodsOffset = U32()
                };

                SkipBytes(8);                           /* the rotation index and rotation lod offsets */

                group.ValuesSize = U32();
                group.ColumnCount = U32();
                group.RowCount = U32();

                if (group.ColumnCount != 0 && group.ValuesSize != group.ColumnCount * group.RowCount)
                {
                    state.Error = $"joint group {g} holds {group.ValuesSize} values for a {group.ColumnCount} by {group.RowCount} matrix";
                    return state;
                }

                /* The first LOD sees every row that carries a real attribute; the rest is padding */
                group.LiveRowCount = group.LodsOffset < regions ? state.LodRegions[group.LodsOffset].RowSize : group.RowCount;

                state.JointGroups[g] = group;
            }

            state.ResolveBlockHeight();

            return state;
        }
        catch (Exception exception)
        {
            state.Error = exception.Message;
            return state;
        }
    }

    /* The block height is whatever the cooking machine's widest vector was, twice its lane count,
     * and is never written down. Every LOD region's two block boundaries were derived from it
     * though, so replaying that derivation for each candidate leaves only the one that was used. */
    private void ResolveBlockHeight()
    {
        var fits = new List<int>();

        foreach (var height in new[] { 8, 16 })
        {
            var agrees = true;

            foreach (var group in JointGroups)
            {
                for (var lod = 0; lod < LodCount && agrees; lod++)
                {
                    var index = group.LodsOffset + lod;

                    if (index >= LodRegions.Length) continue;

                    var region = LodRegions[index];
                    var view = PaddedBlockView(region.RowSize, group.RowCount, (uint) height, (uint) (height / 2));

                    agrees = view.Last == region.RowsPaddedToLastFullBlock && view.SecondLast == region.RowsPaddedToSecondLastFullBlock;
                }

                if (!agrees) break;
            }

            if (agrees) fits.Add(height);
        }

        if (fits.Count != 1)
        {
            Error = fits.Count == 0
                ? "no block height reproduces the lod regions"
                : $"the lod regions do not tell {string.Join(" and ", fits)} apart";

            return;
        }

        BlockHeight = fits[0];
        BlockPadding = fits[0] / 2;
    }

    /* PaddedBlockView's constructor, which is what wrote the two boundaries into every LOD region */
    private static (uint Last, uint SecondLast) PaddedBlockView(uint viewRows, uint paddedRows, uint height, uint padding)
    {
        var endsWithPadToBlock = paddedRows % height == padding;
        var boundaryInLastPadToRows = paddedRows - viewRows < padding;
        var boundaryInLastPadToBlock = endsWithPadToBlock && boundaryInLastPadToRows;

        var target = boundaryInLastPadToBlock ? padding : height;
        var paddedViewRows = (viewRows + target - 1) / target * target;

        var maskOffLastBlock = paddedViewRows != viewRows && !boundaryInLastPadToBlock;
        var last = paddedViewRows - paddedViewRows % height;

        return (last, maskOffLastBlock && last >= height ? last - height : last);
    }

    /* Where a group's value for a given row and column lives.
     *
     * Rows are cut into blocks of BlockHeight, each stored column major inside itself, so walking a
     * column means hopping block to block rather than striding by a fixed amount. A trailing block
     * shorter than that is padded up to BlockPadding, which the row count already accounts for. */
    private long ValueIndex(RigLogicJointGroup group, uint row, uint column)
    {
        var height = (uint) BlockHeight;
        var full = group.RowCount % height == BlockPadding ? group.RowCount - (uint) BlockPadding : group.RowCount;

        if (row < full) return group.ValuesOffset + (long) (row / height) * group.ColumnCount * height + (long) column * height + row % height;

        return group.ValuesOffset + (long) full * group.ColumnCount + (long) column * (group.RowCount - full) + (row - full);
    }

    /* The rig's whole input vector: the raw controls first, then the correctives derived from them */
    public int InputCount => RawControlCount + PsdControlCount + MlControlCount + RbfControlCount;

    /* Fill in the correctives a set of raw control values implies, the way PSDNetImpl::calculate does */
    public void ApplyCorrectives(float[] inputs, int lod = 0)
    {
        if (lod >= PsdInputLods.Length || lod >= PsdOutputLods.Length) return;

        var clamped = new float[inputs.Length];

        foreach (var index in PsdInputLods[lod])
            if (index < inputs.Length) clamped[index] = Math.Clamp(inputs[index], 0.0f, 1.0f);

        foreach (var index in PsdOutputLods[lod])
        {
            var psd = index - PsdMinIndex;

            if (psd < 0 || psd >= Psds.Length || index >= inputs.Length) continue;

            var value = Psds[psd].Weight;

            for (var i = Psds[psd].Offset; i < Psds[psd].Offset + Psds[psd].Size; i++)
                if (i >= 0 && i < PsdInputIndices.Length) value *= clamped[PsdInputIndices[i]];

            inputs[index] = Math.Min(1.0f, value);
        }
    }

    /* Multiply the joint matrix by an input vector, giving a joint attribute index to delta map */
    public Dictionary<int, float> GetJointDeltas(float[] inputs)
    {
        var deltas = new Dictionary<int, float>();

        if (!IsValid) return deltas;

        foreach (var group in JointGroups)
        {
            if (group.ColumnCount == 0 || group.LiveRowCount == 0) continue;

            for (var column = 0u; column < group.ColumnCount; column++)
            {
                var inputAt = group.InputIndicesOffset + column;

                if (inputAt >= InputIndices.Length) break;

                var control = InputIndices[inputAt];
                var weight = control < inputs.Length ? inputs[control] : 0.0f;

                if (weight == 0.0f) continue;

                for (var row = 0u; row < group.LiveRowCount; row++)
                {
                    var outputAt = group.OutputIndicesOffset + row;
                    var valueAt = ValueIndex(group, row, column);

                    if (outputAt >= OutputIndices.Length || valueAt < 0 || valueAt >= Values.Length) break;

                    var value = Values[valueAt] * weight;

                    if (value == 0.0f) continue;

                    var attribute = OutputIndices[outputAt];

                    deltas[attribute] = deltas.GetValueOrDefault(attribute) + value;
                }
            }
        }

        return deltas;
    }

    /* What one raw control does to the joints when driven on its own.
     *
     * Anything the correctives add is part of that, since a corrective that reads only this control
     * fires with it, which is the whole of what the rig would do for this pose. */
    public Dictionary<int, float> GetControlDeltas(int control, int lod = 0)
    {
        var inputs = new float[InputCount];

        if (control < 0 || control >= inputs.Length) return [];

        inputs[control] = 1.0f;

        ApplyCorrectives(inputs, lod);

        return GetJointDeltas(inputs);
    }
}
