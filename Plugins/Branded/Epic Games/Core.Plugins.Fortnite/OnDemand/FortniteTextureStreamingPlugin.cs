using System.Net.Http.Headers;

using Serilog;

using Core.Plugins.Interfaces;
using Core.Plugins.OnDemand;
using Core.Resources.Framework.Base;
using CUE4Parse.Utils;
using UE4Config.Parsing;

namespace Core.Plugins.Fortnite.OnDemand;

/* Fortnite ships the top mip of most textures outside of the installation, the local containers
   only carry the tail of the mip chain. The chunks holding the rest are described either by the
   .uondemandtoc files sitting next to the containers, or on older builds by the endpoint toc
   named in Cloud/IoStoreOnDemand.ini. Without them a 2048x2048 texture decodes as 64x64. */
public sealed class FortniteTextureStreamingPlugin : ITextureStreamingPlugin, IGameIdPlugin
{
    public string Name => "Fortnite Texture Streaming";
    public string GameId => "Fortnite";

    public Uri ChunkHostUri => new("https://download.epicgames.com/", UriKind.Absolute);

    /* The texture chunks are served anonymously, the token is only passed along when a login
     * already happened so authenticated requests aren't rate limited the same way */
    public AuthenticationHeaderValue? Authorization =>
        string.IsNullOrEmpty(EpicGames.Globals.EpicAuth?.Token)
            ? null
            : new AuthenticationHeaderValue("Bearer", EpicGames.Globals.EpicAuth!.Token);

    /* Manually created profiles pointed at a Fortnite installation stream just as well as
     * detected ones, so match on the tocs actually being there rather than on the profile name */
    public bool DoesCharacteristicsMatch(BaseProfile profile)
    {
        return ITextureStreamingPlugin.LocalTocs(profile).Any();
    }

    public async Task<IEnumerable<FileInfo>> ResolveTocs(BaseProfile profile)
    {
        var tocs = ITextureStreamingPlugin.LocalTocs(profile).ToList();
        if (tocs.Count > 0) return tocs;

        /* Pre 3x builds don't ship the tocs locally, they point at one hosted alongside the chunks */
        var tocPath = await GetEndpointTocPath(profile);
        if (string.IsNullOrEmpty(tocPath)) return tocs;

        var onDemandFile = new FileInfo(Path.Combine(Resources.Globals.OnDemandFolder.FullName, tocPath.SubstringAfterLast('/')));

        if (!onDemandFile.Exists || onDemandFile.Length == 0)
        {
            await API.Globals.API.DownloadFileAsync($"{ChunkHostUri}{tocPath.TrimStart('/')}", onDemandFile.FullName);
            onDemandFile.Refresh();
        }

        if (onDemandFile.Exists && onDemandFile.Length > 0)
        {
            tocs.Add(onDemandFile);
        }
        else
        {
            Log.Warning($"{Name}: Could not retrieve the endpoint toc at {tocPath}");
        }

        return tocs;
    }

    private static async Task<string> GetEndpointTocPath(BaseProfile profile)
    {
        if (string.IsNullOrEmpty(profile.ArchiveDirectory)) return string.Empty;

        var onDemandPath = Path.Combine(profile.ArchiveDirectory, @"..\..\..\Cloud\IoStoreOnDemand.ini");
        if (!File.Exists(onDemandPath)) return string.Empty;

        var onDemandIni = new ConfigIni();
        onDemandIni.Read(new StringReader(await File.ReadAllTextAsync(onDemandPath)));

        return onDemandIni
            .Sections.FirstOrDefault(section => section.Name?.Equals("Endpoint") ?? false)?
            .Tokens.OfType<InstructionToken>().FirstOrDefault(token => token.Key.Equals("TocPath"))?
            .Value.Replace("\"", string.Empty) ?? string.Empty;
    }
}
