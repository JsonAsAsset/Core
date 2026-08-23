using Serilog;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* RigLogic -> DNA behavior                                                                                                         */
/*                                                                                                                                  */
/* Epic's optimized cook throws the behavior layer away and bakes the rig into a RigLogic dump instead, so a UE6 head ships a DNA    */
/* that has the joints and the neutral pose and nothing to drive them with. Everything the discarded layer held is still in the      */
/* dump though, in the layout RigLogic runs it from, and that layout is a rearrangement rather than a reduction:                     */
/*                                                                                                                                  */
/*   - the gui to raw conditionals are the same six arrays the DNA writes                                                           */
/*   - the PSD network is the same matrix, split into a per PSD offset and length instead of a row index                            */
/*   - the joint matrices are the same values, block transposed for SIMD and padded out to a vector width                           */
/*                                                                                                                                  */
/* The one thing that genuinely changed is rotation. RigLogic can hand a joint's rotation back as a quaternion, and when it does it  */
/* widens a joint from nine attributes to ten and shifts scale up by one. That is an output stage though: EulerAnglesToQuaternions   */
/* converts after the matrix multiply, so the stored values are still the euler deltas the DNA had, and only the indices moved.      */
/* remapOutputIndicesForQuaternions is what moved them, and this undoes exactly that.                                               */
/*                                                                                                                                  */
/* So the behavior layer can be put back as it was, and a DNA carrying it is one an older RigLogic reads without knowing any of this */
/* happened. */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Objects;

public static class RigLogicDnaBuilder
{
    /* ~~~ Reading the dump ~~~ */

    private sealed class Reader(byte[] data)
    {
        public int Position;

        public byte U8() => data[Position++];

        public ushort U16()
        {
            var value = (ushort) ((data[Position] << 8) | data[Position + 1]);
            Position += 2;

            return value;
        }

        public uint U32()
        {
            var value = ((uint) data[Position] << 24) | ((uint) data[Position + 1] << 16) |
                        ((uint) data[Position + 2] << 8) | data[Position + 3];
            Position += 4;

            return value;
        }

        public ulong U64()
        {
            ulong value = 0;

            for (var i = 0; i < 8; i++)
            {
                value = (value << 8) | data[Position + i];
            }

            Position += 8;

            return value;
        }

        public float F32()
        {
            Span<byte> bytes = [data[Position + 3], data[Position + 2], data[Position + 1], data[Position]];
            Position += 4;

            return BitConverter.ToSingle(bytes);
        }

        public ushort[] U16Array()
        {
            var values = new ushort[U32()];

            for (var i = 0; i < values.Length; i++)
            {
                values[i] = U16();
            }

            return values;
        }

        public float[] F32Array()
        {
            var values = new float[U32()];

            for (var i = 0; i < values.Length; i++)
            {
                values[i] = F32();
            }

            return values;
        }

        public ushort[][] U16Matrix()
        {
            var rows = new ushort[U32()][];

            for (var i = 0; i < rows.Length; i++)
            {
                rows[i] = U16Array();
            }

            return rows;
        }

        public void Skip(int count) => Position += count;

        public void SkipArray(int elementSize)
        {
            /* The count has to be read into a local first: a compound assignment captures Position
             * before the right hand side runs, so reading the count inline would throw away the
             * four bytes the count itself occupies. */
            var count = U32();

            Position += checked((int) (count * (uint) elementSize));
        }

        public void SkipMatrix(int elementSize)
        {
            var rows = U32();

            for (var i = 0u; i < rows; i++)
            {
                SkipArray(elementSize);
            }
        }
    }

    private sealed class Conditionals
    {
        public ushort[] InputIndices = [];
        public ushort[] OutputIndices = [];
        public float[] FromValues = [];
        public float[] ToValues = [];
        public float[] SlopeValues = [];
        public float[] CutValues = [];
    }

    private static Conditionals ReadConditionals(Reader reader)
    {
        /* The range maps are an acceleration structure RigLogic builds for itself, the DNA has no
         * equivalent and does not need one */
        var maps = reader.U32();

        for (var map = 0u; map < maps; map++)
        {
            var ranges = reader.U32();

            for (var range = 0u; range < ranges; range++)
            {
                reader.Skip(8);
                reader.SkipArray(2);
            }
        }

        reader.SkipArray(2); /* intervalsRemaining */

        var table = new Conditionals
        {
            InputIndices = reader.U16Array(),
            OutputIndices = reader.U16Array(),
            FromValues = reader.F32Array(),
            ToValues = reader.F32Array(),
            SlopeValues = reader.F32Array(),
            CutValues = reader.F32Array()
        };

        reader.Skip(4); /* inputCount, outputCount */

        return table;
    }

