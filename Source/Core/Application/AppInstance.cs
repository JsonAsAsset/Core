using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Core.Resources.Migration;
using Serilog;

using Core.Services.Framework;
using Core.Resources.Models;
using Core.Windows;

namespace Core.Application;

public class AppInstance : Avalonia.Application
{
    private IClassicDesktopStyleApplicationLifetime Desktop = null!;
    private bool debugShowStartup;
    
    public Window savedWindow = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
#if DEBUG
        debugShowStartup = false;
#endif

        /* AppData Migration */
        AppDataMigration.Apply();
        EnsureDirectories();
        
        AppServices.Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            App.Initialize(desktop);
            
            Log.Information($"Core version: {VERSION}");
            _ = NativeLibraryLoader.Load(RuntimeFolder);

            AppServices.Plugins.Load();

            Desktop = desktop;

            if (!Settings.Application.CompletedOnboarding || debugShowStartup)
            {
                SpawnWindow(new OnboardingWindow());
            }
            else
            {
                SpawnWindow(new MainWindow());
            }
            
            DispatcherTimer.RunOnce(() => AppServices.Cloud.Initialize(), TimeSpan.FromSeconds(0.5));
            DispatcherTimer.RunOnce(() => Update.Initialize(), TimeSpan.FromSeconds(1.2));
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void SpawnWindow(Window window)
    {
        if (window is MainWindow && Settings.Application.SaveWindowResolution)
        {
            if (Settings.Application.LastWindowResolution.Width != 0.0)
            {
                window.Width = Settings.Application.LastWindowResolution.Width;
                window.Height = Settings.Application.LastWindowResolution.Height;
            }

            window.Closing += (_, _) =>
            {
                if (window.WindowState == WindowState.Maximized)
                {
                    return;
                }
                
                var (width, height) = window.Bounds.Size;

                Log.Information($"Last window resolution before close: {width}x{height}");
                
                Settings.Application.LastWindowResolution = new PixelSize((int)width, (int)height);
            };
        }
        
        window.Closing += (_, _) =>
        {
#pragma warning disable IL3002
            App.RefreshWindowJumpList();
#pragma warning restore IL3002
        };
        
        window.Opened += (_, _) =>
        {
            if (window.Screens.ScreenFromWindow(window) is not { } screens) return;

            var bounds = window.Bounds;
            var centerX = screens.WorkingArea.X + (screens.WorkingArea.Width - bounds.Width) / 2;
            var centerY = screens.WorkingArea.Y + (screens.WorkingArea.Height - bounds.Height) / 2;

            window.Position = new PixelPoint((int)centerX, (int)centerY);
        };

        Desktop.MainWindow = window;
        Desktop.Startup += OnStartup;
        Desktop.Exit += OnExit;
            
        window.Show();

        savedWindow = window;
    }

    private static void OnStartup(object? sender, ControlledApplicationLifetimeStartupEventArgs e)
    {
        if (Settings.Connections.UseDiscordRichPresence)
        {
            Discord.Initialize();
        }
    }

    private static void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Discord.Deinitialize();
    }
}
