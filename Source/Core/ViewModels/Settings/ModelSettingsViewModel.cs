using CommunityToolkit.Mvvm.ComponentModel;

using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using CUE4Parse.UE4.Assets.Exports.Material;

using Core.Framework.Models;

namespace Core.ViewModels.Settings;

public partial class ModelSettingsViewModel : ViewModelBase
{
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsUEFormat))]
    [NotifyPropertyChangedFor(nameof(IsActorXFormat))]
    private EMeshFormat _format;
    
    public bool IsUEFormat => Format == EMeshFormat.UEFormat;
    public bool IsActorXFormat => Format == EMeshFormat.ActorX;
    
    [ObservableProperty] private EFileCompressionFormat _compressionFormat;
    
    [ObservableProperty] private ESocketFormat _socketFormat = ESocketFormat.None;
    
    /* OnlyNaniteLOD was renamed NaniteOnly upstream, same member */
    [ObservableProperty] private ENaniteMeshFormat _naniteFormat = ENaniteMeshFormat.NaniteOnly;
    
    /* EMaterialFormat became EMaterialDepth upstream, FirstLayer became TopLayerOnly */
    [ObservableProperty] private EMaterialDepth _materialFormat = EMaterialDepth.TopLayerOnly;

    [ObservableProperty] private ETextureFormat _textureFormat = ETextureFormat.Png;

    /* ELodFormat became EMeshQuality upstream, its first member still means the highest LOD */
    [ObservableProperty] private EMeshQuality _lodFormat;
    
    [ObservableProperty] private bool _embedMaterials;
    
    [ObservableProperty] private bool _saveMorphTargets = true;
}
