using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Rig;
using CUE4Parse.UE4.Assets.Exports.Rig;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Readers;

using Newtonsoft.Json;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* DNA                                                                                                                              */
/*                                                                                                                                  */
/* A MetaHuman rig, written after the export's properties as the bit stream RigLogic reads. UDNAAsset::Serialize hands the rest of   */
/* the export straight to the DNA reader, so everything from where the properties end to where the export does is the stream.        */
/*                                                                                                                                  */
/* Only the header is taken apart here. What reads a DNA is RigLogic's own reader, so the bytes are what matter, and the header is   */
/* enough to say what they are and that they arrived whole.                                                                         */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Objects;

public class UDNA : UObject
{
    public string? DnaFileName;

    /* "DNA" opens the stream, and its absence means the bytes are not one */
    public bool IsValidStream;

    public ushort Generation;
    public ushort Version;

    /* Everything from the signature to the end of the export */
    public int StreamSize;

    /* The DNA proper, as its own index table accounts for it. Shorter than StreamSize on a UE6
     * cooked head, where a serialized RigLogic follows the stream inside the same export. */
    public int DnaSize;

    /* Whatever the export holds past the end of the DNA. On UE6 this is the baked RigLogic state
     * that UDNA::Serialize reads with MakeShared<FRigLogic>(&Ar, RigLogicConfiguration). */
    public int RigLogicSize;

    private FAssetArchive? _archive;
    private long _streamStart;

    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        var exportStart = Ar.Position;

        /* The class carries no properties of its own and isn't in the mappings, so reading them is
         * allowed to come to nothing. What matters is where the stream starts. */
        try
        {
            base.Deserialize(Ar, validPos);
        }
        catch
        {
            Ar.Position = exportStart;
        }

        DnaFileName = GetOrDefault<string>(nameof(DnaFileName));

        _archive = (FAssetArchive) Ar.Clone();
        _streamStart = FindSignature(Ar, exportStart, validPos);

        IsValidStream = _streamStart >= 0;

        if (!IsValidStream)
        {
            StreamSize = 0;
            Ar.Position = validPos;

            return;
        }

        StreamSize = (int) (validPos - _streamStart);

        /* The header is big endian, which is the one thing about a DNA that isn't the platform's */
        Ar.Position = _streamStart + 3;

        Generation = ReadBigEndianUInt16(Ar);
        Version = ReadBigEndianUInt16(Ar);

        DnaSize = MeasureDna(Ar, _streamStart, StreamSize);
        RigLogicSize = StreamSize - DnaSize;

