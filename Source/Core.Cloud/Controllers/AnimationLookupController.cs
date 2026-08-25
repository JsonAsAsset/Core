using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.Utils;

using Microsoft.AspNetCore.Mvc;

using Core.Resources.Framework.Base;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: Animation Lookup                                                                                          */
/*                                                                                                                                  */
/* Where an animation lives, given only its name. An additive that is a difference from a frame of itself names nothing it was built */
/* over, and what it was is usually written into its own name -- so the other end works out a name and asks here whether the game    */
/* shipped an animation called that.                                                                                                */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    /* Every animation the game shipped under this name, nearest first */
    [HttpGet("find/animation")]
    public ActionResult FindAnimation(string? name, string? near = null)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new
        {
            errorCode = "cloud.animation.no_name",
            errorMessage = "No name supplied",
            numericErrorCode = 1009
        });

        name = name.SubstringAfterLast('/').SubstringBefore('.');

        var matches = new List<string>();

        CollectByName(MainProfile, name, matches);

        foreach (var profile in SecondaryBaseProfiles)
        {
            if (!profile.IsInitialized) continue;

            CollectByName(profile, name, matches);
        }

        if (matches.Count == 0) return NotFoundResponse;

        /* A name can be shipped in several folders, and the one wanted is nearly always the one
         * sitting nearest whatever asked. Ordered by how much of the asking path they share. */
        if (!string.IsNullOrWhiteSpace(near))
        {
            var askedFrom = near.SubstringBeforeLast('/');

            matches.Sort((left, right) => SharedDepth(right, askedFrom).CompareTo(SharedDepth(left, askedFrom)));
        }

        return new JsonResult(new
        {
            name,
            path = matches[0],
            matches
        });
    }

    /* Every file in the profile called Name, that reads back as an animation sequence */
    private static void CollectByName(BaseProfile profile, string name, List<string> matches)
    {
        var suffix = "/" + name + ".uasset";

        foreach (var file in profile.Provider.Files.Keys)
        {
            if (!file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            var path = file.SubstringBeforeLast('.');

            if (matches.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;

            /* Named right and still not the thing: a mesh, a montage, a blueprint. Only what reads
             * back as a sequence is any use as a base. */
            if (LoadExportOfType<UAnimSequence>(profile.Provider, path) is null) continue;

            matches.Add(path);
        }
    }

    /* How many folders two paths have in common from the root down */
    private static int SharedDepth(string left, string right)
    {
        var leftParts = left.Split('/');
        var rightParts = right.Split('/');

        var shared = 0;

        while (shared < leftParts.Length && shared < rightParts.Length
            && string.Equals(leftParts[shared], rightParts[shared], StringComparison.OrdinalIgnoreCase))
        {
            shared++;
        }

        return shared;
    }
}
