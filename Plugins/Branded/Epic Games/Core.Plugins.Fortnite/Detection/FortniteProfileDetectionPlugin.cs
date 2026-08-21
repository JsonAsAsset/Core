using CUE4Parse.UE4.Versions;

using Core.Plugins.EpicGames.Detection;
using Core.Resources.Framework.Base;
using Core.Plugins.Interfaces;

namespace Core.Plugins.Fortnite.Detection;

public sealed class FortniteProfileDetectionPlugin : IEpicGamesDetection, IGameVersionUpdatePlugin
{
    public string GameId => "Fortnite";
    public string Name => "Fortnite Profile Detection";
    public EGame TargetVersion => EGame.GAME_UE6_LATEST;
    
    public async void Detect(List<BaseProfile> LoadedProfiles, Action<BaseProfile>? onDetected = null)
    {
        await IEpicGamesDetection.TryDetectGameAsync(
            gameId: "Fortnite",
            detectFunc: () => IEpicGamesDetection.DetectGame("Fortnite", @"\FortniteGame\Content\Paks", TargetVersion, "Fortnite"),
            onDetected,
            LoadedProfiles
        );
    }
}
