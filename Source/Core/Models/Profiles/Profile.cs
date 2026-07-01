using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.Input;

using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.AssetRegistry.Objects;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;

using FluentAvalonia.UI.Controls;

using Microsoft.IdentityModel.Tokens;

using Serilog;

using Core.API.UEDB.API.Responses;
using Core.Extensions;
using Core.Models.Profiles.Display;
using Core.Plugins;
using Core.Plugins.Interfaces;
using Core.Plugins.Resolvers;
using Core.Services.Framework;
using Core.Resources.Extensions;
using Core.Resources.Framework.Base;
using Core.Resources.Framework.CUEParse;
using Core.Plugins.OnDemand;
using Core.Resources.Migration;
using Core.Windows;
using CUE4Parse.MappingsProvider.Usmap;

namespace Core.Models.Profiles;

public class Profile : BaseProfileDisplay
{
    [JsonIgnore] public List<IPlugin> Plugins = [];
    
    public void ResolvePluginHandler()
    {
        Plugins = [];

        foreach (var Plugin in AppServices.Plugins.List)
        {
            if (Plugin is not IGamePlugin GamePlugin) continue;
            if (!GamePlugin.Match(this, out var reason)) continue;
            
            Plugins.Add(Plugin);
            Log.Information($"{Name}: Added Plugin \"{Plugin.Name}\" [{reason}]");
        }
        
        foreach (var Plugin in Plugins)
        {
            if (Plugin is not (IGameVersionUpdatePlugin archiveResolverPlugin and IGamePlugin gamePlugin)) continue;

            if (gamePlugin.DoesInherentlyMatch(this))
            {
                archiveResolverPlugin.Update(this);
            }
        }
    }

    public async Task ResolveDataFromArchives(bool Voluntary)
    {
        if (Plugins.Count == 0)
        {
            ResolvePluginHandler();
        }
        
        foreach (var Plugin in Plugins)
        {
            if (Plugin is not (IArchiveResolverPlugin archiveResolverPlugin and IGamePlugin gamePlugin)) continue;

            if (gamePlugin.DoesInherentlyMatch(this) || Voluntary)
            {
                await archiveResolverPlugin.Resolve(this);
            }
        }

        if (Voluntary)
        {
            await InitializeProvider();
            InitializeCache(false);
        
            Validate();
        
            DisposeProvider(false);
        }
    }
    
    [JsonIgnore] public readonly List<FAssetData> AssetRegistry = [];
    [JsonIgnore] public Action<Profile>? OnInitialized { get; set; }
    [JsonIgnore] public Action<Profile>? OnInitializationFailure { get; set; }

    public void ResetEvents()
    {
        OnInitialized = null;
        OnInitializationFailure = null;
    }
    
    public async Task Initialize(CancellationToken cancellationToken = default)
    {
        if (HasValidationErrors)
        {
            const string message = "Profile has errors. Modify the profile to continue.";

            Status.OnFailure(message);
            OnInitializationFailure?.Invoke(this);
            
            return;
        }
        
        CheckStatusNotifies();
        
        cancellationToken.ThrowIfCancellationRequested();

        Status.SetState(EProfileStatus.Active);
        
        UpdateStatus("Loading Native Libraries");
        
        IsInitialized = false;
        Log.Information($"Initializing profile {Name}.. ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~");

        UpdateStatus("Loading Files");
        
        cancellationToken.ThrowIfCancellationRequested();
        
        await InitializeProvider();
        LoadMappings(cancellationToken);
        
        cancellationToken.ThrowIfCancellationRequested();
        
        InitializeCache();

        cancellationToken.ThrowIfCancellationRequested();

        await InitializeTextureStreaming();
        await LoadKeys(cancellationToken);
        
        if (Provider is not null && Provider.Files.Count == 0 && Encryption.MainKey == EMPTY_CHAR && Provider.Keys.Count == 0)
        {
            Status.OnFailure("Please enter a valid AES encryption key in the profile settings.");
            OnInitializationFailure?.Invoke(this);
            
            return;
        }
        
        if (Provider is not null && Provider.Files.Count == 0)
        {
            Status.OnFailure("No files were found in the archive or the selected folder.");
            OnInitializationFailure?.Invoke(this);
            
            return;
        }

        if (Provider is not null)
        {
            await Task.Run(() => SetLanguage(Settings.Application.GameLanguage), cancellationToken);
            await Task.Run(() => Provider.LoadVirtualPaths(), cancellationToken);
            
            cancellationToken.ThrowIfCancellationRequested();
        }
        
        cancellationToken.ThrowIfCancellationRequested();
        
        IsInitialized = true;
        
        cancellationToken.ThrowIfCancellationRequested();
        
        Log.Information($"Initialized profile {Name} successfully");
        Info.Message($"Loaded profile {Name} successfully", "", InfoBarSeverity.Success, closeTime: 0.95f);
        UpdateStatus(string.Empty);
        
        OnInitialized?.Invoke(this);
    }

