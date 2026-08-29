using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

using CommunityToolkit.Mvvm.ComponentModel;

using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;

using Core.Resources.Framework.CUEParse;
using Core.Resources.Validators;

using CUE4Parse.MappingsProvider;

namespace Core.Resources.Framework.Base;

public enum EAudioFormatType
{
    [Description("Decompressed Data")]
    Decompressed,
    
    [Description("Raw Data")]
    Raw
}

/* This base profile class is used to separate what's needed to shared, and what's visual (located in Core) */
/* This class is used by both Core.Cloud and Core */
public partial class BaseProfile : ObservableValidator
{
    /* The mappings as the game wrote them, and the same completed from the engine.
     *
     * A cooked package counts its properties through the short list a build without editor data
     * reflects, which is what the mappings hold. The optional segment written beside it by the
     * editor counts through the whole class. The same numbers mean different properties in each,
     * so whoever reads a package picks the one that describes it. */
    public ITypeMappingsProvider? CookedMappings { get; set; }

    public ITypeMappingsProvider? EditorMappings { get; set; }

    /* The LATEST Schema Version */
    private static readonly int LatestSchemaVersion = 1;
    
    /* This Profile's last saved Schema Version */
    [ObservableProperty] private int _schemaVersion = -1;
    
    public BaseProfile()
    {
        UpdateSchemaVersion();
        
        WireChildValidators();
        ValidateErrors();
    }

    public void UpdateSchemaVersion() => SchemaVersion = LatestSchemaVersion;
    
    [JsonIgnore]
    [ObservableProperty]
    private bool _hasValidationErrors;

    private void WireChildValidators()
    {
        MappingsContainer.ErrorsChanged += (_, _) => ValidateErrors();
        Encryption.ErrorsChanged += (_, _) => ValidateErrors();
        ErrorsChanged += (_, _) => ValidateErrors();
    }

    private void ValidateErrors()
    {
        HasValidationErrors =
            HasErrors ||
            (MappingsContainer.HasErrors && MappingsContainer.Override) ||
            Encryption.HasErrors;
    }

    partial void OnMappingsContainerChanged(BaseMappingsContainer value)
    {
        value.ErrorsChanged += (_, _) => ValidateErrors();
        ValidateErrors();
    }
    
    partial void OnEncryptionChanged(EncryptionContainer value)
    {
        value.ErrorsChanged += (_, _) => ValidateErrors();
        ValidateErrors();
    }
    
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Profile Name is required.")]
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsNameEmpty))]
    private string _name = "";

    [NotifyDataErrorInfo]
    [ArchiveDirectory]
    [ObservableProperty] private string _archiveDirectory = "";

    partial void OnArchiveDirectoryChanged(string value)
    {
        ArchiveDirectoryChanged();
    }

    protected virtual void ArchiveDirectoryChanged() { }

    partial void OnNameChanged(string value)
    {
        NameChanged();
    }
    
    protected virtual void NameChanged() { }

    [ObservableProperty] private BaseMappingsContainer _mappingsContainer = new();
    
    [ObservableProperty] private EncryptionContainer _encryption = new();
    
    [ObservableProperty] private EGame _version = EGame.GAME_UE5_LATEST;
    
    [ObservableProperty] private ETexturePlatform _texturePlatform = ETexturePlatform.DesktopMobile;
    
    [ObservableProperty] private EAudioFormatType _audioFormat = EAudioFormatType.Decompressed;
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(FileID))]
    private string _fileName = "";

    [JsonIgnore] public string FileID => FileName.Split(".json")[0];
    
    /* Detection ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoDetected))]
    private string _autoDetectedGameId = string.Empty;
    
    [JsonIgnore]
    public bool IsAutoDetected => AutoDetectedGameId != string.Empty;
    
    [JsonIgnore]
    public bool IsNameEmpty => string.IsNullOrEmpty(Name);
    
    [ObservableProperty] private bool _TexturesOnDemand = true;
    /* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
    
    [JsonIgnore] public BaseProvider Provider = null!;
    
    [JsonIgnore]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotInitializedAndIsActive))] 
    private BaseProfileStatus _status = new();
    
    [JsonIgnore]
    private bool _isInitialized;

    [JsonIgnore]
    public bool IsInitialized
    {
        get => _isInitialized;
        set
        {
            if (_isInitialized == value) return;
            _isInitialized = value;
                
            OnPropertyChanged(nameof(IsNotInitializedAndIsActive));
        }
    }

    [JsonIgnore]
    public bool IsNotInitializedAndIsActive => !IsInitialized && Status.State == EProfileStatus.Active;
    
    /* For using more than one profile */
    [JsonIgnore] public List<string> SecondaryAssetTypes { get; protected init; } = [];

    public void CheckStatusNotifies()
    {
        Status.OnStateChange = () =>
        {
            OnPropertyChanged(nameof(IsNotInitializedAndIsActive));
        };
    }
}