    private sealed class JointGroup
    {
        public uint ValuesOffset;
        public uint InputIndicesOffset;
        public uint OutputIndicesOffset;
        public uint LodsOffset;
        public uint ValuesSize;
        public uint ColCount;
        public uint RowCount;
    }

    /* ~~~ Writing the DNA ~~~ */

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();

        public long Length => _stream.Length;

        public void U16(ushort value)
        {
            _stream.WriteByte((byte) (value >> 8));
            _stream.WriteByte((byte) value);
        }

        public void U32(uint value)
        {
            _stream.WriteByte((byte) (value >> 24));
            _stream.WriteByte((byte) (value >> 16));
            _stream.WriteByte((byte) (value >> 8));
            _stream.WriteByte((byte) value);
        }

        public void F32(float value)
        {
            var bytes = BitConverter.GetBytes(value);

            _stream.WriteByte(bytes[3]);
            _stream.WriteByte(bytes[2]);
            _stream.WriteByte(bytes[1]);
            _stream.WriteByte(bytes[0]);
        }

        public void U16Array(IReadOnlyList<ushort> values)
        {
            U32((uint) values.Count);

            foreach (var value in values)
            {
                U16(value);
            }
        }

        public void F32Array(IReadOnlyList<float> values)
        {
            U32((uint) values.Count);

            foreach (var value in values)
            {
                F32(value);
            }
        }

        public void Raw(byte[] bytes, int start, int length) => _stream.Write(bytes, start, length);

