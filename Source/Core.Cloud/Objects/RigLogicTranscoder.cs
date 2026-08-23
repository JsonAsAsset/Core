using Serilog;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* RigLogic Transcoder                                                                                                              */
/*                                                                                                                                  */
/* A UE6 cooked head keeps no rig in its DNA. The optimized cook writes the definition layer and nothing else, then follows it with  */
/* rl4::RigLogic::dump() -- the compiled rig, in RigLogicLib's own runtime layout. That dump is what the game runs the face with,    */
/* and it is the only copy of the rig the package has.                                                                              */
/*                                                                                                                                  */
/* The dump has no version stamp and no section table: rl4::RigLogic::restore reads the members of whichever RigLogicLib it was      */
/* built as, in declaration order. So a dump written by UE6 is only readable by UE6, and an older engine walking it desynchronises   */
/* on the first structure that changed and then reads noise.                                                                        */
/*                                                                                                                                  */
/* Between 5.7 and 6.0 most of it did not change. The joint matrices, the conditional tables and the PSD network are byte for byte   */
/* the same. What moved is the header, where the PSDs sit, and which sections a null evaluator writes nothing for. This walks a UE6  */
/* dump section by section and writes the same rig back out the way 5.7 lays it out.                                                 */
/*                                                                                                                                  */
/* Layouts were read out of both engines' RigLogicLib, and the walk is checked against the archive it came from: a dump that does    */
/* not end exactly where it should is one this misread, and nothing is returned for it.                                             */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Objects;

public static class RigLogicTranscoder
{
    /* terse writes big endian and prefixes every array with a uint32 count */
    private sealed class Cursor(byte[] data)
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

        /* Vector<T> of a fixed width element, returns how many there were */
        public uint Vector(int elementSize)
        {
            var count = U32();
            Position += checked((int) (count * (uint) elementSize));

            return count;
        }

        /* Matrix<T>, a vector of vectors */
        public void Matrix(int elementSize)
        {
            var rows = U32();

            for (var row = 0u; row < rows; row++)
            {
                Vector(elementSize);
            }
        }

