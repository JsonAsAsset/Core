using Microsoft.Extensions.DependencyInjection;

using Core.ViewModels;
using Core.ViewModels.Profiles;
using Core.WindowModels;

namespace Core.Services.Framework;

public static class AppServices
{
    public static ServiceProvider Services = null!;
    
    public static void Initialize()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddCommonServices();
        serviceCollection.AddViewModels();

        Services = serviceCollection.BuildServiceProvider();
    }
    
    /* Services ~~~~~~~~~~~~~~~~~~~~~ */
    public static AppService App => Services.GetRequiredService<AppService>();
    public static PluginService Plugins => Services.GetRequiredService<PluginService>();
    public static TaskService Tasks => Services.GetRequiredService<TaskService>();
    public static SettingsService Settings => Services.GetRequiredService<SettingsService>();
    public static InfoService Info => Services.GetRequiredService<InfoService>();
    public static RestAPIService RestAPI => Services.GetRequiredService<RestAPIService>();
    public static UpdateService Update => Services.GetRequiredService<UpdateService>();
    public static CloudService Cloud => Services.GetRequiredService<CloudService>();
    public static NavigationService Navigation => Services.GetRequiredService<NavigationService>();
    public static DiscordService Discord => Services.GetRequiredService<DiscordService>();
    
    /* Models ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
    public static MainWindowModel MainWM => Services.GetRequiredService<MainWindowModel>();
    
    public static ProfileSelectionViewModel ProfileSelectionVM => Services.GetRequiredService<ProfileSelectionViewModel>();
    public static SettingsViewModel SettingsVM => Services.GetRequiredService<SettingsViewModel>();
}