    public void SetLanguage(ELanguage language)
    {
        Log.Information(!Provider.TryChangeCulture(Provider.GetLanguageCode(language))
            ? $"Failed to load language \"{language.GetDescription()}\""
            : $"Changed profile's provider language to \"{language.GetDescription()}\"");
    }
    
    private async Task InitializeTextureStreaming()
    {
        var onDemandPlugin = Plugins.OfType<IOnDemandPlugin>().FirstOrDefault();

        if (onDemandPlugin != null)
        {
            await onDemandPlugin.InitializeStreaming(this);
        }
    }

    public void InitializeCache(bool shouldSave = true)
    {
        if (Provider is null) return;
        
        var previousPakFiles = PakFileEntries;
        PakFileEntries = GetPakFiles();

        if (!previousPakFiles.OrderBy(x => x.FileName).SequenceEqual(PakFileEntries.OrderBy(x => x.FileName)))
        {
            if (shouldSave)
            {
                _ = Save();
            }
        }
            
        var keysToRemove = new List<EncryptionKey>();

        foreach (var unknownKey in Encryption.UnknownKeys)
        {
            var pakFile = PakFileEntries.FirstOrDefault(p => p.Guid == unknownKey.Guid);
            if (pakFile is null) continue;
                
            var newKey = new EncryptionKey()
            {
                Guid = unknownKey.Guid,
                Key = unknownKey.Key
            };
                    
            Encryption.Keys.Add(newKey);
            keysToRemove.Add(unknownKey);
        }

        if (keysToRemove.Count > 0)
        {
            if (shouldSave)
            {
                _ = Save();
            }
        }

        foreach (var key in keysToRemove)
        {
            Encryption.UnknownKeys.Remove(key);
        }
    }
    
    public void UpdateStatus(string status) => MainWM.UpdateStatus(status);
    
    private async Task InitializeProvider()
    {
        if (ArchiveDirectory.Length != 0)
        {
            Provider = new BaseProvider(ArchiveDirectory, new VersionContainer(Version, TexturePlatform));
        }
        
        var onDemandPlugin = Plugins.OfType<IOnDemandPlugin>().FirstOrDefault();

        if (onDemandPlugin != null)
        {
            await onDemandPlugin.PreInitialize(this);
        }
        
        if (onDemandPlugin != null)
        {
            await onDemandPlugin.SetupProvider(this);
        }

        if (!Encryption.IsValid) Encryption.MainKey = EMPTY_CHAR;
        
        Provider.ReadScriptData = Settings.Serialization.ReadBlueprintBytecode;
        Provider.ReadShaderMaps = Settings.Serialization.ReadMaterialShaderMaps;
        Provider.ReadNaniteData = true;

        if (onDemandPlugin != null)
        {
            await onDemandPlugin.Initialize(this);
        }
        else
        {
            Provider.Initialize();
        }
    }
    
