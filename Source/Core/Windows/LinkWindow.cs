using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Core.Framework.Models;
using Core.Resources.Framework.Base;
using Core.WindowModels;

namespace Core.Windows;

/* ~~~ LinkWindow ~~~ */
public partial class LinkWindow : WindowBase<LinkWindowModel>
{
    public LinkWindowModel WM => WindowModel;
    
    public LinkWindow()
    {
        Setup();
        DataContext = WindowModel;
    }
    
    /* ~~~ Setup Logic ~~~ */
    private void Setup()
    {
        InitializeComponent();
        WindowModel = new LinkWindowModel();

        _ = WindowModel.Initialize();
        DataContext = WindowModel;
    }

    private void Close(object? sender, RoutedEventArgs e)
    {
        Close();
    }
    
    private async void PrimaryButtonClick(object? sender, RoutedEventArgs e)
    {
        var profile = WM.SelectedProfile?.Profile;

        if (profile is null)
        {
            return;
        }

        await profile.Initialize();
        profile.Display.LastUsed = DateTime.Now;

        profile.Status.SetState(EProfileStatus.Active);

        MainWM.LinkedProfile = profile;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ProfileSelectionVM.UpdateProfileCard(profile);
        });

        Close();
    }
}