        Ar.Position = validPos;
    }

    /* How far the DNA actually reaches, read off its own index table rather than assumed to be the
     * rest of the export.
     *
     * Epic's optimized cook writes two things into one export: a DNA holding only the definition
     * layer -- names, joint hierarchy, neutral pose -- and behind it the RigLogic that layer would
     * otherwise have been used to build. Serving the pair as one file hands the reader a DNA with a
     * few hundred kilobytes of something else stuck to the end of it. */
    private static int MeasureDna(FAssetArchive Ar, long streamStart, int streamSize)
    {
        try
        {
            /* signature(3) + generation(2) + version(2) */
            Ar.Position = streamStart + 7;

            var count = ReadBigEndianUInt32(Ar);

            /* An index that cannot fit in what is here is not an index */
            if (count == 0 || count > 64 || 11 + count * 16 > streamSize) return streamSize;

            var end = 0L;

            for (var i = 0u; i < count; i++)
            {
                Ar.Position += 4; /* id */
                Ar.Position += 4; /* layer generation + version */

                var offset = ReadBigEndianUInt32(Ar);
                var size = ReadBigEndianUInt32(Ar);

                end = Math.Max(end, (long) offset + size);
            }

            if (end <= 0 || end > streamSize) return streamSize;

            /* Pre v23 closes with "AND", which belongs to the DNA even though no layer covers it */
            if (end + 3 <= streamSize)
            {
                Ar.Position = streamStart + end;

                var marker = Ar.ReadBytes(3);

                if (marker[0] == (byte) 'A' && marker[1] == (byte) 'N' && marker[2] == (byte) 'D')
                {
                    end += 3;
                }
            }

            return (int) end;
        }
        catch
        {
            return streamSize;
        }
    }

    /* Where "DNA" opens the stream. The properties before it are a short header at most, so the
     * search stays near the start rather than walking the whole rig. */
    private static long FindSignature(FAssetArchive Ar, long exportStart, long validPos)
    {
        const int Window = 512;

        var length = (int) Math.Min(Window, validPos - exportStart);
        if (length < 7) return -1;

        Ar.Position = exportStart;

        var head = Ar.ReadBytes(length);

        for (var i = 0; i + 7 <= length; i++)
        {
            if (head[i] == (byte) 'D' && head[i + 1] == (byte) 'N' && head[i + 2] == (byte) 'A')
            {
                return exportStart + i;
            }
        }

        return -1;
    }

    /* The rig as data: the joints, the pose they rest at, and what each control does to them.
     *
     * Read on demand, and only the two layers a pose needs. The geometry and the machine learned
     * layers are a large part of a head and none of it says how a control moves a joint. */
    public RawDefinition? Definition { get; private set; }
    public RawBehavior? Behavior { get; private set; }

    public bool ReadRig()
    {
        if (Definition is not null && Behavior is not null) return true;
        if (!IsValidStream) return false;

        var stream = ReadStream();
        if (stream.Length == 0) return false;

        ReadRig(stream, out var definition, out var behavior);

        Definition ??= definition;
        Behavior ??= behavior;

        return Definition is not null && Behavior is not null;
    }

    /* The same read for a DNA that arrived as bytes rather than as one of these.
     *
     * A head that keeps its DNA in the mesh has it as a UDNAAsset holding the stream outright, with
     * no UDNA anywhere, so the layers have to be readable without one. */
    public static void ReadRig(byte[] stream, out RawDefinition? definition, out RawBehavior? behavior)
    {
        definition = null;
        behavior = null;

        /* A DNA kept in a package of its own has a few bytes of that package's own ahead of the
         * stream, where one hung straight off a mesh starts at it. The reader wants the stream. */
        for (var start = 0; start + 3 <= stream.Length && start < 64; start++)
        {
            if (stream[start] != 'D' || stream[start + 1] != 'N' || stream[start + 2] != 'A') continue;

            if (start > 0) stream = stream[start..];

            break;
        }

        if (stream.Length < 7 || stream[0] != 'D' || stream[1] != 'N' || stream[2] != 'A') return;

        var Ar = new FArchiveBigEndian(new FByteArchive("DNA", stream));

        /* Past the signature and the version that follows it */
        Ar.Position = 7;

        var version = (FileVersion) (((stream[3] << 8 | stream[4]) << 16) + (stream[5] << 8 | stream[6]));

        /* From v26 the index table opens the stream. Before it, section offsets are kept in a
         * lookup table that has to be turned into one. */
        IndexTable indexTable;

        if (version >= FileVersion.v26)
        {
            indexTable = new IndexTable(Ar);
        }
        else
        {
            var lookup = new SectionLookupTable(Ar);

            var versionAr = new FArchiveBigEndian(new FByteArchive("DNA", stream)) { Position = 3 };

            indexTable = new IndexTable(lookup, new DNAVersion(versionAr));
        }

        foreach (var entry in indexTable.Entries)
        {
            if (entry.Id is not ("defn" or "bhvr")) continue;

            Ar.Position = entry.Offset;

            try
            {
                if (entry.Id == "defn") definition = new RawDefinition(Ar);
                else behavior = new RawBehavior(Ar);
            }
            catch
            {
                /* A layer that will not read leaves whatever else did */
            }
        }
    }

    /* The DNA itself, for whatever hands it to RigLogic */
    public byte[] ReadStream()
    {
        if (_archive is null || DnaSize <= 0) return [];

        _archive.Position = _streamStart;

        return _archive.ReadBytes(DnaSize);
    }

    /* The DNA and the rig behind it, as the export holds them. What the rebuilder needs, since it
     * reads one and writes it back into the other. */
    public byte[] ReadFullStream()
    {
        if (_archive is null || StreamSize <= 0) return [];

        _archive.Position = _streamStart;

        return _archive.ReadBytes(StreamSize);
    }

    /* The baked rig behind the DNA, where the cook put one. Empty otherwise. */
    public byte[] ReadRigLogicState()
    {
        if (_archive is null || RigLogicSize <= 0) return [];

        _archive.Position = _streamStart + DnaSize;

        return _archive.ReadBytes(RigLogicSize);
    }

    private static ushort ReadBigEndianUInt16(FAssetArchive Ar)
    {
        var bytes = Ar.ReadBytes(2);

        return (ushort) ((bytes[0] << 8) | bytes[1]);
    }

    private static uint ReadBigEndianUInt32(FAssetArchive Ar)
    {
        var bytes = Ar.ReadBytes(4);

        return ((uint) bytes[0] << 24) | ((uint) bytes[1] << 16) | ((uint) bytes[2] << 8) | bytes[3];
    }

    protected override void WriteJson(JsonWriter writer, JsonSerializer serializer)
    {
        base.WriteJson(writer, serializer);

        if (!string.IsNullOrEmpty(DnaFileName))
        {
            writer.WritePropertyName(nameof(DnaFileName));
            writer.WriteValue(DnaFileName);
        }

        writer.WritePropertyName(nameof(IsValidStream));
        writer.WriteValue(IsValidStream);

        writer.WritePropertyName(nameof(StreamSize));
        writer.WriteValue(StreamSize);

        if (!IsValidStream) return;

        writer.WritePropertyName(nameof(DnaSize));
        writer.WriteValue(DnaSize);

        writer.WritePropertyName(nameof(RigLogicSize));
        writer.WriteValue(RigLogicSize);

        writer.WritePropertyName(nameof(Generation));
        writer.WriteValue(Generation);

        writer.WritePropertyName(nameof(Version));
        writer.WriteValue(Version);
    }
}
