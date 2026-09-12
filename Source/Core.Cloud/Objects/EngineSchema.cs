using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Versions;

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

    public static void Apply(TypeMappings? mappings, string? mappingsFile, EGame version)
    {
        if (mappings is null || string.IsNullOrWhiteSpace(mappingsFile)) return;

        var major = ((int) version >> 24) & 0xFF;
        var minor = ((int) version >> 16) & 0xFF;

        var found = Locate(mappingsFile, major, minor);

        if (found is null)
        {
            Log.Warning("No engine schema for UE {0}.{1}, so the editor mappings are left as the game wrote them. " +
                        "Editor only properties will read short. Dump one with -run=SchemaDump from a {0}.{1} editor and put it beside the mappings as {2}",
                major, minor, NameFor(major, minor));

            return;
        }

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

        /* Refused outright rather than read partly.
         *
         * A dump from another engine is not a worse answer, it is a confident wrong one: a
         * property added since sits in the middle of the list, and everything after it in the
         * package then reads as the property before the one it meant. Nothing about the result
         * looks wrong, which is what makes it worth refusing. */
        if (!WrittenBy(written, major, minor, out var said))
        {
            Log.Error("The engine schema at {0} came from UE {1} and this profile is UE {2}.{3}, so it is not used. " +
                      "Dump one from a {2}.{3} editor and put it beside the mappings as {4}",
                found, said, major, minor, NameFor(major, minor));

            return;
        }

        var completed = 0;
        var already = 0;

        var refused = new List<string>();

        var reordered = new List<string>();

        foreach (var (name, token) in written)
        {
            if (token is not JObject entry) continue;

            if (!mappings.Types.TryGetValue(name, out var schema)) continue;

            var listed = entry["Properties"] as JArray;

            if (listed is null) continue;

            /* Counting as far as the engine does is not the same as agreeing with it.
             *
             * A dump taken against one engine and used against another can hold the same number of
             * properties in a different order, and every one of them then reads off the wrong
             * place with nothing missing to give it away. Where the two disagree the engine is the
             * one that was there when the package was written, so it is taken whole. */
            if (schema.PropertyCount >= (int?) entry["Slots"])
            {
                if (Agrees(schema, listed))
                {
                    already++;

                    continue;
                }

                Rebuild(schema, listed);

                reordered.Add(name);

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

        Log.Information("Completed {0} type(s) from the engine schema, {1} put back in order, {2} already whole, {3} left alone",
            completed, reordered.Count, already, refused.Count);

        if (reordered.Count > 0)
        {
            Log.Warning("Put back in order: {0}", string.Join(", ", reordered));
        }

        if (refused.Count > 0)
        {
            /* Said out loud rather than counted, because a type left alone here is a type that
             * still reads by numbers the package does not use */
            Log.Warning("Left alone: {0}", string.Join(", ", refused));
        }
    }

    /* What a schema for one engine is called, since one file cannot serve them all */
    public static string NameFor(int major, int minor) => $"EngineSchema-{major}.{minor}.json";

    /* Beside the mappings, or in the folder above them.
     *
     * The one named for this engine is the only one taken. An unnamed EngineSchema.json is read
     * only for what it says it came from, so an old one left lying around is refused by name
     * rather than used by accident. */
    private static string? Locate(string mappingsFile, int major, int minor)
    {
        var wanted = NameFor(major, minor);

        var directory = Path.GetDirectoryName(mappingsFile);

        while (!string.IsNullOrEmpty(directory))
        {
            foreach (var candidate in new[] { Path.Combine(directory, wanted), Path.Combine(directory, FileName) })
            {
                if (File.Exists(candidate)) return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /* What the dump says it came from. One that says nothing came from before this was stamped and
     * is refused for that reason, since there is no way to tell what it describes. */
    private static bool WrittenBy(JObject written, int major, int minor, out string said)
    {
        said = "no engine at all";

        if (written["$Engine"] is not JObject engine) return false;

        var wroteMajor = (int?) engine["Major"];
        var wroteMinor = (int?) engine["Minor"];

        if (wroteMajor is null || wroteMinor is null) return false;

        said = $"{wroteMajor}.{wroteMinor}";

        return wroteMajor == major && wroteMinor == minor;
    }

    /* Whether what the mappings have is the engine's list with things taken out of it.
     *
     * That is the only relationship the two are meant to be in: the same class, one of them
     * without what a build with no editor data leaves out. Read in order, every name the mappings
     * carry has to turn up in the engine's list, in that order. Where it does not, the two are not
     * describing the same class and nothing is put over anything. */
    /* Whether the mappings name the same properties in the same places the engine does */
    private static bool Agrees(Struct schema, JArray listed)
    {
        for (var index = 0; index < listed.Count; index++)
        {
            if (!schema.Properties.TryGetValue(index, out var property)) continue;

            if ((string?) listed[index]["Name"] != property.Name) return false;
        }

        return true;
    }

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