    private async Task LoadKeys(CancellationToken cancellationToken = default)
    {
        if (Provider is not null)
        {
            await Provider.SubmitKeyAsync(ZERO_GUID, Encryption.MainAESKey);
            Log.Information($"Submitted AES Key: {Encryption.MainAESKey}");
            
            cancellationToken.ThrowIfCancellationRequested();
            
            if (Encryption.HasKeys && Provider is not null)
            {
                foreach (var vfs in Provider.UnloadedVfs.ToArray())
                {
                    foreach (var extraKey in Encryption.Keys.Where(extraKey => extraKey.IsValid && extraKey.Key != "").Where(extraKey => vfs.TestAesKey(extraKey.AESKey)))
                    {
                        if (Provider is null) continue;
                        
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        await Provider.SubmitKeyAsync(vfs.EncryptionKeyGuid, extraKey.AESKey);
                        Log.Information($"Submitted Dynamic AES Key: {extraKey.AESKey}");
                    }
                }
            }
            
            cancellationToken.ThrowIfCancellationRequested();
            Provider!.PostMount();
        }
    }
    
    private async void LoadMappings(CancellationToken cancellationToken = default)
    {
        MappingsResponse? mapping = null;
        try
        {
            mapping = await API.UEDB.Globals.API.FetchMappingAsync(token: cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to fetch online mappings {ex.Message}");
        }

        if (Provider is null) return;

        cancellationToken.ThrowIfCancellationRequested();

        var MappingFile = MappingsContainer.Path;

        if (!MappingsContainer.Override && string.IsNullOrEmpty(MappingFile) || !File.Exists(MappingFile))
        {
            MappingFile = mapping is { LocalPath: not null } ? mapping.LocalPath : GetLocallyRecentCreatedMappings();
        }
        
        cancellationToken.ThrowIfCancellationRequested();

        if (MappingFile is not null && File.Exists(MappingFile))
        {
            Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(MappingFile);
        
            Log.Information($"Loaded Mappings: {MappingFile}");
        }
    }
    
    private static string? GetLocallyRecentCreatedMappings()
    {
        if (!UEDBMappingsFolder.Exists)
        {
            return null;
        }
        
        var usmapFiles = UEDBMappingsFolder.GetFiles("*.usmap");
        return usmapFiles.Length <= 0 ? null : usmapFiles.MaxBy(x => x.CreationTime)?.FullName;
    }
    
    private async Task LoadAssetRegistries(CancellationToken cancellationToken = default)
    {
        var assetRegistries = Provider.Files.Where(x => x.Key.Contains("AssetRegistry", StringComparison.OrdinalIgnoreCase)).ToArray();
        
        foreach (var (path, file) in assetRegistries)
        {
            if (!path.EndsWith(".bin") || path.Contains("Plugin", StringComparison.OrdinalIgnoreCase) || path.Contains("Editor", StringComparison.OrdinalIgnoreCase)) continue;

            UpdateStatus($"Loading {file.Name}");
            var assetArchive = await file.SafeCreateReaderAsync();
            if (assetArchive is null) continue;

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var assetRegistry = new FAssetRegistryState(assetArchive);
                AssetRegistry.AddRange(assetRegistry.PreallocatedAssetDataBuffers);
                Log.Information("Loaded Asset Registry: {FilePath}", file.Path);
            }
            catch (Exception e)
            {
                Log.Warning("Failed to load asset registry: {FilePath}", file.Path);
                Log.Error(e.ToString());
            }
        }
    }
    
