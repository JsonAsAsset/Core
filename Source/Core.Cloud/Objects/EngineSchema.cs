using CUE4Parse.MappingsProvider;

using Newtonsoft.Json.Linq;

using Serilog;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Engine schema                                                                                                                    */
/*                                                                                                                                  */
/* What a class really has, put over mappings that are short of it.                                                                  */
/*                                                                                                                                  */
/* An unversioned property is a number counted through a class's properties in the order it declares them, so reading one needs the  */
/* whole list. Mappings dumped from a game hold only what a build without editor data reflects, and every property a class keeps     */
/* behind WITH_EDITORONLY_DATA is missing from them. A package the editor saved counted through all of them, so from the first one   */
/* missing onwards every number lands on the property before the one it meant.                                                       */
/*                                                                                                                                  */
/* It is not one class. UEdGraphNode is short of seven, a Niagara function call node of three of its own, and so on down. Written    */
/* out by hand it would be a copy of the engine kept up by hand, so the engine is asked instead: the plugin walks its own classes    */
/* and writes what it finds, and that is read here.                                                                                  */
/*                                                                                                                                  */
/* The mappings file is never touched. What is completed is the copy already in memory, and only where the engine has more than the  */
/* mappings do and what the mappings do have still reads in order as part of it. Anything else is left exactly as it was.            */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Objects;

public static class EngineSchema
{
    public const string FileName = "EngineSchema.json";

    public static void Apply(TypeMappings? mappings, string? mappingsFile)
    {
        if (mappings is null || string.IsNullOrWhiteSpace(mappingsFile)) return;

        var found = Locate(mappingsFile);

        if (found is null) return;

        JObject written;

        try
        {
            written = JObject.Parse(File.ReadAllText(found));
        }
        catch (Exception e)
        {
            Log.Warning("Could not read the engine schema at {0}: {1}", found, e.Message);

            return;
        }

        var completed = 0;
        var already = 0;

        var refused = new List<string>();

        foreach (var (name, token) in written)
        {
            if (token is not JObject entry) continue;

            if (!mappings.Types.TryGetValue(name, out var schema)) continue;

            var listed = entry["Properties"] as JArray;

            if (listed is null) continue;

            /* Mappings that already count as far as the engine does have nothing missing */
            if (schema.PropertyCount >= (int?) entry["Slots"])
            {
                already++;

                continue;
            }

            if (!Completes(schema, listed))
            {
                refused.Add(name);

                continue;
            }

            Rebuild(schema, listed);

            completed++;
        }

        Log.Information("Completed {0} type(s) from the engine schema, {1} already whole, {2} left alone",
            completed, already, refused.Count);

        if (refused.Count > 0)
        {
            /* Said out loud rather than counted, because a type left alone here is a type that
             * still reads by numbers the package does not use */
            Log.Warning("Left alone: {0}", string.Join(", ", refused));
        }
    }

    /* Beside the mappings, or in the folder above them */
    private static string? Locate(string mappingsFile)
    {
        var directory = Path.GetDirectoryName(mappingsFile);

        while (!string.IsNullOrEmpty(directory))
        {
            var candidate = Path.Combine(directory, FileName);

            if (File.Exists(candidate)) return candidate;

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /* Whether what the mappings have is the engine's list with things taken out of it.
     *
     * That is the only relationship the two are meant to be in: the same class, one of them
     * without what a build with no editor data leaves out. Read in order, every name the mappings
     * carry has to turn up in the engine's list, in that order. Where it does not, the two are not
     * describing the same class and nothing is put over anything. */
    private static bool Completes(Struct schema, JArray listed)
    {
        var at = 0;

        for (var index = 0; index < schema.PropertyCount; index++)
        {
            if (!schema.Properties.TryGetValue(index, out var property)) continue;

            var found = false;

            while (at < listed.Count)
            {
                var named = (string?) listed[at]["Name"];

                at++;

                if (named == property.Name)
                {
                    found = true;

                    break;
                }
            }

            if (!found) return false;
        }

        return true;
    }

    private static void Rebuild(Struct schema, JArray listed)
    {
        /* What the mappings already say about a property is kept, since it is the same property
         * and they say it in the terms the reader is built around. Only the ones they have no
         * word for are taken from the engine. */
        var known = new Dictionary<string, PropertyInfo>();

        foreach (var (_, property) in schema.Properties)
        {
            known.TryAdd(property.Name, property);
        }

        var rebuilt = new Dictionary<int, PropertyInfo>();

        var index = 0;

        foreach (var one in listed)
        {
            var named = (string?) one["Name"];

            if (named is null) continue;

            var width = (int?) one["ArraySize"] ?? 1;

            var info = known.TryGetValue(named, out var existing)
                ? existing
                : new PropertyInfo(0, named, ReadType(one), width);

            /* A fixed size array stands in as many places as it has elements */
            for (var slot = 0; slot < width; slot++)
            {
                rebuilt[index + slot] = info;
            }

            index += width;
        }

        schema.Properties = rebuilt;
        schema.PropertyCount = index;
    }

    private static PropertyType ReadType(JToken described)
    {
        return new PropertyType(
            (string?) described["Type"] ?? "ObjectProperty",
            (string?) described["StructType"],
            described["InnerType"] is { } inner ? ReadType(inner) : null,
            described["ValueType"] is { } value ? ReadType(value) : null,
            (string?) described["EnumName"],
            (bool?) described["IsEnumAsByte"]);
    }
}
