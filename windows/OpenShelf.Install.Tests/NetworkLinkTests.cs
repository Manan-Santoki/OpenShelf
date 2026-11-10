using System.Net;
using System.Net.Http.Headers;
using OpenShelf.Install;
using Xunit.Abstractions;

namespace OpenShelf.Install.Tests;

/// <summary>A fact that only runs when OPENSHELF_NETWORK_TESTS=1.</summary>
public sealed class NetworkFactAttribute : FactAttribute
{
    public NetworkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENSHELF_NETWORK_TESTS") != "1")
            Skip = "Set OPENSHELF_NETWORK_TESTS=1 to check the real download links.";
    }
}

public class NetworkLinkTests(ITestOutputHelper output)
{
    /// <summary>Resolves every tool's current download address and checksum from the real servers and asks the file
    /// server for its headers (HEAD). Nothing is downloaded.</summary>
    [NetworkFact]
    public async Task EveryCurrentDownloadResolvesAndIsReachable()
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenShelf-link-check", "1.0"));
        var http = new SourceHttp(client, TimeSpan.FromSeconds(45));
        var problems = new List<string>();

        foreach (var tool in ToolSources.All)
            foreach (var source in tool.Sources)
            {
                try
                {
                    var resolved = await source.Resolve(http, CancellationToken.None);
                    using var response = await http.SendAsync(HttpMethod.Head, resolved.Url, CancellationToken.None);
                    var length = response.Content.Headers.ContentLength;
                    var final = response.RequestMessage?.RequestUri;
                    output.WriteLine($"{tool.Id,-12} {source.Name,-32} v{resolved.Version}  sha256 {resolved.Sha256[..12]}…  {length / 1_000_000} MB\n    {resolved.Url}\n    -> {final}");
                    if (resolved.Sha256.Length != 64) problems.Add($"{tool.Id}/{source.Name}: bad checksum");
                    if (length is null or < 1_000_000) problems.Add($"{tool.Id}/{source.Name}: size {length}");
                }
                catch (Exception e)
                {
                    output.WriteLine($"{tool.Id,-12} {source.Name,-32} FAILED: {e.Message}");
                    problems.Add($"{tool.Id}/{source.Name}: {e.Message}");
                }
            }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
