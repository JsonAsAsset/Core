using System.IO.Enumeration;

using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.Utils;

using Core.Resources.Framework.Base;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud: Additive Base Poses                                                                                                  */
/*                                                                                                                                  */
/* What an additive was built over, for the ones that no longer say. An aim offset keeps the kind of difference it is and drops the  */
/* animation it was taken from, so there is nothing left in the asset to follow: the pairing lives in how the game names things, and */
/* the game is what this is a reader for. Kept here rather than asked about every time; anything not named below is still asked         */
/* about at the other end.                                                                                                          */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    /* One pattern and the animations to build what matches it over.
     *
     * More than one may be named: the game keeps the same pose in more than one place across its
     * builds, and a path that isn't in the build being read is no use. They are tried in the order
     * they are written and the first one that is actually there wins. */
    private sealed record AdditiveBase(string Pattern, params string[] BasePoses);

    private static readonly string[] AR_NonTargeted =
    [
        "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/AssaultRifle/AR_NonTargeted_Pose_CMM", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/M_AssaultRifle_NonTargeted"
    ];
    
    private static readonly string[] AR_Hip_NonTargeted =
    [
        "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/M_AssaultRifle_HipNonTargeted"
    ];
    
    private static readonly string[] AR_Targeted =
    [
        "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/M_AssaultRifle_Targeted"
    ];
    
    private static readonly string[] Launcher =
    [
        "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/Launcher_CMM"
    ];
    
    private static readonly string[] Rifle =
    [
        "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/Rifle_NonTargeted_CMM"
    ];
    
    private static readonly string[] Pistol =
    [
        "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/M_Pistol_HF"
    ];
    
    private static readonly string[] Pistol_Targeted =
    [
        "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/M_Pistol_DS"
    ];

    /* Matched against the sequence's name, first match wins, so anything more particular goes above
     * whatever is broader than it */
    private static readonly AdditiveBase[] AdditiveBasePoses =
    [
        new("NoWep_Crouch2Idle*", "FortniteGame/Content/Animation/Game/MainPlayerFN/Poses/NoWepBasePose/NoWep_BasePose_Relaxed"),
        new("NoWep_Idle2Crouch*", "FortniteGame/Content/Animation/Game/MainPlayerFN/Poses/NoWepBasePose/Crouch_NoWep_BasePose_Relaxed"),

        new("Idle_Noise_SneakySnowman", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/M_NoWep_Relaxed"),
        new("Idle_Noise", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/M_NoWep_Relaxed"),
        
        new("CantDoIt", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/M_NoWep_Relaxed"),
        new("CantDoIt_Neutral", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/M_NoWep_Relaxed"),

        new("FromStanding_From*", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/M_NoWep_Relaxed"),
        new("FromStanding_InFront*", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/M_NoWep_Relaxed"),
        
        new("Idle_Noise_AR_Downsights", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/AssaultRifle/AR_NonTargeted_Pose_CMM"),
        new("Idle_Noise_ConsumableLarge_CMM", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/ConsumableLarge/ConsumableLarge_Pose_CMM"),
        new("ConsumableSmall_IdleNoise_CMM", "FortniteGame/Content/Animation/Game/MainPlayer/Combat/Gadgets/Medium/Male/ConsumableSmall/ConsumableSmall_Pose_CMM"),
        new("AssaultRifle_Core_Relaxed_IdleNoise", "FortniteGame/Plugins/GameFeatures/SharedWeaponAnims/Content/Poses/RedDot_AR/AssaultRifle_Core_RedDot_Relaxed"),
        new("ButterflyLook_AO_*", "FortniteGame/Content/Animation/Game/MainPlayer/Combat/Gadgets/Medium/Male/GhostRock/GhostRock_Base_Pose_CMM"),
        new("Pistol_Fang_NonTargeted_*", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/AO/Pistol_Fang_Pose_NonTargeted"),
        new("Pistol_NonTargeted_*", Pistol),
        new("Pistol_Zapper_Fire_*", Pistol),
        new("PistolAuto_Fire", Pistol),
        new("Pistol_Revolver_Fire_CMM", Pistol),
        new("Pistol_GripClipLong_Fire_*", Pistol),
        new("M_AO_Pistol_Downsights*", Pistol_Targeted),

        new("GhostRock_AO_*", "FortniteGame/Content/Animation/Game/MainPlayer/Combat/Gadgets/Medium/Male/GhostRock/GhostRock_Base_Pose_CMM"),
        new("GhostRock_Idle_Leans_*", "FortniteGame/Content/Animation/Game/MainPlayer/Combat/Gadgets/Medium/Male/GhostRock/GhostRock_Base_Pose_CMM", "FortniteGame/Content/Animation/Game/MainPlayer/Combat/Gadgets/Medium/Male/GhostRock/GhostRock_Base_Pose_CMM"),
        new("Launcher_*", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/Launcher_CMM"),
        new("DualPistol_*", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/DualPistol_NonTargeted_CMM"),
        new("AshtonChicago_AO_Targeted_*", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/Ashton_Chicago/AshtonChicago_NonTargeted_Pose_CMM"),
        new("ValetCone_AO_*", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/ValetCone/ValetCone_Targeted_CMM"),
        new("Flashlight_NTAO_*", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/Flashlight/Handheld_Flashlight_Pose_CMM"),
        new("FeyCrab_Target_*", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/FeyCrab/FeyCrab_Targeted_Pose_Male"),
        new("Bow_NT_*", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/ExplosiveBow/ExplosiveBow_NonTargeting_Pose_CMM"),
        new("DrumGun_*", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/AutoDrum/Pose_AutoDrum_NonTargeted_CMM"),
        new("TacticalShotgun02_NonTargeted_*", "FortniteGame/Content/Animation/Game/MainPlayer/Poses/Medium/Male/TacticalShotgun02/TacticalShotgun02_NonTargeted_CMM"),
        
        new("Sprint_Lean_*", "FortniteGame/Content/Animation/Game/MainPlayer/Locomotion/Medium/Male/Sprint/Sprint_Default"),
        
        new("RPG_Fire_GuidedMissile_*", Launcher),
        
        /* AR */
        new("AR_NonTargeted_*", AR_NonTargeted),
        new("M_AO_AR_Downsights*", AR_Targeted),
        new("AssaultRifle_FrontClip_DownSightsFire_*", AR_NonTargeted),
        new("AssaultRifle_FrontClip_HipFire_*", AR_NonTargeted),
        new("BoltAction_SniperRifle_NoScope_Fire_*", AR_NonTargeted),
        
        new("Shotgun_Default_DownsightsFire_*", Rifle),
        new("Shotgun_Default_Hipfire_*", Rifle),
    ];

    /* What the game calls the animation an additive was taken from, when it calls it anything at
     * all: the same name with this on the end. Sitting beside it is the usual arrangement. */
    private const string AdditiveBaseSuffix = "_Core";

    /* Where an aim offset's name stops being the name of the pose it was taken from, and starts
     * being what it does to it: "M_GoingCommando_AO_RU_45" is a turn of "M_GoingCommando". */
    private const string AdditiveOffsetMarker = "_AO";

    /* The body an animation was authored for, written on the end of its name: three letters
     * beginning with C. Everything else is a variant of the one below, so a name ending in any
     * other one is asking for this one. */
    private const string AdditiveBaseBody = "CMM";

    /* The animation this one is named after, for the pairs the game names rather than lists.
     *
     * Three habits, in the order they are worth trying: the animation an additive was taken from
     * put beside it under the same name with _Core on the end; an aim offset named after the pose
     * it turns, with the turn written on the end; and a body variant of an animation whose base is
     * the same animation for the body every other one is a variant of. */
    private static string? FindDerivedBasePose(BaseProfile profile, string sequenceName, string path)
    {
        if (FindNamedBasePose(profile, sequenceName + AdditiveBaseSuffix, path) is { } core) return core;

        var marker = sequenceName.LastIndexOf(AdditiveOffsetMarker, StringComparison.OrdinalIgnoreCase);

        if (marker > 0)
        {
            var posed = sequenceName[..marker];

            if (FindNamedBasePose(profile, posed, path) is { } pose) return pose;
        }

        if (SwapBodyForBase(sequenceName) is { } sameAnimation
            && FindNamedBasePose(profile, sameAnimation, path) is { } body)
        {
            return body;
        }

        return null;
    }

    /* The same name written for the body every other one is a variant of, or nothing when the name
     * doesn't end in a body at all -- or already ends in that one, which would be itself. */
    private static string? SwapBodyForBase(string sequenceName)
    {
        /* An underscore, a C, and two more: anything shorter is not a name with a body on the end,
         * and anything else in that shape is a word that happens to finish the same way */
        if (sequenceName.Length < 4) return null;

        var body = sequenceName[^3..];

        if (sequenceName[^4] != '_' || (body[0] != 'C' && body[0] != 'c')) return null;

        if (body.Equals(AdditiveBaseBody, StringComparison.OrdinalIgnoreCase)) return null;

        return sequenceName[..^3] + AdditiveBaseBody;
    }

    /* An animation of this name: beside the sequence first, since that is where one nearly always
     * sits, and then anywhere the game keeps one -- nearest to the sequence first, so a name used
     * in several places resolves to the one it was cooked alongside. */
    private static string? FindNamedBasePose(BaseProfile profile, string name, string path)
    {
        var askedFrom = path.SubstringBeforeLast('/');

        var beside = askedFrom + "/" + name;

        if (LoadExportOfType<UAnimSequence>(profile.Provider, beside) is not null) return beside;

        var matches = new List<string>();

        CollectByName(profile, name, matches);

        if (matches.Count == 0) return null;

        matches.Sort((left, right) => SharedDepth(right, askedFrom).CompareTo(SharedDepth(left, askedFrom)));

        return matches[0];
    }

    /* The animations named for this one, in the order to try them, or nothing when it is not one
     * of them */
    private static string[] FindAdditiveBasePoses(string sequenceName)
    {
        foreach (var entry in AdditiveBasePoses)
        {
            if (FileSystemName.MatchesSimpleExpression(entry.Pattern, sequenceName, ignoreCase: true))
            {
                return entry.BasePoses;
            }
        }

        return [];
    }
}
