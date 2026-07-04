using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Core.Extensions;
using Core.Framework.Models;
using Core.WindowModels;

namespace Core.Windows;

public partial class MainWindow : WindowBase<MainWindowModel>
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = WindowModel;
        
        Navigation.App.Initialize(MainNavigationView);
        Navigation.App.OnNavigate += WindowModel.OnNavigationItemSelected;

        WindowModel.Initialize();
    }

    public void OnEditProfile(object? sender, RoutedEventArgs e)
    {  
        WindowModel.RequestEditProfile();
    }
    
    public void OnEditLinkedProfile(object? sender, RoutedEventArgs e)
    {  
        WindowModel.RequestEditLinkedProfile();
    }
}
