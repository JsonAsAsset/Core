using CommunityToolkit.Mvvm.ComponentModel;

using Core.Framework.Models;

namespace Core.ViewModels.Settings;

public partial class SerializationSettingsViewModel : ViewModelBase
{
    [ObservableProperty] private bool _readBlueprintBytecode = true;
    [ObservableProperty] private bool _readMaterialShaderMaps;

    partial void OnReadBlueprintBytecodeChanged(bool value)
    {
        if (MainWM.CurrentProfile is null || MainWM.CurrentProfile.Provider is null) return;

        MainWM.CurrentProfile.Provider.ReadScriptData = value;
    }

    partial void OnReadMaterialShaderMapsChanged(bool value)
    {
        if (MainWM.CurrentProfile is null || MainWM.CurrentProfile.Provider is null) return;

        MainWM.CurrentProfile.Provider.ReadShaderMaps = value;
    }
}
