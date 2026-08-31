using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects.Unversioned;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.UObject;

using Serilog;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Schema aligned                                                                                                                   */
/*                                                                                                                                  */
/* A class the mappings describe with fewer properties than the package counted through.                                            */
/*                                                                                                                                  */
/* An unversioned property is a number into a class's list of properties, so the two have to be the same list. Where the mappings    */
/* are short, every number past the missing one lands on the property before the one it meant, and the last of them fall off the end */
/* of the class entirely. A renderer that was switched off reads as one that never said so, and nothing about it looks wrong.        */
/*                                                                                                                                  */
/* The package says how long the list really is. The highest number it uses has to be the last property of the class, so a number    */
/* past the end of what the mappings hold is the mappings being short, and by exactly that much.                                     */
/*                                                                                                                                  */
/* Where the missing ones sit is decided the same way, by what the package does rather than by guessing: the gap is put at the last  */
/* place that leaves every number the package actually used pointing at a property that exists. Anything earlier would move a        */
/* property the package has already read correctly.                                                                                  */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Objects;

public class USchemaAligned : UObject
{
    /* Looked at once per class. The mappings are shared, so the answer is too. */
    private static readonly HashSet<string> Examined = [];

    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        try
        {
            Align(Ar);
        }
        catch (Exception e)
        {
            Log.Warning("Could not check {0} against its mappings: {1}", ExportType, e.Message);
        }

        base.Deserialize(Ar, validPos);
    }

    private void Align(FAssetArchive Ar)
    {
        var type = ExportType;

        lock (Examined)
        {
            if (!Examined.Add(type)) return;
        }

        /* Only where the package counts through the class at all.
         *
         * A package that names its properties as it writes them has no numbering to be short for,
         * and the bytes at the front of it are not a header. Read as one they answer with whatever
         * they happen to say, which is a class asked to grow by hundreds of properties it has not
         * got. */
        if (Ar.Owner is null || !Ar.Owner.HasFlags(EPackageFlags.PKG_UnversionedProperties)) return;

        if (Ar.Owner.Mappings is not { } mappings || !mappings.Types.TryGetValue(type, out var schema)) return;

        var used = SlotsUsed(Ar);

        if (used.Count == 0) return;

        var own = schema.PropertyCount;
        var whole = schema.CountProperties(true);
        var highest = used.Max();

        /* Everything the package used is inside the class, so the mappings are as long as they need to be */
        if (highest < whole) return;

        var missing = highest - whole + 1;

        var at = Gap(schema, used, own, missing);

        if (at < 0)
        {
            Log.Warning(
                "{0} counts through {1} properties in the package and the mappings hold {2}, and there is nowhere to put the "
                + "difference that leaves what the package wrote pointing at properties that exist. Left as it is.",
                type, highest + 1, whole);

            return;
        }

        var rebuilt = new Dictionary<int, CUE4Parse.MappingsProvider.PropertyInfo>();

        foreach (var (index, info) in schema.Properties)
        {
            rebuilt[index < at ? index : index + missing] = info;
        }

        schema.Properties = rebuilt;
        schema.PropertyCount = own + missing;

        Log.Warning(
            "{0} counts through {1} properties in the package and the mappings hold {2}. The {3} the mappings do not know about "
            + "sit at {4}, which is the only place that leaves the rest reading as the package wrote them.",
            type, highest + 1, whole, missing, at);
    }

    /* Which numbers the package actually wrote, read without disturbing where the reader is */
    private static List<int> SlotsUsed(FAssetArchive Ar)
    {
        var found = new List<int>();
        var start = Ar.Position;

        try
        {
            var header = new FUnversionedHeader(Ar);

            if (header.HasValues)
            {
                using var walking = new FIterator(header);

                do
                {
                    found.Add(walking.Current.Val);
                } while (walking.MoveNext());
            }
        }
        finally
        {
            Ar.Position = start;
        }

        return found;
    }

    /* The last place the missing properties can sit without moving one the package already read */
    private static int Gap(CUE4Parse.MappingsProvider.Struct schema, List<int> used, int own, int missing)
    {
        for (var at = own; at >= 0; at--)
        {
            var stands = true;

            foreach (var slot in used)
            {
                /* Past the class's own properties is the super's, which the shift is what puts right */
                if (slot >= own + missing) continue;

                if (slot >= at && slot < at + missing)
                {
                    stands = false;

                    break;
                }

                if (!schema.Properties.ContainsKey(slot < at ? slot : slot - missing))
                {
                    stands = false;

                    break;
                }
            }

            if (stands) return at;
        }

        return -1;
    }
}
