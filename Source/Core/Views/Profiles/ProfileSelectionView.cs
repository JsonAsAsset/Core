using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

using CommunityToolkit.Mvvm.Input;

using FluentAvalonia.UI.Controls;

using Core.Controls.Profiles;
using Core.Framework.Models;
using Core.Models.Enums;
using Core.ViewModels.Profiles;
using Core.WindowModels;
using Core.Windows;

namespace Core.Views.Profiles;

public partial class ProfileSelectionView : ViewBase<ProfileSelectionViewModel>
{
    private bool UIThreadCompleted;
    
    public ProfileSelectionView() : base(ProfileSelectionVM)
    {
        InitializeComponent();
    
        ViewModel.ProfileListPanel = ProfileListPanel;
        ViewModel.WrapCard = WrapCard;
        ViewModel.HookEvents = HookEvents;
        ViewModel.OnLayoutChanged = UpdateCardWidths;
    
        ProfileListPanel.SizeChanged += (_, _) => UpdateCardWidths();

        Avalonia.Threading.Dispatcher.UIThread.Post(async void () =>
        {
            if (UIThreadCompleted) return;
            
            UIThreadCompleted = true;
            
            await ViewModel.RefreshAllAsync();
            UpdateCardWidths();
        });
    }
    
    private void ClearSearch(object? sender, RoutedEventArgs e) => ViewModel.ClearSearch();

    private void UpdateCardWidths()
    {
        /* A null panel made the old check fall through to a dereference, since null <= 0 is false */
        if (ProfileListPanel is null || ProfileListPanel.Bounds.Width <= 0) return;

        var availableWidth = ProfileListPanel.Bounds.Width;
        const double minCardWidth = 430;
        const double cardSpacing = 5;

        var cardsPerRow = Math.Max(1, (int)((availableWidth + cardSpacing) / (minCardWidth + cardSpacing)));

        var totalSpacing = cardSpacing * (cardsPerRow - 1);
        var totalCardWidth = availableWidth - totalSpacing;
        var cardWidth = totalCardWidth / cardsPerRow;

        foreach (var control in ProfileListPanel.Children)
        {
            if (control is Border { Child: ProfileCard } border)
            {
                border.Width = cardWidth;
            }
        }
    }

    private Border WrapCard(ProfileCard card) => new()
    {
        Child = card,
        Margin = new Thickness(0, 10, 0, 0),
        ClipToBounds = false,
        MinWidth = 400,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private void HookEvents(ProfileCard card)
    {
        var window = this.GetVisualRoot() as MainWindow;
        if (window is null)
        {
            return;
        }
        
        var viewModel = (MainWindowModel)window.DataContext!;

        card.OnStart += async (_, _) =>
        {
            var profile = card.ViewModel.Profile;
            if (profile is not null)
            {
                await viewModel.StartProfileAsync(profile);
            }
        };

        card.OnEdit += (_, _) =>
        {
            var profile = card.ViewModel.Profile;
            if (profile is null) return;
            
            profile.OpenEditor(MainWM.Window);
        };

        card.OnDelete += async (_, _) =>
        {
            var profile = card.ViewModel.Profile;
            if (profile is null) return;
            
            var dialog = new ContentDialog
            {
                Title = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 15,
                    Margin = new Thickness(0, 0, 0, 5),
                    Children =
                    {
                        new ProfileSplashControl(2.5f)
                        {
                            DataContext = profile
                        },
                        new TextBlock
                        {
                            Text = $"Delete {profile.Name}?",
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 0, 5)
                        }
                    }
                },
                Content = $"'{profile.Name}' will be permanently removed and cannot be restored.",
                CloseButtonText = "Cancel",
                PrimaryButtonText = "Delete",
                PrimaryButtonCommand = new RelayCommand(() =>
                {
                    if (profile.FileName is not null)
                    {
                        ViewModel.CardMap.Remove(profile.FileName);
                    }

                    /* Drops the card and refreshes the empty states in one pass */
                    ViewModel.ApplyView();

                    if (viewModel.CurrentProfile is not null && viewModel.CurrentProfile == profile)
                    {
                        viewModel.NavigateToStatus(AppStatus.Idle);
                    }

                    profile.Delete();
                })
            };
            
            await dialog.ShowAsync(MainWM.Window);
        };
    }
}
