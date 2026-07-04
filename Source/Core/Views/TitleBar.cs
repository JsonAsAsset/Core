using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Core.Extensions;
using Core.Services;
using Core.Services.Framework;
using Core.Windows;

namespace Core.Views;

public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
        
        AttachedToVisualTree += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                AttachGlow(Profile, "ProfileHoverGlow");
                AttachGlow(Link, "LinkHoverGlow");
            }, DispatcherPriority.Loaded);
        };
    }
    
    private T? FindDescendant<T>(Visual root, string name) where T : Control
    {
        foreach (var child in root.GetVisualDescendants())
        {
            if (child is T match && match.Name == name)
            {
                return match;
            }
        }
        return null;
    }
    
    private void AttachGlow(Button button, string glowElementName)
    {
        if (button == null) return;

        var glow = FindDescendant<Grid>(this, glowElementName);
        if (glow == null) return;

        button.PointerEntered += (_, _) => FadeOpacity(glow, 1.0);
        button.PointerExited  += (_, _) => FadeOpacity(glow, 0.0);
    }
    
    private readonly Dictionary<Visual, CancellationTokenSource> _fadeTokens = new();

    private async void FadeOpacity(Visual target, double targetOpacity, int durationMs = 350)
    {
        if (target == null) return;

        if (_fadeTokens.TryGetValue(target, out var existingCts))
        {
            existingCts.Cancel();
            _fadeTokens.Remove(target);
        }

        var start = await Dispatcher.UIThread.InvokeAsync(() => target.Opacity);
        var end = targetOpacity;

        if (Math.Abs(start - end) < 0.001)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _fadeTokens[target] = cts;
        var token = cts.Token;

        const int stepMs = 16;
        var elapsed = 0;

        try
        {
            while (elapsed < durationMs && !token.IsCancellationRequested)
            {
                var time = elapsed / (double)durationMs;
                var eased = CubicEaseOut(time);
                var value = Math.Clamp(start + (end - start) * eased, 0, 1);

                target.Opacity = value;

                await Task.Delay(stepMs, token);
                elapsed += stepMs;
            }

            if (!token.IsCancellationRequested)
            {
                target.Opacity = end;
            }
        }
        catch (TaskCanceledException) { }
        
        finally
        {
            _fadeTokens.Remove(target);
        }
    }

    private static double CubicEaseOut(double t)
    {
        var p = t - 1;
        return p * p * p + 1;
    }
    
    private void EditProfile(object? sender, RoutedEventArgs e) => (VisualRoot as MainWindow)?.OnEditProfile(sender, e);
    private void EditLinkedProfile(object? sender, RoutedEventArgs e) => (VisualRoot as MainWindow)?.OnEditLinkedProfile(sender, e);

    private void RestartAPI(object? sender, RoutedEventArgs e)
    {
        AppServices.Cloud.Restart();
    }

    private LinkWindow? _linkWindow;

    private void Link_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_linkWindow is { IsVisible: true })
        {
            _linkWindow.Activate();
            _linkWindow.Focus();
            return;
        }

        if (this.GetVisualRoot() is not Window window)
        {
            return;
        }

        _linkWindow = new LinkWindow();
        _linkWindow.Closed += (_, _) => _linkWindow = null;
        _linkWindow.CenterToScreen(window);

        _ = _linkWindow.ShowDialog(window);
    }

    private void Button_OnClick(object? sender, RoutedEventArgs e)
    {
        if (MainWM.LinkedProfile is not null)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                MainWM.LinkedProfile.DisposeProvider();

                ProfileSelectionVM.UpdateProfileCard(MainWM.LinkedProfile);
                
                MainWM.LinkedProfile = null;
            });
        }
    }
}
