using CommunityToolkit.Mvvm.ComponentModel;

using Core.Framework.Models;
using Core.Models.Plugins;

namespace Core.WindowModels;

public partial class BuildLogWindowModel : WindowModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private UnrealProject project = new();

    /* Follows the tail unless the reader has scrolled away from it */
    [ObservableProperty] private bool autoScroll = true;

    public string Subtitle => $"{Project.EngineVersionLabel}  ·  {Project.PluginVersionLabel}";
}
