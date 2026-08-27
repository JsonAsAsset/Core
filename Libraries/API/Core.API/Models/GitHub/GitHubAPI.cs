using RestSharp;
using Serilog;
using Core.API.Models.Base;
using Core.API.Models.GitHub.Responses;

namespace Core.API.Models.GitHub;

public class GitHubAPI(RestClient client) : APIBase(client, GITHUB_API_LINK)
{
    public async Task<GitHubReleaseResponse?> GetLatestRelease()
    {
        var response = await ExecuteAsync<GitHubReleaseResponse>("releases/latest", verbose: false);

        if (response is null)
        {
            Log.Information("GetLatestRelease failed");
        }

        return response;
    }
    
    /* Same call against another repository, "owner/name" */
    public async Task<GitHubReleaseResponse?> GetLatestRelease(string repository)
    {
        var response = await ExecuteAsync<GitHubReleaseResponse>($"https://api.github.com/repos/{repository}/releases/latest", useBaseUrl: false, verbose: false);

        if (response is null)
        {
            Log.Information("GetLatestRelease failed for {Repository}", repository);
        }

        return response;
    }

    public async Task<GitHubContentsResponse[]?> GetOrsionUmaps()
    {
        var response = await ExecuteAsync<GitHubContentsResponse[]>("https://api.github.com/repos/Orsion/Fortnite/contents/.usmap", useBaseUrl: false, verbose: false);

        if (response is null)
        {
            Log.Information("GetOrsionUmaps failed");
        }

        return response;
    }
}
