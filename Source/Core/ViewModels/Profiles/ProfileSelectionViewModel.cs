using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Core.Framework.Models;
using Core.Models;
using Core.Models.Enums;
using Core.Models.Profiles;
using Core.Resources.Extensions;
using Core.Services.Framework;
using Core.Views.Profiles;

namespace Core.ViewModels.Profiles;

public partial class ProfileSelectionViewModel : ViewModelBase
{
    public Dictionary<string, ProfileCard> CardMap { get; } = new();
    private Dictionary<string, ProfileCardViewModel> ViewModelMap { get; } = new();

    /* A card keeps its wrapper as logical parent even while filtered out of the panel,
     * so wrappers have to be remembered here rather than read back off the panel */
    private readonly Dictionary<ProfileCard, Border> WrapperMap = new();

    [ObservableProperty] private bool isEmpty;

    /* Set when there are profiles but the search hides all of them */
    [ObservableProperty] private bool hasNoMatches;

    [ObservableProperty] private string searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortModeLabel))]
    [NotifyPropertyChangedFor(nameof(IsSortByVersionName))]
    [NotifyPropertyChangedFor(nameof(IsSortByLastUsed))]
    private EProfileSortMode sortMode = EProfileSortMode.VersionName;

    public string SortModeLabel => SortMode.GetDescription();
    public bool IsSortByVersionName => SortMode == EProfileSortMode.VersionName;
    public bool IsSortByLastUsed => SortMode == EProfileSortMode.LastUsed;

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    private int visibleCount;

    public string CountLabel => IsSearching
        ? $"{visibleCount} of {CardMap.Count}"
        : CardMap.Count == 1 ? "1 profile" : $"{CardMap.Count} profiles";

    partial void OnSearchTextChanged(string value)
    {
        ApplyView();

        OnPropertyChanged(nameof(IsSearching));
    }

    public void ClearSearch() => SearchText = string.Empty;
    partial void OnSortModeChanged(EProfileSortMode value) => ApplyView();

    [RelayCommand]
    private void SetSortMode(EProfileSortMode mode) => SortMode = mode;

    public Panel? ProfileListPanel { get; set; }
    public Func<ProfileCard, Border>? WrapCard { get; set; }
    public Action<ProfileCard>? HookEvents { get; set; }

    /* The view owns card sizing, so it has to run again whenever the panel is rebuilt */
    public Action? OnLayoutChanged { get; set; }

    private bool hasDetectedGames;

    private async Task LoadAll()
    {
        if (!hasDetectedGames)
        {
            hasDetectedGames = true;
        
            await GameDetection.LoadAllAsync();
            GameDetection.DetectAllProfilesAsync();
            GameDetection.PostDetection();
        }
    }
    
    private bool hasAttemptedProfileLoad;
    private bool hasLoadedProfiles;
    
    public async Task RefreshAllAsync()
    {
        if (!hasLoadedProfiles)
        {
            await LoadAll();
            
            hasLoadedProfiles = true;
        }
        
        /* Navigating back to this tab builds a new view and re-runs this, so the previous
         * generation has to let go of the clock before it is dropped */
        foreach (var viewModel in ViewModelMap.Values)
        {
            viewModel.Release();
        }

        CardMap.Clear();
        ViewModelMap.Clear();

        var profiles = GameDetection.LoadedProfiles;

        var sorted = Profile.SortProfiles(profiles);

        _ = Task.Run(() =>
        {
            foreach (var profile in sorted)
            {
                _ = profile.ResolveDataFromArchives(false);
            }
        });

        if (Globals.LaunchProfileArg != string.Empty && !hasAttemptedProfileLoad)
        {
            foreach (var profile in GameDetection.LoadedProfiles.Where(profile => profile.FileID == Globals.LaunchProfileArg))
            {
                _ = MainWM.StartProfileAsync(profile);
            }
            
            hasAttemptedProfileLoad = true;
        }
        
        if (AppServices.Settings.Application.LoadRecentProfileOnLaunch && MainWM.CurrentProfile is null && !hasAttemptedProfileLoad)
        {
            var recentProfile = GameDetection.GetRecentlyUsedProfiles(1).FirstOrDefault() ?? GameDetection.LoadedProfiles.FirstOrDefault();

            if (recentProfile is not null)
            {
                _ = MainWM.StartProfileAsync(recentProfile);
            }

            hasAttemptedProfileLoad = true;
        }

        if (!AppServices.Settings.Application.LoadRecentProfileOnLaunch)
        {
            hasAttemptedProfileLoad = true;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var profile in sorted)
            {
                CardMap[profile.FileName] = CreateCard(profile);
            }

            ApplyView();
        });
    }

    /* Every card stays on the panel in sort order. Searching only flips visibility, because
     * detaching and re-attaching cards costs a full re-realize of every splash, blur and flyout,
     * which is far too slow to run on each keystroke. */
    public void ApplyView()
    {
        IsEmpty = CardMap.Count == 0;

        if (ProfileListPanel is null)
        {
            HasNoMatches = false;
            OnPropertyChanged(nameof(CountLabel));

            return;
        }

        PruneWrappers();
        PruneViewModels();

        var ordered = Order(CardMap.Values.Where(card => card.ViewModel.Profile is not null).ToList());

        SyncPanel(ordered);

        var query = SearchText?.Trim() ?? string.Empty;
        var matches = 0;

        foreach (var card in ordered)
        {
            var isMatch = Matches(card.ViewModel.Profile!, query);

            GetWrapper(card).IsVisible = isMatch;

            if (isMatch) matches++;
        }

        HasNoMatches = !IsEmpty && matches == 0;

        visibleCount = matches;
        OnPropertyChanged(nameof(CountLabel));
    }

    /* Only touches the tree when the order or the membership actually moved */
    private void SyncPanel(List<ProfileCard> ordered)
    {
        var children = ProfileListPanel!.Children;

        if (children.Count == ordered.Count)
        {
            var identical = true;

            for (var i = 0; i < ordered.Count; i++)
            {
                if (ReferenceEquals(children[i], GetWrapper(ordered[i]))) continue;

                identical = false;
                break;
            }

            if (identical) return;
        }

        children.Clear();

        foreach (var card in ordered)
        {
            children.Add(GetWrapper(card));
        }

        OnLayoutChanged?.Invoke();
    }

    /* Reused so a reorder doesn't discard the Width the view measured, and so a card is never
     * handed a second parent after being filtered out and back in */
    private Border GetWrapper(ProfileCard card)
    {
        if (WrapperMap.TryGetValue(card, out var existing))
        {
            return existing;
        }

        var wrapper = WrapCard!(card);
        WrapperMap[card] = wrapper;

        return wrapper;
    }

    private void PruneWrappers()
    {
        if (WrapperMap.Count == 0) return;

        var live = CardMap.Values.ToHashSet();

        foreach (var card in WrapperMap.Keys.Where(card => !live.Contains(card)).ToList())
        {
            WrapperMap[card].Child = null;
            WrapperMap.Remove(card);
        }
    }

    /* A deleted profile leaves its view model behind, which would keep ticking on the clock */
    private void PruneViewModels()
    {
        foreach (var fileName in ViewModelMap.Keys.Where(fileName => !CardMap.ContainsKey(fileName)).ToList())
        {
            ViewModelMap[fileName].Release();
            ViewModelMap.Remove(fileName);
        }
    }

    private static bool Matches(Profile profile, string query)
    {
        return query.Length == 0 ||
               profile.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               profile.ArchiveDirectory.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private List<ProfileCard> Order(List<ProfileCard> cards)
    {
        if (SortMode == EProfileSortMode.LastUsed)
        {
            /* Never used sinks to the bottom rather than to 01/01/0001 among real dates */
            return cards
                .OrderByDescending(card => card.ViewModel.Profile!.Display.LastUsed ?? DateTime.MinValue)
                .ThenBy(card => card.ViewModel.Profile!.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var byProfile = cards.ToLookup(card => card.ViewModel.Profile!);

        return Profile.SortProfiles(byProfile.Select(group => group.Key).ToList())
            .SelectMany(profile => byProfile[profile])
            .ToList();
    }

    public void AddProfile(Profile profile)
    {
        if (CardMap.ContainsKey(profile.FileName))
        {
            return;
        }

        CardMap[profile.FileName] = CreateCard(profile);

        ApplyView();
    }

    public void UpdateProfileCard(Profile profile)
    {
        if (profile?.FileName is null) return;
        if (!CardMap.TryGetValue(profile.FileName, out var card)) return;

        card.ViewModel.Profile = profile;
        card.ViewModel.UpdateProfileProperties();

        if (ProfileListPanel is null) return;

        /* Last used may have just changed, so the order can move underneath us */
        ApplyView();
    }

    private ProfileCard CreateCard(Profile profile)
    {
        var vm = GetOrCreateProfileViewModel(profile);
        var card = new ProfileCard(vm);
        
        HookEvents?.Invoke(card);
        return card;
    }

    private ProfileCardViewModel GetOrCreateProfileViewModel(Profile profile)
    {
        if (ViewModelMap.TryGetValue(profile.FileName, out var existing))
        {
            return existing;
        }

        var newVm = new ProfileCardViewModel { Profile = profile };
        newVm.Initialize();
        
        ViewModelMap[profile.FileName] = newVm;
        
        return newVm;
    }
}
