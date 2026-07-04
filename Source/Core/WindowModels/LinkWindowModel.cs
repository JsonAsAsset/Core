using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Framework.Models;
using Core.Models;
using Core.Models.Profiles;
using Core.Resources.Extensions;

namespace Core.WindowModels;

public partial class LinkWindowModel : WindowModelBase
{
    [ObservableProperty]
    private ObservableCollection<LinkProfileItemModel> profiles = [];

    [ObservableProperty]
    private LinkProfileItemModel? selectedProfile;

    public LinkWindowModel()
    {
    }

    public override async Task Initialize()
    {
        Profiles = new ObservableCollection<LinkProfileItemModel>(
            GameDetection
                .GetRecentlyUsedProfiles(GameDetection.LoadedProfiles.Count)
                .Where(profile => profile != MainWM.CurrentProfile)
                .Select(profile => new LinkProfileItemModel(profile))
        );
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

    public IBrush Background => IsSelected
        ? Brush.Parse("#212121")
        : Brushes.Transparent;

    [ObservableProperty]
    private bool isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(Background));
    }

    public LinkProfileItemModel(Profile profile)
    {
        Profile = profile;
    }
}