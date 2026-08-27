using Core.Framework.Models;
using Core.Services.Framework;
using Core.ViewModels.Settings;

namespace Core.Views;

public partial class PluginView : ViewBase<PluginSettingsViewModel>
{
    public PluginView() : base(AppServices.Settings.Plugin)
    {
        InitializeComponent();
    }
}
