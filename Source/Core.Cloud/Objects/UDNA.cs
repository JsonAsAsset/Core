using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Readers;

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

    /* Everything from the end of the properties to the end of the export */
    public int StreamSize;

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

        Ar.Position = validPos;
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

    /* The stream itself, for whatever hands it to RigLogic */
    public byte[] ReadStream()
    {
        if (_archive is null || StreamSize <= 0) return [];

        _archive.Position = _streamStart;

        return _archive.ReadBytes(StreamSize);
    }

    private static ushort ReadBigEndianUInt16(FAssetArchive Ar)
    {
        var bytes = Ar.ReadBytes(2);

        return (ushort) ((bytes[0] << 8) | bytes[1]);
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

        writer.WritePropertyName(nameof(Generation));
        writer.WriteValue(Generation);

        writer.WritePropertyName(nameof(Version));
        writer.WriteValue(Version);
    }
}