    /* File IO ~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
    public async Task Save(bool bSyncToFModel = true)
    {
        if (Globals.RedactProfiles)
#pragma warning disable CS0162 // Unreachable code detected
        {
            return;
        }
#pragma warning restore CS0162 // Unreachable code detected
        
        Directory.CreateDirectory(ProfilesFolder.ToString());

        if (IsAutoDetected)
        {
            FileName = Name + ".json";
        }
        else
        {
            if (FileName.IsNullOrEmpty())
            {
                FileName = GenerateRandomHash() + ".json";
            }
        }

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(ProfilesFolder.ToString(), FileName), json);

        if (Settings.Connections.SyncFModel && bSyncToFModel)
        {
            _ = SyncToFModel.Save();
        }
    }

    public void Delete()
    {
        if (File.Exists(SavedFilePath))
        {
            File.Delete(SavedFilePath);
            Log.Information($"Deleted Profile file: {SavedFilePath}");
        }
        else
        {
            Log.Information($"Profile settings file not found: {SavedFilePath}");
        }
    }
    
    private static string GenerateRandomHash()
    {
        return Guid.NewGuid().ToString("N")[..5];
    }
    
    public Profile Clone()
    {
        return new Profile
        {
            Name = Name,
            TexturePlatform = TexturePlatform,
            AudioFormat = AudioFormat,
            ArchiveDirectory = ArchiveDirectory,
            MappingsContainer = MappingsContainer.Clone(),
            Encryption = Encryption.Clone(),
            Version = Version,
            FileName = FileName,
            AutoDetectedGameId = AutoDetectedGameId,
            PakFileEntries = [..PakFileEntries],
            Display = Display.Clone(),
            Status = Status,
            IsInitialized = true,
            SecondaryAssetTypes = [..SecondaryAssetTypes],
            EnableDisplayLinks = true
        };
    }
    
    public Profile LazyClone()
    {
        return new Profile
        {
            Name = Name,
            ArchiveDirectory = ArchiveDirectory,
            Version = Version,
            FileName = FileName
        };
    }
    
    public void CopyFrom(Profile other)
    {
        Name = other.Name;
        ArchiveDirectory = other.ArchiveDirectory;
        TexturePlatform = other.TexturePlatform;
        AudioFormat = other.AudioFormat;
        MappingsContainer = other.MappingsContainer;
        Encryption = other.Encryption;
        Version = other.Version;
        FileName = other.FileName;
        AutoDetectedGameId = other.AutoDetectedGameId;
        PakFileEntries = new List<BasePakFileEntry>(other.PakFileEntries);
        Display = other.Display;
        SecondaryAssetTypes.Clear();
        SecondaryAssetTypes.AddRange(other.SecondaryAssetTypes);
        EnableDisplayLinks = true;
    }
    
    public bool Compare(Profile other)
    {
        if (other is null) return false;

        return 
           /* Allows user to change name without restart
           string.Equals(Name, other.Name, StringComparison.Ordinal) && 
           */
           string.Equals(ArchiveDirectory, other.ArchiveDirectory, StringComparison.Ordinal)
           && Equals(MappingsContainer, other.MappingsContainer)
           && string.Equals(MappingsContainer.Path, other.MappingsContainer.Path, StringComparison.Ordinal)
           && Equals(MappingsContainer.Override, other.MappingsContainer.Override)
           && Equals(Encryption, other.Encryption)
           && Equals(TexturePlatform, other.TexturePlatform)
           && Equals(Version, other.Version);
    }
    
    public void DisposeProvider(bool setStatus = true)
    {
        if (Provider is not null)
        {
            Provider.UnloadNonStreamedVfs();
            Provider.Dispose();

            Log.Information($"Disposed Provider for {Name}");
        }
        
        Provider = null!;

        if (setStatus)
        {
            CheckStatusNotifies();
            Status.SetState(EProfileStatus.Idle);
            IsInitialized = false;
        }
        
        GC.Collect();
    }

    public static async Task<List<Profile>> LoadAllAsync()
    {
        Directory.CreateDirectory(ProfilesFolder.ToString());

        var profileFiles = Directory.GetFiles(ProfilesFolder.ToString(), "*.json");

        var tasks = profileFiles.Select(async file =>
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var profileSetting = JsonSerializer.Deserialize<Profile>(json);

                if (profileSetting is null) return null;

                AppDataMigration.ApplyToProfile(profileSetting);

                profileSetting.Display.Profile = profileSetting;
                profileSetting.Display.Splash.Profile = profileSetting;

                if (string.IsNullOrEmpty(profileSetting.Display.GradientStartColor) || string.IsNullOrEmpty(profileSetting.Display.GradientEndColor))
                {
                    profileSetting.Display.SetRandomGradient();
                }
                
                profileSetting.EnableDisplayLinks = true;
                profileSetting.ResolvePluginHandler();

                if (Globals.RedactProfiles)
#pragma warning disable CS0162 // Unreachable code detected
                {
                    var names = new[]
                    {
                        "Profile"
                    };
            
                    var random = new Random();
            
                    profileSetting.Name = names[random.Next(names.Length)];
                    profileSetting.Display.SetRandomGradient();
                    profileSetting.ArchiveDirectory = @"D:\Builds\Profile\Content\Paks";
                }
#pragma warning restore CS0162 // Unreachable code detected
                
                Log.Information($"Loaded Profile {profileSetting.Name}");

                return profileSetting;
            }
            catch (Exception ex)
            {
                Log.Information($"Error loading profile settings from {file}: {ex.Message}");
                
                return null;
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(profile => profile is not null).ToList()!;
    }
    
    public bool TryParseProfileName(string name, out double value)
    {
        var normalized = name;

        if (name.Count(c => c == '.') == 2)
        {
            var parts = name.Split('.');
            if (parts.Length == 3)
            {
                normalized = parts[0] + "." + parts[1] + parts[2];
            }
        }

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            Log.Information($"Could not parse Profile Name {name}");
            return false;
        }

        return true;
    }
    
    public EGame PredictBaseUEVersion(string name)
    {
        if (!TryParseProfileName(name, out var version))
        {
            return EGame.GAME_UE5_7;
        }

        var major = Math.Floor(version);

        switch (major)
        {
            case < 1.10:
            {
                return EGame.GAME_UE4_16;
            }
            case < 2.5:
            {
                return EGame.GAME_UE4_19;
            }
            case < 7.0:
            {
                return EGame.GAME_UE4_21;
            }
            case < 8.1:
            {
                return EGame.GAME_UE4_22;
            }
            case < 13.0:
            {
                return EGame.GAME_UE4_23;
            }
        }

        const double minVersion = 1.0;
        const double maxVersion = 36.30;
        var t = Math.Clamp((version - minVersion) / (maxVersion - minVersion), 0.0, 1.0);

        var baseGames = Enum.GetValues(typeof(EGame))
            .Cast<EGame>()
            .Where(g => ((uint)g & 0xFFFF) == 0) 
            .Select(g => (uint)g)
            .OrderBy(v => v)
            .ToArray();

        var startGame = baseGames.First();
        var endGame = baseGames.Last();
        var lerped = startGame + (endGame - startGame) * t;
        var snapped = baseGames.OrderBy(v => Math.Abs(v - lerped)).First();

        return (EGame)snapped;
    }
    
    public void Validate()
    {
        ValidateAllProperties();
    }
    
    public async Task BrowseArchiveDirectoryPath()
    {
        if (await App.BrowseFolderDialog(ArchiveDirectory) is { } path)
        {
            ArchiveDirectory = path;
        }
    }
    
    public async Task BrowseMappingsPathFile()
    {
        if (await App.BrowseFileDialog(fileTypes: Globals.MappingsFileType, suggestedFileName: MappingsContainer.Path) is { } path)
        {
            MappingsContainer.Path = path;
        }
    }

    public void OpenEditor(Window window = null!)
    {
        /* ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract */
        window ??= MainWM.Window;

        var win = new ProfileEditorWindow(this);
        win.CenterToScreen(window);
        _ = win.ShowDialog(window);
    }
    
    public static void CreateNewProfile(Window window = null!)
    {
        var newProfile = new Profile
        {
            Status =
            {
                State = EProfileStatus.Uncompleted
            }
        };
        
        newProfile.Display.SetRandomGradient();
        newProfile.OpenEditor(window);
    }
    
    public static IRelayCommand<Window> CreateNewProfileCommand { get; } = new RelayCommand<Window>(CreateNewProfile!);
    
    public void OpenInFileExplorer(Window window = null!)
    {
        if (File.Exists(SavedFilePath))
        {
            var argument = $"/select,\"{SavedFilePath}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
    }
    
    public (bool IsNumeric, decimal? NumericVersion, string NameLower) GetSortKey()
    {
        if (decimal.TryParse(Name, NumberStyles.Number, CultureInfo.InvariantCulture, out var version))
        {
            return (true, version, string.Empty);
        }

        return (false, null, Name.ToLowerInvariant());
    }

    public static List<Profile> SortProfiles(List<Profile> Profiles)
    {
        return Profiles.OrderByDescending(p => !p.GetSortKey().IsNumeric)
            .ThenByDescending(p => p.GetSortKey().NumericVersion)
            .ThenBy(p => p.GetSortKey().NameLower)
            .ToList();
    }
}