        public byte[] ToArray() => _stream.ToArray();
    }

    /* An index entry is 4 bytes of id, the layer's own generation and version, then where it is and
     * how long it runs for */
    private readonly record struct Layer(string Id, ushort Generation, ushort Version, byte[] Payload);

    /* A DNA carrying the rig the cook baked out, at the file version an older RigLogic reads.
     * Null when the dump is not in the layout this knows how to take apart. */
    public static byte[]? Rebuild(byte[] dna, int dumpStart)
    {
        try
        {
            return Build(dna, dumpStart);
        }
        catch (Exception exception)
        {
            Log.Warning($"[Core.Cloud]: Could not rebuild a behavior layer from the rig: {exception.Message}");

            return null;
        }
    }

    private static byte[]? Build(byte[] dna, int dumpStart)
    {
        var reader = new Reader(dna) { Position = dumpStart };

        /* ~~~ Configuration ~~~ */
        reader.Skip(7); /* calculationType, six load flags */

        var translationType = reader.U8();
        var rotationType = reader.U8();
        var scaleType = reader.U8();

        reader.Skip(12); /* pruning thresholds */

        /* Nine attributes per joint is what a DNA behavior layer indexes against. Anything else is
         * RigLogic widening the output, and only quaternions do that. */
        var attributesPerJoint = translationType + rotationType + scaleType;
        var quaternionOutput = rotationType == 4;

        if (attributesPerJoint != 9 && !quaternionOutput)
        {
            Log.Warning($"[Core.Cloud]: Rig has {attributesPerJoint} attributes per joint, which is not a shape a DNA describes");

            return null;
        }

        /* ~~~ RigMetadata ~~~ */
        reader.Skip(28); /* coordinateSystem, rotationSequence, rotationSigns */

        var lodCount = reader.U16();
        reader.U16(); /* guiControlCount */
        var rawControlCount = reader.U16();
        var psdControlCount = reader.U16();
        var mlControlCount = reader.U16();
        var rbfControlCount = reader.U16();
        reader.U16(); /* jointGroupCount, the joints section carries its own */
        var jointAttributeCount = reader.U16();
        var blendShapeCount = reader.U16();
        var animatedMapCount = reader.U16();
        var mlTypeCount = reader.U16();
        var rbfSolverCount = reader.U16();
        var twistCount = reader.U16();
        var swingCount = reader.U16();

        reader.SkipArray(2); /* evaluators */

        if (mlControlCount != 0 || mlTypeCount != 0 || rbfControlCount != 0 || rbfSolverCount != 0 ||
            twistCount != 0 || swingCount != 0)
        {
            Log.Warning(
                "[Core.Cloud]: Rig uses machine learned, RBF or twist/swing behavior. Those are stored in " +
                "shapes this does not put back, so the behavior layer would come out incomplete.");

            return null;
        }

        /* ~~~ Controls ~~~ */
        reader.SkipMatrix(2); /* registeredControls */
        var guiToRaw = ReadConditionals(reader);
        reader.SkipArray(8); /* initialValues */

        /* ~~~ MachineLearnedBehavior (null evaluator) and RBFBehavior (null evaluator) ~~~ */
        reader.SkipMatrix(2); /* meshRegionCounts */

        /* ~~~ PSDNet ~~~ */
        reader.SkipMatrix(2); /* inputLODs */
        reader.SkipMatrix(2); /* outputLODs */

        var psdInputIndices = reader.U16Array();

        var psdCount = reader.U32();
        var psdOffsets = new ulong[psdCount];
        var psdSizes = new ulong[psdCount];
        var psdWeights = new float[psdCount];

        for (var i = 0u; i < psdCount; i++)
        {
            psdOffsets[i] = reader.U64();
            psdSizes[i] = reader.U64();
            psdWeights[i] = reader.F32();
        }

        var psdMinIndex = reader.U16();
        reader.U16(); /* psdMaxIndex */

        /* ~~~ Joints ~~~ */
        var valueCount = reader.U32();
        var values = new float[valueCount];

        for (var i = 0u; i < valueCount; i++)
        {
            values[i] = reader.F32();
        }

        var storageInputIndices = reader.U16Array();
        var storageOutputIndices = reader.U16Array();

        var lodRegionCount = reader.U32();
        var lodRows = new uint[lodRegionCount];

        for (var i = 0u; i < lodRegionCount; i++)
        {
            reader.Skip(12);              /* ColumnLOD: size, sizeAlignedTo4, sizeAlignedTo8 */
            lodRows[i] = reader.U32();    /* RowLOD.size, the rows this LOD actually uses */
            reader.Skip(8);               /* the two padded row counts, only the calculator needs those */
        }

        reader.SkipArray(2); /* outputRotationIndices */
        reader.SkipArray(2); /* outputRotationLODs */

        var groupCount = reader.U32();
        var groups = new JointGroup[groupCount];

        for (var i = 0u; i < groupCount; i++)
        {
            groups[i] = new JointGroup
            {
                ValuesOffset = reader.U32(),
                InputIndicesOffset = reader.U32(),
                OutputIndicesOffset = reader.U32(),
                LodsOffset = reader.U32()
            };

            reader.Skip(8); /* outputRotationIndicesOffset, outputRotationLODsOffset */

            groups[i].ValuesSize = reader.U32();
            groups[i].ColCount = reader.U32();
            groups[i].RowCount = reader.U32();
        }

        reader.SkipArray(4); /* neutralValues */
        reader.SkipMatrix(2); /* variableAttributeIndices */
        reader.SkipMatrix(2); /* jointIndices */
        reader.Skip(2);       /* jointGroupCount */

        /* ~~~ BlendShapes ~~~ */
        var blendShapeLods = reader.U16Array();
        var blendShapeInputs = reader.U16Array();
        var blendShapeOutputs = reader.U16Array();

        /* ~~~ AnimatedMaps ~~~ */
        var animatedMapLods = reader.U16Array();
        var animatedMapConditionals = ReadConditionals(reader);

        if (reader.Position != dna.Length)
        {
            Log.Warning($"[Core.Cloud]: Rig walked to {reader.Position} of {dna.Length}, so its layout is not the one this knows");

            return null;
        }

        /* ~~~ Put the matrices back the way a DNA holds them ~~~
         *
         * The stored matrix is block transposed: a block of rows is written column by column so the
         * calculator can stride down a column with one vector load. Undoing it is the same walk with
         * the assignment turned around. */
        var padTo = InferPadTo(groups, lodRows);

        if (padTo == 0)
        {
            Log.Warning("[Core.Cloud]: Could not tell what vector width the rig was built for");

            return null;
        }

        var blockHeight = padTo * 2;

        var jointGroups = new List<(ushort[] Lods, ushort[] Inputs, ushort[] Outputs, float[] Values, ushort[] Joints)>();
        var maxOutput = 0;

        for (var g = 0u; g < groupCount; g++)
        {
            var group = groups[g];

            var cols = (int) group.ColCount;
            var rows = (int) lodRows[group.LodsOffset]; /* LOD zero is every row the group has */

            if (cols == 0 || rows == 0) continue;

            var matrix = Deoptimize(values, (int) group.ValuesOffset, rows, cols, blockHeight, (int) padTo);

            var inputs = new ushort[cols];
            Array.Copy(storageInputIndices, (int) group.InputIndicesOffset, inputs, 0, cols);

            var outputs = new ushort[rows];

            for (var row = 0; row < rows; row++)
            {
                var stored = storageOutputIndices[group.OutputIndicesOffset + row];

                outputs[row] = quaternionOutput ? ToDnaAttributeIndex(stored) : stored;
                maxOutput = Math.Max(maxOutput, outputs[row]);
            }

            var lods = new ushort[lodCount];

            for (var lod = 0; lod < lodCount; lod++)
            {
                lods[lod] = (ushort) lodRows[group.LodsOffset + (uint) lod];
            }

            /* The joints a group touches, which the DNA states rather than implying */
            var joints = outputs.Select(index => (ushort) (index / 9)).Distinct().OrderBy(index => index).ToArray();

            jointGroups.Add((lods, inputs, outputs, matrix, joints));
        }

        /* ~~~ Behavior layer ~~~ */
        var behavior = new Writer();

        /* Controls */
        behavior.U16(psdControlCount);

        behavior.U16Array(guiToRaw.InputIndices);
        behavior.U16Array(guiToRaw.OutputIndices);
        behavior.F32Array(guiToRaw.FromValues);
        behavior.F32Array(guiToRaw.ToValues);
        behavior.F32Array(guiToRaw.SlopeValues);
        behavior.F32Array(guiToRaw.CutValues);

        /* The PSD network keeps one entry per PSD with a run of inputs behind it. The DNA writes the
         * same thing one cell at a time, so a PSD's output index is repeated for every input. */
        var psdRows = new List<ushort>();
        var psdColumns = new List<ushort>();
        var psdValues = new List<float>();

        for (var i = 0u; i < psdCount; i++)
        {
            var output = (ushort) (psdMinIndex + i);

            for (var j = 0ul; j < psdSizes[i]; j++)
            {
                psdRows.Add(output);
                psdColumns.Add(psdInputIndices[psdOffsets[i] + j]);
                psdValues.Add(psdWeights[i]);
            }
        }

        behavior.U16Array(psdRows);
        behavior.U16Array(psdColumns);
        behavior.F32Array(psdValues);

        /* Joints. The row count is every attribute the rig has, not the highest one that happens to
         * be driven: RigLogic sizes its output buffer from it. Quaternion output widened a joint to
         * ten attributes, and a DNA counts nine. */
        var jointCount = attributesPerJoint == 0 ? 0 : jointAttributeCount / attributesPerJoint;
        var rowCount = Math.Max(jointCount * 9, maxOutput + 1);

        behavior.U16((ushort) rowCount);
        behavior.U16((ushort) (rawControlCount + psdControlCount));

        behavior.U32((uint) jointGroups.Count);

        foreach (var (lods, inputs, outputs, groupValues, joints) in jointGroups)
        {
            behavior.U16Array(lods);
            behavior.U16Array(inputs);
            behavior.U16Array(outputs);
            behavior.F32Array(groupValues);
            behavior.U16Array(joints);
        }

        /* Blend shape channels */
        behavior.U16Array(blendShapeLods);
        behavior.U16Array(blendShapeInputs);
        behavior.U16Array(blendShapeOutputs);

        /* Animated maps */
        behavior.U16Array(animatedMapLods);
        behavior.U16Array(animatedMapConditionals.InputIndices);
        behavior.U16Array(animatedMapConditionals.OutputIndices);
        behavior.F32Array(animatedMapConditionals.FromValues);
        behavior.F32Array(animatedMapConditionals.ToValues);
        behavior.F32Array(animatedMapConditionals.SlopeValues);
        behavior.F32Array(animatedMapConditionals.CutValues);

        Log.Information(
            $"[Core.Cloud]: Rebuilt a behavior layer: {jointGroups.Count} joint group(s), " +
            $"{psdRows.Count} PSD cell(s), {guiToRaw.InputIndices.Length} conditional(s), " +
            $"{blendShapeCount} blend shape(s), {animatedMapCount} animated map(s)");

        return RewriteDna(dna, dumpStart, behavior.ToArray());
    }

    /* Which vector width the rig was built for, read off the padding it left behind. The row count a
     * group stores is its real row count rounded up to that width, so the width is whichever one
     * every group agrees on. */
    private static uint InferPadTo(JointGroup[] groups, uint[] lodRows)
    {
        foreach (var candidate in (uint[]) [4, 8, 16])
        {
            var fits = true;

            foreach (var group in groups)
            {
                if (group.ColCount == 0) continue;

                var rows = lodRows[group.LodsOffset];
                var padded = (rows + candidate - 1) / candidate * candidate;

                if (padded == group.RowCount && padded * group.ColCount == group.ValuesSize) continue;

                fits = false;

                break;
            }

            if (fits) return candidate;
        }

        return 0;
    }

    /* The inverse of bpcm::Optimizer::optimize, which is the same walk writing the other way.
     * Stride is one in every build of RigLogic there has been, so the two innermost loops it has
     * collapse into one here. */
    private static float[] Deoptimize(float[] source, int offset, int rows, int cols, uint blockHeight, int padTo)
    {
        var matrix = new float[rows * cols];

        var remainder = (int) (rows % blockHeight);
        var target = rows - remainder;
        var cursor = offset;

        for (var row = 0; row < target; row += (int) blockHeight)
        {
            for (var col = 0; col < cols; col++)
            {
                for (var index = 0; index < blockHeight; index++, cursor++)
                {
                    matrix[(row + index) * cols + col] = source[cursor];
                }
            }
        }

        if (remainder == 0) return matrix;

        /* The tail block is padded out to the vector width, and the padding is skipped rather than
         * read: it was never part of the matrix */
        var paddedBlockHeight = (remainder + padTo - 1) / padTo * padTo;

        for (var col = 0; col < cols; col++)
        {
            for (var index = 0; index < remainder; index++, cursor++)
            {
                matrix[(target + index) * cols + col] = source[cursor];
            }

            cursor += paddedBlockHeight - remainder;
        }

        return matrix;
    }

    /* remapOutputIndicesForQuaternions turned a nine attribute joint into a ten attribute one by
     * opening a slot for the quaternion's fourth component and pushing scale up past it. Going back
     * is the same step in reverse; the fourth component is never a row, so it never turns up here. */
    private static ushort ToDnaAttributeIndex(ushort index)
    {
        var joint = index / 10;
        var attribute = index % 10;

        return (ushort) (joint * 9 + (attribute < 6 ? attribute : attribute - 1));
    }

    /* ~~~ The DNA around it ~~~
     *
     * The definition layer is kept exactly as the cook wrote it, the behavior layer is the one that
     * was missing, and the layers a newer RigLogic added are dropped so the file is one an older one
     * recognises end to end. */
    private static byte[]? RewriteDna(byte[] dna, int dumpStart, byte[] behavior)
    {
        var reader = new Reader(dna);

        if (dna[0] != 'D' || dna[1] != 'N' || dna[2] != 'A') return null;

        reader.Position = 7;

        var count = reader.U32();
        var layers = new List<Layer>();

        var entries = new (string Id, ushort Generation, ushort Version, uint Offset, uint Size)[count];

        for (var i = 0u; i < count; i++)
        {
            var id = new string([(char) reader.U8(), (char) reader.U8(), (char) reader.U8(), (char) reader.U8()]);

            entries[i] = (id, reader.U16(), reader.U16(), reader.U32(), reader.U32());
        }

        /* Written at 2.3 with the layers 2.3 defines, which is the oldest shape that still carries
         * everything this rig uses and so the one the most engines read. 5.4's RigLogic stops at
         * 2.3 and knows only these five; everything newer still reads them, and the layers a later
         * version added (rbfb, rbfe, jbmd, twsw, mlbe, dsce) are all empty here anyway.
         *
         * 2.1 is deliberately not the target: RawBehavior writes section offsets at that version
         * and a flat payload at every other, so writing 2.3 keeps the layout this builds. */
        string[] known = ["desc", "defn", "bhvr", "geom", "mlbh"];

        foreach (var id in known)
        {
            var entry = entries.FirstOrDefault(candidate => candidate.Id == id);

            if (entry.Id is null) continue;

            if (id == "bhvr")
            {
                layers.Add(new Layer("bhvr", 1, 1, behavior));

                continue;
            }

            if (entry.Offset + entry.Size > dumpStart) return null;

            var payload = new byte[entry.Size];
            Array.Copy(dna, (int) entry.Offset, payload, 0, (int) entry.Size);

            layers.Add(new Layer(id, entry.Generation, entry.Version, payload));
        }

        var writer = new Writer();

        writer.Raw("DNA"u8.ToArray(), 0, 3);
        writer.U16(2);
        writer.U16(3);

        writer.U32((uint) layers.Count);

        var offset = (uint) (7 + 4 + layers.Count * 16);

        foreach (var layer in layers)
        {
            foreach (var character in layer.Id)
            {
                writer.Raw([(byte) character], 0, 1);
            }

            writer.U16(layer.Generation);
            writer.U16(layer.Version);
            writer.U32(offset);
            writer.U32((uint) layer.Payload.Length);

            offset += (uint) layer.Payload.Length;
        }

        foreach (var layer in layers)
        {
            writer.Raw(layer.Payload, 0, layer.Payload.Length);
        }

        Log.Information($"[Core.Cloud]: Wrote a 2.3 DNA with a behavior layer: {writer.Length} bytes, {layers.Count} layer(s)");

        return writer.ToArray();
    }
}
