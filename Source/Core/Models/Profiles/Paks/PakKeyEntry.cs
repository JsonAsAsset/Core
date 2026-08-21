namespace Core.Models.Profiles.Paks;

public class PakKeyEntry
{
    public string FileName { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;

    /* Keys outlive the containers they were minted for. A profile holds on to every key it has ever
     * been handed, so once a build stops shipping the chunk a key belongs to there is no container
     * left to take a name from, and the guid is all the entry ever had. */
    public bool HasContainer => !string.IsNullOrWhiteSpace(FileName);

    public string DisplayName => HasContainer ? FileName : "No container in this installation";
}
