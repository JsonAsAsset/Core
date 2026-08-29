using CUE4Parse.Utils;

using Microsoft.AspNetCore.Mvc;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Schema                                                                                                                           */
/*                                                                                                                                  */
/* What the mappings say a type is, as they say it after everything has been put over them.                                          */
/*                                                                                                                                  */
/* An unversioned property is a number counted through a class's properties, so a property read off the wrong place is a schema that */
/* disagrees with the package by one entry somewhere. Finding which entry means looking at the list, and the list is otherwise only   */
/* visible from inside a read that has already gone wrong.                                                                          */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    [HttpGet("companion")]
    public ActionResult GetCompanion(string? path)
    {
        if (!IsBaseProfileReady || path is null) return NotInitializedResponse;

        path = path.SubstringBefore('.');

        var profile = FindBaseProfileForPath(path, found: out var found);
        if (!found) return NotFoundResponse;

        var provider = profile.Provider;

        var wasUsing = provider.MappingsContainer;
        var editorSchema = EditorSchemaFor(provider);

        if (editorSchema is not null) provider.MappingsContainer = editorSchema;

        try
        {
            if (!provider.TryLoadPackage($"{path}.o.uasset", out var editorAsset)) return NotFoundResponse;

            return new JsonResult(editorAsset.GetExports().Select(one => new
            {
                name = one.Name,
                type = one.ExportType,
                properties = one.Properties.Select(held => held.Name.Text).ToArray()
            }).ToArray());
        }
        finally
        {
            provider.MappingsContainer = wasUsing;
        }
    }

    [HttpGet("schema")]
    public ActionResult GetSchema(string? type)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(type)) return BadRequest(new
        {
            errorCode = "cloud.schema.no_type",
            errorMessage = "No type supplied",
            numericErrorCode = 1004
        });

        var mappings = MainProfile.Provider.MappingsContainer?.MappingsForGame;

        if (mappings is null || !mappings.Types.TryGetValue(type, out var schema))
        {
            return NotFoundResponse;
        }

        var properties = new List<object>();

        for (var index = 0; index < schema.PropertyCount; index++)
        {
            if (!schema.Properties.TryGetValue(index, out var property))
            {
                properties.Add(new { index, name = "<none>" });

                continue;
            }

            properties.Add(new
            {
                index,
                name = property.Name,
                type = property.MappingType.Type,
                structType = property.MappingType.StructType,
                enumName = property.MappingType.EnumName,
                inner = property.MappingType.InnerType?.Type,
                innerStruct = property.MappingType.InnerType?.StructType,
                value = property.MappingType.ValueType?.Type,
                arraySize = property.ArraySize
            });
        }

        return new JsonResult(new
        {
            mappings = MainProfile.LoadedMappingsFile,
            name = schema.Name,
            super = schema.SuperType,
            count = schema.PropertyCount,
            total = schema.CountProperties(true),
            properties
        });
    }
}
