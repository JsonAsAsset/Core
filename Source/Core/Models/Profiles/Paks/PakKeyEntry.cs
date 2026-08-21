using CommunityToolkit.Mvvm.ComponentModel;

using Core.Resources.Framework.CUEParse;

namespace Core.Models.Profiles.Paks;

public partial class PakKeyEntry : ObservableObject
{
    /* Empty for a key the archive has no container for */
    [ObservableProperty] private string _fileName = string.Empty;

    [ObservableProperty] private string _key = string.Empty;

    [ObservableProperty] private string _guid = string.Empty;

    /* Keys outlive the containers they were minted for. A profile holds on to every key it has ever
     * been handed, so once a build stops shipping the chunk a key belongs to there is no container
     * left to take a name from, and the guid is all the entry ever had. */
    public bool HasContainer => !string.IsNullOrWhiteSpace(FileName);

    /* The container is what the reader is really waiting on, so it leads. Falling back to the guid
     * beats a blank line, and the guid is still shown underneath either way. */
    public string DisplayName => HasContainer ? FileName : Guid;

    public bool HasKey => EncryptionKey.IsValidKey(Key);

    /* Typed but not yet a key, which is worth telling apart from an untouched entry */
    public bool IsKeyMalformed => !string.IsNullOrWhiteSpace(Key) && !HasKey;

    public bool Matches(string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        search = search.Trim();

        return FileName.Contains(search, System.StringComparison.OrdinalIgnoreCase)
               || Guid.Contains(search, System.StringComparison.OrdinalIgnoreCase);
    }

    partial void OnKeyChanged(string value)
    {
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(IsKeyMalformed));
    }

    partial void OnFileNameChanged(string value)
    {
        OnPropertyChanged(nameof(HasContainer));
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnGuidChanged(string value) => OnPropertyChanged(nameof(DisplayName));
}
