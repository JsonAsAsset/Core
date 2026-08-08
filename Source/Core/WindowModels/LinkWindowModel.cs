using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Framework.Models;
using Core.Models;
using Core.Models.Profiles;
using Core.Resources.Utilities;

namespace Core.WindowModels;

public partial class LinkWindowModel : WindowModelBase
{
    /* Assigned once and never replaced. Handing the ItemsControl a new collection on each
     * keystroke regenerates every row, splash and progress ring, which is far too slow to type
     * through, so the search only flips IsMatch on the rows instead. */
    [ObservableProperty]
    private ObservableCollection<LinkProfileItemModel> profiles = [];

    private int matchCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanLink))]
    private LinkProfileItemModel? selectedProfile;

    [ObservableProperty]
    private string searchText = string.Empty;

    /* The link runs the profile's whole initialization, which is far from instant */
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonText))]
    [NotifyPropertyChangedFor(nameof(CanLink))]
    private bool isLinking;

    public bool HasSelection => SelectedProfile is not null;
    public bool CanLink => HasSelection && !IsLinking;
    public bool HasResults => matchCount > 0;
    public bool HasProfiles => Profiles.Count > 0;
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    public string PrimaryButtonText => IsLinking ? "Linking" : "Link";

    public string ActiveProfileName => MainWM.CurrentProfile?.Name ?? "the active profile";

    public LinkWindowModel()
    {
    }

    public override Task Initialize()
    {
        /* Not GetRecentlyUsedProfiles, which drops anything without a LastUsed and so would
         * hide every profile that has never been loaded from the list entirely */
        Profiles = new ObservableCollection<LinkProfileItemModel>(
            GameDetection.LoadedProfiles
                .Where(profile => profile != MainWM.CurrentProfile)
                .OrderByDescending(profile => profile.Display.LastUsed ?? DateTime.MinValue)
                .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .Select(profile => new LinkProfileItemModel(profile)));

        ApplyFilter();

        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(ActiveProfileName));

        return Task.CompletedTask;
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();

        OnPropertyChanged(nameof(IsSearching));
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim() ?? string.Empty;

        matchCount = 0;

        foreach (var item in Profiles)
        {
            item.IsMatch = query.Length == 0 ||
                           item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           item.ArchiveDirectory.Contains(query, StringComparison.OrdinalIgnoreCase);

            if (item.IsMatch) matchCount++;
        }

        OnPropertyChanged(nameof(HasResults));

        /* A selection the search just hid would be invisible but still linkable */
        if (SelectedProfile is null || SelectedProfile.IsMatch) return;

        SelectedProfile.IsSelected = false;
        SelectedProfile = null;
    }

    [RelayCommand]
    private void SelectProfile(LinkProfileItemModel profile)
    {
        if (SelectedProfile is not null)
            SelectedProfile.IsSelected = false;

        SelectedProfile = profile;
        SelectedProfile.IsSelected = true;
    }
}

public partial class LinkProfileItemModel : ObservableObject
{
    public Profile Profile { get; }

    public string Name => Profile.Name;
    public string ArchiveDirectory => Profile.ArchiveDirectory;

    public string LastUsed => Profile.Display.LastUsed.HasValue
        ? TimeUtilities.GetRelativeTime(Profile.Display.LastUsed.Value, RelativeTimeClock.Now)
        : "Never used";

    public bool IsAutoDetected => Profile.IsAutoDetected;
    public bool HasValidationErrors => Profile.HasValidationErrors;

    [ObservableProperty]
    private bool isSelected;

    /* Drives row visibility so the search never rebuilds the list */
    [ObservableProperty]
    private bool isMatch = true;

    public LinkProfileItemModel(Profile profile)
    {
        Profile = profile;
    }
}
