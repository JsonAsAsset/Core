using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Core.Framework.Models;
using Core.Services;
using Core.ViewModels;

namespace Core.Views;

public partial class SettingsView : ViewBase<SettingsViewModel>
{
    public static readonly StyledProperty<bool> IsOnboardingProperty = AvaloniaProperty.Register<SettingsView, bool>(nameof(IsOnboarding));
    
    public bool IsOnboarding
    {
        get => GetValue(IsOnboardingProperty);
        set => SetValue(IsOnboardingProperty, value);
    }

    private NavigatorContext Context = Navigation.Settings;
    private bool HasInitialized;
    
    public SettingsView(): base(SettingsVM)
    {
        InitializeComponent();
        
        AttachedToVisualTree += (_, _) =>
        {
            if (HasInitialized) return;
            
            SettingsVM.IsOnboarding = IsOnboarding;
            
            if (IsOnboarding)
            {
                Context = new NavigatorContext();
            }
                
            Context.Initialize(NavigationView);
                
            HasInitialized = true;
        };
        
        MainWM.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWM.CurrentProfile))
            {
                UpdateProfile();
            }
        };
        
        NavigationView.TemplateApplied += (_, __) => FindPaneContentGrid();
        NavigationView.LayoutUpdated += (_, __) => UpdatePaneBorder();
        
        UpdateProfile();
    }
    
    private void UpdateProfile()
    {
        SettingsVM.CurrentProfile = MainWM.CurrentProfile;

        var isCurrentProfileValid = SettingsVM.CurrentProfile is not null;
        
        NavigationView.Classes.Set("has-profile", isCurrentProfileValid);
        PaneBorder.Classes.Set("has-profile", isCurrentProfileValid);
        
        Dispatcher.UIThread.Post(() =>
        {
            EditProfileButton.IsVisible = isCurrentProfileValid;
        });
    }
    
    private ScrollViewer? NavigationLeftPanelContents;
    
    private void FindPaneContentGrid()
    {
        NavigationLeftPanelContents = NavigationView.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault(v => v.Name == "MenuItemsScrollViewer");

        if (NavigationLeftPanelContents is null)
        {
            Dispatcher.UIThread.Post(FindPaneContentGrid, DispatcherPriority.Render);
            return;
        }

        NavigationLeftPanelContents.SizeChanged += (_, __) => UpdatePaneBorder();
        
        UpdatePaneBorder();
    }
    private void UpdatePaneBorder()
    {
        if (NavigationLeftPanelContents is null)
        {
            return;
        }

        PaneBorder.Height = NavigationLeftPanelContents.DesiredSize.Height - PaneBorder.Margin.Top;
    }

    private void EditProfile(object? sender, RoutedEventArgs e)
    {
        MainWM.CurrentProfile!.OpenEditor();
    }
    
    public void OpenGitHubLink(object? sender, RoutedEventArgs e)
    {
        AppService.OpenLink($"{GITHUB_COMMIT_LINK}/{COMMIT}");
    }
    
    public void OpenGitHubLicense(object? sender, RoutedEventArgs e)
    {
        AppService.OpenLink($"{GITHUB_LINK}/blob/main/LICENSE");
    }

    private void CopyGitCloneCommand(object? sender, RoutedEventArgs e)
    {
        App.CopyText(IS_COMMIT_AVAILABLE
            ? $"git clone --recurse-submodules {GITHUB_LINK}.git && cd {GITHUB_REPO_NAME} && git checkout {COMMIT} && git submodule update --init --recursive\n"
            : $"git clone --recurse-submodules {GITHUB_LINK}.git && cd {GITHUB_REPO_NAME} && git submodule update --init --recursive\n");
    }
}