        /* Identical in both engines, so it is only ever walked to find where it ends */
        public void ConditionalTable()
        {
            var maps = U32();

            for (var map = 0u; map < maps; map++)
            {
                var ranges = U32();

                for (var range = 0u; range < ranges; range++)
                {
                    Position += 8; /* from, to */
                    Vector(2);     /* rows */
                }
            }

            Vector(2); /* intervalsRemaining */
            Vector(2); /* inputIndices */
            Vector(2); /* outputIndices */

            Vector(4); /* fromValues */
            Vector(4); /* toValues */
            Vector(4); /* slopeValues */
            Vector(4); /* cutValues */

            Position += 4; /* inputCount, outputCount */
        }
    }

    private enum EvaluatorType : ushort
    {
        Auto,
        Null,
        Concrete
    }

    /* What a 5.7 RigMetrics needs, pulled out of a 6.0 RigMetadata */
    private struct Metrics
    {
        public ushort LodCount;
        public ushort GuiControlCount;
        public ushort RawControlCount;
        public ushort PsdControlCount;
        public ushort MlControlCount;
        public ushort RbfControlCount;
        public ushort JointAttributeCount;
        public ushort BlendShapeCount;
        public ushort AnimatedMapCount;
        public ushort NeuralNetworkCount;
        public ushort RbfSolverCount;
    }

    private readonly record struct Span(int Start, int End)
    {
        public int Length => End - Start;
    }

    /* A UE6 dump rewritten the way 5.7 reads one. Null when the dump does not walk cleanly, which
     * means the layout moved again and guessing would only hand the engine noise. */
    public static byte[]? ToLegacy(byte[] dump)
    {
        try
        {
            return Transcode(dump);
        }
        catch (Exception exception)
        {
            Log.Warning($"[Core.Cloud]: RigLogic dump did not transcode: {exception.Message}");

            return null;
        }
    }

    private static byte[]? Transcode(byte[] dump)
    {
        var cursor = new Cursor(dump);

        /* ~~~ Configuration ~~~
         * 6.0 dropped rotationOrder and gained the three pruning thresholds. 5.7 wants the order
         * back and none of the thresholds. */
        var calculationType = cursor.U8();

        var loadJoints = cursor.U8();
        var loadBlendShapes = cursor.U8();
        var loadAnimatedMaps = cursor.U8();
        var loadMachineLearnedBehavior = cursor.U8();
        var loadRbfBehavior = cursor.U8();
        var loadTwistSwingBehavior = cursor.U8();

        var translationType = cursor.U8();
        var rotationType = cursor.U8();
        var scaleType = cursor.U8();

        cursor.Position += 12; /* translation / rotation / scale pruning thresholds */

        /* ~~~ RigMetadata ~~~ */
        cursor.Position += 12; /* coordinateSystem */
        var rotationSequence = cursor.U32();
        cursor.Position += 12; /* rotationSigns */

        var metrics = new Metrics
        {
            LodCount = cursor.U16(),
            GuiControlCount = cursor.U16(),
            RawControlCount = cursor.U16(),
            PsdControlCount = cursor.U16(),
            MlControlCount = cursor.U16(),
            RbfControlCount = cursor.U16()
        };

        cursor.U16(); /* jointGroupCount, 5.7 keeps this on the joints section instead */

        metrics.JointAttributeCount = cursor.U16();
        metrics.BlendShapeCount = cursor.U16();
        metrics.AnimatedMapCount = cursor.U16();

        /* 6.0 counts ML types where 5.7 counts networks. Both are zero on anything that has no ML
         * behavior, which is the only case this can carry over. */
        metrics.NeuralNetworkCount = cursor.U16();
        metrics.RbfSolverCount = cursor.U16();

        var twistCount = cursor.U16();
        var swingCount = cursor.U16();

        /* Which sections wrote a real evaluator and which wrote nothing. 5.7 has no such queue, it
         * decides from the counts, so the two have to agree for the result to be readable. */
        var evaluatorCount = cursor.U32();
        var evaluators = new EvaluatorType[evaluatorCount];

        for (var i = 0u; i < evaluatorCount; i++)
        {
            evaluators[i] = (EvaluatorType) cursor.U16();
        }

        if (metrics.NeuralNetworkCount != 0 || metrics.RbfSolverCount != 0 || twistCount != 0 || swingCount != 0)
        {
            Log.Warning(
                "[Core.Cloud]: RigLogic dump uses machine learned, RBF or twist/swing behavior, " +
                "which 5.7 lays out differently. Not transcoding.");

            return null;
        }

        /* ~~~ Controls ~~~ 6.0 leads with registeredControls and keeps the PSDs in a section of
         * their own; 5.7 has no registeredControls and wants the PSDs in between. */
        cursor.Matrix(2);

        var guiToRawStart = cursor.Position;
        cursor.ConditionalTable();
        var guiToRaw = new Span(guiToRawStart, cursor.Position);

        var initialValuesStart = cursor.Position;
        cursor.Vector(8); /* ControlInitializer { uint32 index, float value } */
        var initialValues = new Span(initialValuesStart, cursor.Position);

        /* ~~~ MachineLearnedBehavior ~~~ null evaluator writes nothing, the module still writes the
         * per mesh region counts. 5.7 writes an empty matrix in the same place. */
        var meshRegionCountsStart = cursor.Position;
        cursor.Matrix(2);
        var meshRegionCounts = new Span(meshRegionCountsStart, cursor.Position);

        /* ~~~ RBFBehavior ~~~ evaluator only, and it is null here, so nothing at all */

        /* ~~~ PSDNet ~~~ same six members in the same order as 5.7 keeps inside Controls */
        var psdStart = cursor.Position;
        cursor.Matrix(2);  /* inputLODs */
        cursor.Matrix(2);  /* outputLODs */
        cursor.Vector(2);  /* inputIndicesPerPSD */
        cursor.Vector(20); /* PSD { size_t offset, size_t size, float weight } */
        cursor.Position += 4; /* psdMinIndex, psdMaxIndex */
        var psds = new Span(psdStart, cursor.Position);

        /* ~~~ Joints ~~~ the bulk of the rig, and byte for byte the same in both */
        var jointsStart = cursor.Position;

        cursor.Vector(4);  /* values, fp32 */
        cursor.Vector(2);  /* inputIndices */
        cursor.Vector(2);  /* outputIndices */
        cursor.Vector(24); /* lodRegions: ColumnLOD + PaddedBlockView, three uint32 each */
        cursor.Vector(2);  /* outputRotationIndices */
        cursor.Vector(2);  /* outputRotationLODs */
        cursor.Vector(36); /* jointGroups: nine uint32 */

        cursor.Vector(4);  /* neutralValues */
        cursor.Matrix(2);  /* variableAttributeIndices */
        cursor.Matrix(2);  /* jointIndices */
        cursor.Position += 2; /* jointGroupCount */

        var joints = new Span(jointsStart, cursor.Position);

        /* ~~~ BlendShapes ~~~ 6.0 writes a real section even with nothing in it. 5.7 picks the null
         * evaluator whenever blendShapeCount is zero, and that one writes nothing. */
        var blendShapesStart = cursor.Position;
        cursor.Vector(2); /* lods */
        cursor.Vector(2); /* inputIndices */
        cursor.Vector(2); /* outputIndices */
        var blendShapes = new Span(blendShapesStart, cursor.Position);

        /* ~~~ AnimatedMaps ~~~ identical */
        var animatedMapsStart = cursor.Position;
        cursor.Vector(2); /* lods */
        cursor.ConditionalTable();
        var animatedMaps = new Span(animatedMapsStart, cursor.Position);

        /* The walk is the only check there is. Landing anywhere but the end means a section moved. */
        if (cursor.Position != dump.Length)
        {
            Log.Warning(
                $"[Core.Cloud]: RigLogic dump walked to {cursor.Position} of {dump.Length}, " +
                "so its layout is not the one this knows. Not transcoding.");

            return null;
        }

        /* ~~~ Write it the way 5.7 reads it ~~~ */
        using var stream = new MemoryStream(dump.Length);

        void Byte(byte value) => stream.WriteByte(value);

        void U16(ushort value)
        {
            stream.WriteByte((byte) (value >> 8));
            stream.WriteByte((byte) value);
        }

        void Copy(Span span) => stream.Write(dump, span.Start, span.Length);

        /* Configuration, with rotationOrder taken from the rotation sequence 6.0 records on the
         * metadata instead. Both enumerate XYZ, XZY, YXZ, YZX, ZXY, ZYX in that order. */
        Byte(calculationType);
        Byte(loadJoints);
        Byte(loadBlendShapes);
        Byte(loadAnimatedMaps);
        Byte(loadMachineLearnedBehavior);
        Byte(loadRbfBehavior);
        Byte(loadTwistSwingBehavior);
        Byte(translationType);
        Byte(rotationType);
        Byte((byte) rotationSequence);
        Byte(scaleType);

        /* RigMetrics */
        U16(metrics.LodCount);
        U16(metrics.GuiControlCount);
        U16(metrics.RawControlCount);
        U16(metrics.PsdControlCount);
        U16(metrics.MlControlCount);
        U16(metrics.RbfControlCount);
        U16(metrics.JointAttributeCount);
        U16(metrics.BlendShapeCount);
        U16(metrics.AnimatedMapCount);
        U16(metrics.NeuralNetworkCount);
        U16(metrics.RbfSolverCount);

        /* Controls, with the PSDs folded back in where they used to live */
        Copy(guiToRaw);
        Copy(psds);
        Copy(initialValues);

        /* MachineLearnedBehavior: null evaluator, then the counts */
        Copy(meshRegionCounts);

        /* RBFBehavior: null evaluator, nothing */

        Copy(joints);

        /* BlendShapes: written only when 5.7 would build something that reads them */
        if (metrics.BlendShapeCount != 0)
        {
            Copy(blendShapes);
        }

        /* Same rule as the blend shapes: nothing to read means 5.7 builds the null one */
        if (metrics.AnimatedMapCount != 0)
        {
            Copy(animatedMaps);
        }

        Log.Information(
            $"[Core.Cloud]: Transcoded a RigLogic dump for 5.7: {dump.Length} -> {stream.Length} bytes, " +
            $"{metrics.JointAttributeCount} joint attribute(s), {metrics.PsdControlCount} PSD(s)");

        return stream.ToArray();
    }
}
