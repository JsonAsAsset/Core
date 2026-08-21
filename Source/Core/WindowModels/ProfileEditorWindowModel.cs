using System;
using System.IO;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.IdentityModel.Tokens;

using Core.Converters.Enum;
using Core.Models.Profiles;
using Core.Resources.Framework.Base;
using Core.Resources.Framework.CUEParse;
using Core.Plugins.Resolvers;
using Core.ViewModels.ProfileEditor;

namespace Core.WindowModels;

/* ~~~ ProfileEditorWindowModel ~~~ */
public partial class ProfileEditorWindowModel : ProfileEditorViewModel
{
    /* ~~~ State ~~~ */
    public Profile OriginalProfile { get; set; } = null!;

    public void CloseWindow()
    {
        OnClose?.Invoke();
    }

    [ObservableProperty] private bool _isUserInterfaceEnabled = true;

    [ObservableProperty] private bool _hasArchiveResolver;

    /* ~~~ Observable Properties ~~~ */
    [ObservableProperty] 
    private string? _titleBarText = "";
    
    /* ~~~ Computed Properties ~~~ */
    public object SaveChangesText => 
        Profile?.Status.State == EProfileStatus.Uncompleted ? "Create" : 
        Profile?.Status.State == EProfileStatus.Active ? "Save & Load" : 
        "Save Changes";

    public bool? ShowEncryptionTab => Profile?.PakFileEntries.Count > 0;

    /* ~~~ Constructor ~~~ */
    public ProfileEditorWindowModel()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Profile))
            {
                OnProfileChanged();
                
                Profile!.PropertyChanged += (_, ProfileArgs) =>
                {
                    if (ProfileArgs.PropertyName == nameof(Profile.Name))
                    {
                        OnProfileNameChanged();
                    }
                };
            }
        };
    }
    
    /* ~~~ Events ~~~ */
    public event Action<Profile, Profile, bool>? ProfileSaved;
    public event Action? OnClose;

    public void Reset()
    {
        ProfileSaved = null;
        PakKeyEntries = [];
    }

    /* ~~~ Initialization ~~~ */
    public override void Initialize()
    {
        base.Initialize();
        
        Profile!.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Profile.ArchiveDirectory))
            {
                OnArchiveDirectoryChanged();
            }
        };
    }

    private void OnArchiveDirectoryChanged()
    {
        Profile!.ResolvePluginHandler();
        
        if (Profile is null ||
            /* This operation is to fill the name if it is empty using the archive directory */
            !Profile.Name.IsNullOrEmpty()
            || Profile.ArchiveDirectory.IsNullOrEmpty()
            || !Directory.Exists(Profile.ArchiveDirectory)) return;
        
        var parts = Profile.ArchiveDirectory.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        string? version = null;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!parts[i].Equals("Content", StringComparison.OrdinalIgnoreCase) || !parts[i + 1].Equals("Paks", StringComparison.OrdinalIgnoreCase)) continue;
            
            if (i >= 2)
            {
                version = parts[i - 2];
            }
            
            break;
        }

        if (version is not null)
        {
            Profile.Name = version;

            Profile.Version = Profile.PredictBaseUEVersion(Profile.Name);
            SelectedVersionName = Profile.Version.ToString()[5..];
        }
    }

    private void OnProfileChanged()
    {
        OnPropertyChanged(nameof(SaveChangesText));
        OnPropertyChanged(nameof(ShowEncryptionTab));

        OnProfileNameChanged();
    }
    
    private void OnProfileNameChanged()
    {
        TitleBarText = Profile!.IsNameEmpty ? "" : $"Editing {Profile.Name}";
        Profile.ResolvePluginHandler();
        HasArchiveResolver = Profile!.Plugins.Any(p => p is IArchiveResolverPlugin);
    }

    public void Save()
    {
        if (Profile is null || Profile.HasValidationErrors || SelectedVersionName is null || !IsUserInterfaceEnabled)
        {
            return;
        }
        
        if (EGameNameConverter.TryParse(SelectedVersionName, out var selectedGame))
        {
            Profile.Version = selectedGame;
        }

        Profile.Encryption.Keys = PakKeyEntries
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Key) ||
                Profile.Encryption.Keys.Any(k =>
                    !string.IsNullOrWhiteSpace(k.Key) &&
                    k.Key != entry.Key))
            /* Keyed on the guid, which is what a key is actually identified by. Grouping on the file
             * name collapsed every entry whose container is missing into one group, and dropped all
             * but the first of them on save. */
            .GroupBy(entry => entry.Guid)
            .Select(group => group.First())
            .Select(entry => new EncryptionKey
            {
                Key = entry.Key,
                Guid = entry.Guid
            }).ToList();

        ProfileSaved?.Invoke(OriginalProfile, Profile, IsUncompletedProfile);
    }
}
