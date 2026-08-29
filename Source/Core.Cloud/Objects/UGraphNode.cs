using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.EdGraph;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Versions;

using Newtonsoft.Json;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Graph node                                                                                                                       */
/*                                                                                                                                  */
/* A node in an editor graph, and the pins that carry what it is wired to. The pins are written by an archive of their own after the */
/* properties, so nothing reading properties alone finds them, and a graph read without them is a pile of nodes with no edges.       */
/*                                                                                                                                  */
/* UEdGraphNode::Serialize writes them whenever the package is new enough to have the optimized pin format, and asks nothing else.   */
/* CUE4Parse asks one thing more: that the package keep its editor-only data. A Niagara module ships as a cooked stub beside an      */
/* optional segment that carries the whole graph, and that segment is flagged as filtering editor-only data while carrying it, so    */
/* the pins were skipped over every time. Read here on the engine's condition alone.                                                 */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Objects;

public class UGraphNode : UObject
{
    public UEdGraphPinReference?[] Pins = [];

    /* An entry is at least a null flag, an owning node and a guid. Nothing else is needed from the
     * number than a bound loose enough to be certain and tight enough that a misread length is
     * refused before it is turned into an allocation. */
    private const int SmallestPin = 16;

    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        base.Deserialize(Ar, validPos);

        if (FBlueprintsObjectVersion.Get(Ar) < FBlueprintsObjectVersion.Type.EdGraphPinOptimized)
        {
            return;
        }

        var start = Ar.Position;

        /* Nothing written after the properties is a node that was saved without its pins */
        if (start >= validPos) return;

        try
        {
            /* Read ahead of the array itself, so a length that could not be one is turned down
             * before it becomes an allocation of that size */
            var count = Ar.Read<int>();

            Ar.Position = start;

            if (count < 0 || (long) count * SmallestPin > validPos - start)
            {
                return;
            }

            UEdGraphPin.SerializeAsOwningNode(Ar, ref Pins);
        }
        catch
        {
            /* Whatever follows the properties here is not a pin array. Put the read back where it
             * started so the export is left as it would have been. */
            Ar.Position = start;

            Pins = [];
        }
    }

    protected override void WriteJson(JsonWriter writer, JsonSerializer serializer)
    {
        base.WriteJson(writer, serializer);

        writer.WritePropertyName(nameof(Pins));
        serializer.Serialize(writer, Pins);
    }
}
