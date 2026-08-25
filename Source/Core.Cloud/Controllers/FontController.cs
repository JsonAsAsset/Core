using CUE4Parse.UE4.Assets.Exports.Engine.Font;
using CUE4Parse.Utils;

using Microsoft.AspNetCore.Mvc;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: Font Faces                                                                                                */
/*                                                                                                                                  */
/* The font itself, which is the one part of a font face that is not a property.                                                    */
/*                                                                                                                                  */
/* A font face asset is a handful of settings and a whole typeface: the bytes of a TTF or an OTF. Properties come back through the   */
/* ordinary export and those bytes do not, so they are served here on their own.                                                     */
/*                                                                                                                                  */
/* Where they are depends on how the face was set to load. Only a face marked Inline keeps them in the asset:                        */
/*                                                                                                                                  */
/*     bool bSaveInlineData = LoadingPolicy == EFontLoadingPolicy::Inline || !Ar.IsCooking();                                        */
/*                                                                                                                                  */
/* and the default is LazyLoad, so most cooked faces have nothing in the asset at all. Those keep the typeface in a file of its own   */
/* beside the package, named for the object with a ufont extension, which is what gets read when the asset comes up empty.           */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    /* The typeface a font face carries, as the file it was made from */
    [HttpGet("export/fontface")]
    public ActionResult GetFontFace(string? path)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new
        {
            errorCode = "cloud.fontface.no_path",
            errorMessage = "No asset supplied",
            numericErrorCode = 1005
        });

        path = path.SubstringBefore('.');

        var profile = FindBaseProfileForPath(path, found: out var found);
        if (!found) return NotFoundResponse;

        if (LoadExportOfType<UFontFace>(profile.Provider, path) is not { } fontFace) return NotFoundResponse;

        var typeface = fontFace.FontFaceData is { Data.Length: > 0 } inline ? inline.Data : null;

        /* Nothing inline means the cook put it beside the package instead. UFontFace::GetCookedFilename
         * builds that name as the package's folder, the object's name, and a ufont extension. */
        if (typeface is null)
        {
            var folder = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
            var beside = string.IsNullOrEmpty(folder) ? $"{fontFace.Name}.ufont" : $"{folder}/{fontFace.Name}.ufont";

            if (profile.Provider.TrySaveAsset(beside, out var payload) && payload.Length > 0)
            {
                typeface = payload;
            }
        }

        if (typeface is null || typeface.Length == 0)
        {
            return NotFound(new
            {
                errorCode = "cloud.fontface.no_data",
                errorMessage = "The font face has no typeface, inline or beside it",
                numericErrorCode = 1001
            });
        }

        /* Named by what it is, since a face can hold either and the two are told apart by the tag
         * the file opens with rather than by anything the asset says */
        var extension = typeface.Length >= 4 && typeface[0] == 'O' && typeface[1] == 'T' && typeface[2] == 'T' && typeface[3] == 'O'
            ? "otf"
            : "ttf";

        return File(typeface, "application/octet-stream", $"{fontFace.Name}.{extension}");
    }
}
