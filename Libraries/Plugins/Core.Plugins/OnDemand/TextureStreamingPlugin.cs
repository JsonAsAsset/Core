using System.Diagnostics;
using System.Net.Http.Headers;

using CUE4Parse.UE4.IO;

using Serilog;

using Core.Resources.Framework.Base;

namespace Core.Plugins.OnDemand;

/* Games that keep their high resolution mips out of the local containers describe them in
   IoStore "on demand" tocs. The package itself still ships locally, only its streamed payloads
   (.ubulk / .uptnl) live on the CDN, so without those tocs mounted a texture silently falls back
   to whatever mip was small enough to be inlined, which is usually 64x64. */
public interface ITextureStreamingPlugin : IPlugin
{
    /* Host the chunks referenced by the tocs are pulled from */
    Uri ChunkHostUri { get; }

    /* Only needed by games that gate their CDN behind an account */
    AuthenticationHeaderValue? Authorization => null;

    /* Profiles can opt out, the first mount downloads every container toc and that isn't cheap */
    bool IsEnabled(BaseProfile profile) => profile.TexturesOnDemand;

    /* Tocs to mount, by default the ones shipped next to the local containers */
    Task<IEnumerable<FileInfo>> ResolveTocs(BaseProfile profile)
    {
        return Task.FromResult(LocalTocs(profile));
    }

    public static IEnumerable<FileInfo> LocalTocs(BaseProfile profile)
    {
        if (string.IsNullOrEmpty(profile.ArchiveDirectory)) return [];

        var directory = new DirectoryInfo(profile.ArchiveDirectory);

        return directory.Exists ? directory.EnumerateFiles("*.uondemandtoc", SearchOption.AllDirectories) : [];
    }

    /* Registers the on demand containers against the profile's provider.
     * Has to run before the profile submits its keys, encrypted containers are only ever mounted
     * as a key comes in, so anything registered afterwards would stay unloaded. */
    async Task StreamTextures(BaseProfile profile)
    {
        var provider = profile.Provider;
        if (provider is null || !IsEnabled(profile)) return;

        provider.OnDemandOptions ??= new IoStoreOnDemandOptions
        {
            ChunkHostUri = ChunkHostUri,
            ChunkCacheDirectory = Resources.Globals.OnDemandChunksFolder,
            Authorization = Authorization,
            Timeout = TimeSpan.FromMinutes(5)
        };

        var tocs = (await ResolveTocs(profile)).ToArray();
        if (tocs.Length == 0) return;

        var timestamp = Stopwatch.GetTimestamp();
        var before = provider.MountedVfs.Count + provider.UnloadedVfs.Count;

        /* Each toc pulls its containers over the network, so they're worth overlapping */
        await Task.WhenAll(tocs.Select(async toc =>
        {
            try
            {
                await provider.RegisterVfsAsync(new IoChunkToc(toc.FullName, provider.Versions));
            }
            catch (Exception exception)
            {
                Log.Warning($"{Name}: Failed to register on demand toc {toc.Name}: {exception.Message}");
            }
        }));

        /* Picks up whatever isn't encrypted, the rest mounts as the profile submits its keys */
        await provider.MountAsync();

        var registered = provider.MountedVfs.Count + provider.UnloadedVfs.Count - before;

        Log.Information(
            $"{Name}: Registered {registered} on demand container(s) from {ChunkHostUri} " +
            $"in {Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds:F0}ms");
    }
}
