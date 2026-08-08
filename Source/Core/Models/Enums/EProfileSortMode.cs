using System.ComponentModel;

namespace Core.Models.Enums;

public enum EProfileSortMode
{
    [Description("Version / name")]
    VersionName,

    [Description("Last used")]
    LastUsed
}
