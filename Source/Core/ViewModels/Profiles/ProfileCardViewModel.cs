using System;

using Avalonia.Media;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Core.Models;
using Core.ViewModels.Profiles.Framework;

namespace Core.ViewModels.Profiles;

public partial class ProfileCardViewModel : ProfileViewModelBase
{
    /* Events ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
    public event EventHandler? OnStart;
    public event EventHandler? OnEdit;
    public event EventHandler? OnDelete;

    public void Delete() => OnDelete?.Invoke(this, EventArgs.Empty);
    public void Edit() => OnEdit?.Invoke(this, EventArgs.Empty);
    public void Start() => OnStart?.Invoke(this, EventArgs.Empty);

    [ObservableProperty] private string _loadButtonText = "Load";
    [ObservableProperty] private float directoryMaxWidth = 230;

    public override void UpdateProfileProperties()
    {
        base.UpdateProfileProperties();

        UpdateLoadingText();
    }

    private void UpdateLoadingText()
    {
        LoadButtonText = IsRunning!.Value ? (Profile!.IsInitialized ? "Loaded" : "Loading") : "Load";
    }

    private DispatcherTimer? hoverTimer;

    private double _currentScale = 1;
    private double _targetScale = 1;

    private BoxShadow _currentShadow = new() { OffsetX = 0, OffsetY = 0, Blur = 5, Spread = 0, Color = Color.Parse("#38000000") };
    private BoxShadow _targetShadow = new() { OffsetX = 0, OffsetY = 0, Blur = 5, Spread = 0, Color = Color.Parse("#38000000") };

    private BoxShadows _boxShadow;
    public BoxShadows BoxShadow
    {
        get => _boxShadow;
        set => SetProperty(ref _boxShadow, value);
    }

    private double _scale = 1.0;
    public double Scale
    {
        get => _scale;
        set
        {
            if (SetProperty(ref _scale, value))
            {
                OnPropertyChanged(nameof(ScaledMoreUp));
            }
        }
    }
    
    public double ScaledMoreUp
    {
        get
        {
            if (Scale <= 1.0)
            {
                return 1.0;
            }

            var t = Math.Min((Scale - 1.0) / 0.5, 1.0);
            return 1.0 + t * 1.05;
        }
    }

    public void HoverEnter()
    {
        _targetScale = 1.055;
        StartHoverAnimation();
    }

    public void HoverExit()
    {
        _targetShadow = new BoxShadow
        {
            OffsetX = 0,
            OffsetY = 0,
            Blur = 5,
            Spread = 0,
            Color = Color.Parse("#38000000")
        };
        _targetScale = 1;
        StartHoverAnimation();
    }

    private void StartHoverAnimation()
    {
        hoverTimer?.Stop();
        hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        hoverTimer.Tick += (_, _) =>
        {
            var isComplete = true;

            var newBlur = Lerp(_currentShadow.Blur, _targetShadow.Blur, 0.2);
            if (Math.Abs(newBlur - _targetShadow.Blur) > 0.01)
                isComplete = false;

            var newColor = Color.FromArgb(
                (byte)Lerp(_currentShadow.Color.A, _targetShadow.Color.A, 0.2),
                (byte)Lerp(_currentShadow.Color.R, _targetShadow.Color.R, 0.2),
                (byte)Lerp(_currentShadow.Color.G, _targetShadow.Color.G, 0.2),
                (byte)Lerp(_currentShadow.Color.B, _targetShadow.Color.B, 0.2)
            );

            var newShadow = new BoxShadow
            {
                OffsetX = _currentShadow.OffsetX,
                OffsetY = _currentShadow.OffsetY,
                Blur = newBlur,
                Spread = _currentShadow.Spread,
                Color = newColor,
                IsInset = _currentShadow.IsInset
            };

            BoxShadow = new BoxShadows(newShadow);
            _currentShadow = newShadow;

            _currentScale = Lerp(_currentScale, _targetScale, 0.15);
            Scale = _currentScale;

            if (Math.Abs(_currentScale - _targetScale) > 0.001)
                isComplete = false;

            if (isComplete)
            {
                hoverTimer?.Stop();
                hoverTimer = null;
            }
        };
        hoverTimer.Start();
    }

    private static double Lerp(double from, double to, double by) => from + (to - from) * by;
    
    /* Initializer ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
    private EventHandler? clockHandler;

    /* Called from both the card constructor and the view model factory, so it has to be idempotent.
     * The clock is a static event, meaning a stray subscription outlives the card forever. */
    public override void Initialize()
    {
        if (clockHandler is not null) return;

        base.Initialize();

        clockHandler = (_, _) => OnPropertyChanged(nameof(LastUsed));
        RelativeTimeClock.Tick += clockHandler;
    }

    /* Detaches from the clock so a replaced card generation can be collected */
    public void Release()
    {
        if (clockHandler is null) return;

        RelativeTimeClock.Tick -= clockHandler;
        clockHandler = null;

        hoverTimer?.Stop();
        hoverTimer = null;
    }
}
