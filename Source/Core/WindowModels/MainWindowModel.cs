using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using FluentAvalonia.UI.Controls;
using FluentIcons.Common;
using Microsoft.WindowsAPICodePack.Taskbar;
using Core.Cloud.Controllers;
using Core.Controls.Profiles;
using Core.Framework.Models;
using Core.Models.Enums;
using Core.Models.Profiles;
using Core.Services;
using Core.Services.Framework;
using Core.Resources.Framework.Base;
using Core.Views;
using Core.Views.Profiles;

namespace Core.WindowModels;

/* ~~~ Main Window ViewModel ~~~ */
public partial class MainWindowModel : WindowModelBase
{
    public static InfoService Info => AppServices.Info;
    
    public string Title => CurrentProfile is not null ? $"{APP_NAME} - {CurrentProfile.Name}" : $"{APP_NAME}";

    /* ~~~ Observable State ~~~ */
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrentToolbarContentVisible))]
    private object? _currentToolbarContent;
    
    public bool IsCurrentToolbarContentVisible => CurrentToolbarContent is not null;

    [ObservableProperty]
    private AppStatus _status = AppStatus.Idle;
    
    [ObservableProperty]
    private string _textStatus = "";

    public void UpdateStatus(string status)
    {
        TextStatus = status;
    }
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileDisplayName))]
    [NotifyPropertyChangedFor(nameof(DoesProfileExist))]
    [NotifyPropertyChangedFor(nameof(IsProfileInitialized))]
    [NotifyPropertyChangedFor(nameof(CanLinkProfile))]
    [NotifyPropertyChangedFor(nameof(Title))]
    private Profile? _currentProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileButtonOpacity))]
    private double _titleBarOpacity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleBarSecondGradientBrush))]
    private Color _titleBarSecondGradientColor = Colors.White;

    public Brush TitleBarSecondGradientBrush => new SolidColorBrush(TitleBarSecondGradientColor);

    [ObservableProperty]
    private LinearGradientBrush _titleBarGradientBrush = null!;

    /* ~~~ Profile Info ~~~ */
    public string ProfileDisplayName => CurrentProfile?.Name ?? "Unknown";
    public bool DoesProfileExist => CurrentProfile is not null;
    public bool IsProfileInitialized => CurrentProfile?.IsInitialized ?? false;
    
    public double ProfileButtonOpacity => Math.Max(TitleBarOpacity, 0.5);
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DoesLinkedProfileExist))]
    [NotifyPropertyChangedFor(nameof(CanLinkProfile))]
    [NotifyPropertyChangedFor(nameof(LinkedProfileDisplayName))]
    private Profile? _linkedProfile;
    
    partial void OnLinkedProfileChanged(Profile? value)
    {
        CloudApiController.SetSecondaryProfile(value!);
    }
    
    public string LinkedProfileDisplayName => LinkedProfile?.Name ?? "Unknown";
    public bool DoesLinkedProfileExist => LinkedProfile is not null;
    public bool CanLinkProfile => LinkedProfile == null && CurrentProfile != null;

    /* ~~~ Lifecycle Methods ~~~ */
    public new void Initialize()
    {
        if (CurrentProfile is not null)
        {
            CurrentProfile.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(Profile.Name))
                {
                    OnPropertyChanged(nameof(ProfileDisplayName));
                }
            };
        }
        
        NavigateToStatus(AppStatus.Idle);
        
        DispatcherTimer.RunOnce(() => ProfileSelectionVM.RefreshAllAsync(), TimeSpan.FromSeconds(1.0));
    }

    /* ~~~ Profile Handling ~~~ */
    private void SetCurrentProfile(Profile? profile)
    {
        var lastProfile = CurrentProfile;
        
        if (profile is not null)
        {
            profile.ResetEvents();
        }
        
        CurrentProfile = profile;

        if (CurrentProfile is not null)
        {
            CloudApiController.SetProfile(CurrentProfile);
        }
        
        RefreshProfileProperties();

        lastProfile?.DisposeProvider();
    }

    public void RefreshProfileProperties()
    {
        if (CurrentProfile is null)
        {
            TitleBarOpacity = 0.0;
        }

        OnPropertyChanged(nameof(ProfileDisplayName));
        OnPropertyChanged(nameof(DoesProfileExist));
        OnPropertyChanged(nameof(IsProfileInitialized));

        UpdateGradientBrush();
    }

    public void OnNavigationItemSelected(Type pageType)
    {
        CurrentToolbarContent = pageType == typeof(ProfileSelectionView) ? new ProfileSelectionViewToolbar() : null;
    }

    public void RequestEditProfile()
    {
        CurrentProfile?.OpenEditor();
    }
    
    public void RequestEditLinkedProfile()
    {
        LinkedProfile?.OpenEditor();
    }
    
    /* ~~~ Status Transitions ~~~ */
    public void NavigateToStatus(AppStatus newStatus)
    {
        var previousStatus = Status;
        Status = newStatus;

        switch (newStatus)
        {
            case AppStatus.Idle:
            {
                Discord.UpdateState("Idle");
                Discord.UpdateDetails("");
                
                if (previousStatus == AppStatus.Active)
                {
                    if (CurrentProfile is not null)
                    {
                        SetCurrentProfile(null);
                    }

                    StopTitleBarBeatEffect();
                }
            }
            break;

            case AppStatus.Active:
            {
                UpdateDiscordDetailStatus();
                Discord.UpdateState("Active");
                
                StartTitleBarBeatEffect();
            }
            break;

            default:
            {
                throw new ArgumentOutOfRangeException(nameof(newStatus), newStatus, null);
            }
        }
    }

    /* ~~~ Title Bar Animation ~~~ */
    private bool isBeating;
    private DispatcherTimer? beatTimer;
    private double beatTime;
    private double lastKnownOpacity = 1.0;
    
    private const double BeatIncrement = 0.1;
    private static readonly TimeSpan BeatInterval = TimeSpan.FromMilliseconds(7);

    private void StartTitleBarBeatEffect()
    {
        if (isBeating || Status == AppStatus.Idle)
        {
            return;
        }

        isBeating = true;
        beatTime = 0;
        TitleBarOpacity = 0.0;

        if (beatTimer is null)
        {
            beatTimer = new DispatcherTimer { Interval = BeatInterval };
            beatTimer.Tick += (_, _) =>
            {
                if (!isBeating)
                {
                    return;
                }

                beatTime += BeatIncrement;
                var t = (Math.Sin(beatTime) + 1) / 2;
                var eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 4;
                var opacity = 0.0 + eased * 0.7;

                TitleBarOpacity = opacity;
                lastKnownOpacity = opacity;
            };
        }

        beatTimer.Start();
    }

    private void StopTitleBarBeatEffect()
    {
        beatTimer?.Stop();
        beatTimer = null;

        if (Status == AppStatus.Idle)
        {
            TitleBarOpacity = 0.0;
            isBeating = false;
            
            return;
        }

        if (!isBeating)
        {
            return;
        }

        isBeating = false;
        RefreshProfileProperties();

        var stopwatch = Stopwatch.StartNew();
        const double duration = 0.475;

        var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        fadeTimer.Tick += (_, _) =>
        {
            var t = Math.Clamp(stopwatch.Elapsed.TotalSeconds / duration, 0, 1);
            var eased = t < 0.5 ? 2 * t * t : -1 + (4 - 2 * t) * t;
            var startOpacity = lastKnownOpacity;
            var newOpacity = startOpacity + (1.0 - startOpacity) * eased;

            TitleBarOpacity = newOpacity;

            if (t >= 1)
            {
                fadeTimer.Stop();
            }
        };

        fadeTimer.Start();
    }

    private void UpdateGradientBrush()
    {
        if (CurrentProfile is null)
        {
            TitleBarOpacity = 0.0;
            
            return;
        }

        var color = CurrentProfile?.Display.GradientBrush!.GradientStops[0].Color ?? Color.Parse("#fc0388");

        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
        if (luminance < 0.15)
        {
            color = Colors.White;
        }

        TitleBarSecondGradientColor = color;

        TitleBarGradientBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(100, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(350, 0, RelativeUnit.Absolute),
            GradientStops =
            [
                new GradientStop(Color.Parse("#0d0d0d"), 0.02),
                new GradientStop(color, 0.25),
                new GradientStop(color, 0.5),
                new GradientStop(Color.Parse("#0d0d0d"), 1.0)
            ]
        };

        OnPropertyChanged(nameof(MainWM.TitleBarGradientBrush));
        OnPropertyChanged(nameof(TitleBarSecondGradientBrush));
    }
    
    public static bool IsAPIServiceEnabled => Settings is not null && Settings.Cloud.RunHostedAPI;
    public static bool IsAPIServiceRunning => AppServices.Cloud.API is not null && AppServices.Cloud.API!.IsRunning && !AppServices.Cloud.API!.HasErrored;
    public static bool IsAPIServiceErrored => AppServices.Cloud.API is not null && AppServices.Cloud.API!.HasErrored;
    
    public static IBrush APIServiceStatusColor => IsAPIServiceErrored ? Brush.Parse("#ff0055") : IsAPIServiceRunning ? Brushes.White : Brush.Parse("#b3b3b3");
    public static Icon APIServiceIcon => IsAPIServiceErrored ? Icon.CloudError : IsAPIServiceRunning ? Icon.Cloud : Icon.CloudOff;
    
    public void UpdateAPIServiceEnabled()
    {
        OnPropertyChanged(nameof(IsAPIServiceEnabled));
    }
    
    public void UpdateAPIServiceStatusColor()
    {
        OnPropertyChanged(nameof(IsAPIServiceRunning));
        OnPropertyChanged(nameof(IsAPIServiceErrored));
        OnPropertyChanged(nameof(APIServiceStatusColor));
        OnPropertyChanged(nameof(APIServiceIcon));
    }
    
    private CancellationTokenSource? LastProfileCancellationTokenSource = new();
    
    public async Task StartProfileAsync(Profile profile)
    {
        if (CurrentProfile is not null)
        {
            CurrentProfile.DisposeProvider();
            
            await Dispatcher.UIThread.InvokeAsync(() => ProfileSelectionVM.UpdateProfileCard(CurrentProfile));
        }

        if (profile is null)
        {
            return;
        }
        
        var oldTokenSource = LastProfileCancellationTokenSource;
        LastProfileCancellationTokenSource = new CancellationTokenSource();
        _ = oldTokenSource?.CancelAsync()!;
        oldTokenSource?.Dispose();
        
        SetCurrentProfile(profile);
        RefreshProfileProperties();

        if (CurrentProfile is null)
        {
            return;
        }
        
        CurrentProfile.Display.LastUsed = DateTime.Now;
        _ = CurrentProfile.Save();

        CurrentProfile.CheckStatusNotifies();
        CurrentProfile.Status.SetState(EProfileStatus.Active);
        CurrentProfile.IsInitialized = false;

        _ = Dispatcher.UIThread.InvokeAsync(() => ProfileSelectionVM.UpdateProfileCard(CurrentProfile));
        App.SetAppProgressState(TaskbarProgressBarState.Indeterminate);

        NavigateToStatus(AppStatus.Active);
        
        CurrentProfile.OnInitialized += OnProfileInitialized;
        CurrentProfile.OnInitializationFailure += OnProfileInitializationFailure;

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            await CurrentProfile.Initialize(LastProfileCancellationTokenSource!.Token);
        });
    }

    private void OnProfileInitialized(Profile inProfile)
    {
        if (inProfile.FileName != CurrentProfile!.FileName) return;
        
        Dispatcher.UIThread.InvokeAsync(StopTitleBarBeatEffect);
        Dispatcher.UIThread.InvokeAsync(() => ProfileSelectionVM.UpdateProfileCard(CurrentProfile));
        App.SetAppProgressState(TaskbarProgressBarState.NoProgress);

        UpdateDiscordDetailStatus();
        OnPropertyChanged(nameof(IsProfileInitialized));
    }
    
    private async void OnProfileInitializationFailure(Profile inProfile)
    {
        if (inProfile.FileName != CurrentProfile!.FileName) return;
        
        NavigateToStatus(AppStatus.Idle);
        App.SetAppProgressState(TaskbarProgressBarState.Error);
                    
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ProfileSelectionVM.UpdateProfileCard(inProfile);
            
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
                            DataContext = inProfile
                        },
                        new TextBlock
                        {
                            Text = $"{inProfile.Name} failed to load",
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 0, 5)
                        }
                    }
                },
                Content = inProfile.Status.FailureReason,
                CloseButtonText = "Dismiss",
                PrimaryButtonText = "Edit Profile",
                PrimaryButtonCommand = new RelayCommand(() =>
                {
                    inProfile.OpenEditor();
                })
            };
            
            _ = dialog.ShowAsync(MainWM.Window);
        });
    }

    private void UpdateDiscordDetailStatus()
    {
        var details = IsProfileInitialized
            ? $"{CurrentProfile!.Name} — Loaded"
            : $"{CurrentProfile!.Name} — Idling";

        Discord.UpdateDetails(details);
    }
    
    private static string FormatNumber(long number)
    {
        return number switch
        {
            >= 1_000_000_000 => $"{number / 1_000_000_000.0:F1} billion",
            >= 1_000_000 => $"{number / 1_000_000.0:F1} million",
            _ => number.ToString("N0")
        };
    }
}
