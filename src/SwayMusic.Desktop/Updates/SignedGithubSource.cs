using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace SwayMusic.Desktop.Updates;

// Only full packages authenticated by the embedded publisher key enter Velopack's feed.
// In particular, verification finishes before the SDK can extract a new Update.exe.
internal sealed class SignedGithubSource(HttpClient http, string publicKey) : IUpdateSource
{
    internal const string Repository = "https://github.com/DeFlabi123/SwayMusic";
    private const string Api = "https://api.github.com/repos/DeFlabi123/SwayMusic/releases?per_page=30";
    private SignedUpdateRelease? _approved;
    private Uri? _packageUrl;
    public UpdateProof? Proof { get; private set; }
    public CancellationToken CheckToken { get; set; }

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId,
        string? channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (appId != SignedUpdateRelease.AppId || channel != SignedUpdateRelease.StableChannel)
            throw new InvalidDataException("This installation uses an unsupported update channel.");
        var bytes = await GetBytesAsync(new Uri(Api), 2 * 1024 * 1024, CheckToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        var candidates = document.RootElement.EnumerateArray()
            .Where(r => !r.GetProperty("draft").GetBoolean() && !r.GetProperty("prerelease").GetBoolean())
            .Select(r => new { Tag = r.GetProperty("tag_name").GetString() ?? "", Assets = r.GetProperty("assets") })
            .Where(r => System.Text.RegularExpressions.Regex.IsMatch(r.Tag, @"^v\d+\.\d+\.\d+$") && SemanticVersion.TryParse(r.Tag[1..], out _))
            .Where(r => r.Assets.EnumerateArray().Any(a => a.GetProperty("name").GetString() == SignedUpdateRelease.ManifestName))
            .OrderByDescending(r => SemanticVersion.Parse(r.Tag[1..])).ToArray();
        if (candidates.Length == 0) return new VelopackAssetFeed { Assets = [] };
        var candidate = candidates[0];
        Uri AssetUrl(string name)
        {
            var matches = candidate.Assets.EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("A required update asset is missing or duplicated.");
            var uri = new Uri(matches[0].GetProperty("browser_download_url").GetString()!);
            if (!uri.AbsoluteUri.StartsWith(Repository + "/releases/download/", StringComparison.Ordinal))
                throw new InvalidDataException("The update asset is outside the configured repository.");
            return uri;
        }
        var manifest = await GetBytesAsync(AssetUrl(SignedUpdateRelease.ManifestName), 131072, CheckToken).ConfigureAwait(false);
        var signature = Encoding.UTF8.GetString(await GetBytesAsync(AssetUrl(SignedUpdateRelease.SignatureName), 1024, CheckToken).ConfigureAwait(false));
        var release = SignedUpdateRelease.Verify(manifest, signature, publicKey);
        if (candidate.Tag != "v" + release.Version) throw new InvalidDataException("The release tag and signed version differ.");
        _packageUrl = AssetUrl(release.FileName);
        _approved = release;
        Proof = new(Convert.ToBase64String(manifest), signature);
        return new VelopackAssetFeed { Assets = [release.ToAsset()] };
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry,
        string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        var release = _approved ?? throw new InvalidOperationException("Check for a signed update first.");
        if (_packageUrl is null || releaseEntry.FileName != release.FileName
            || releaseEntry.Version.ToString() != release.Version || releaseEntry.Type != VelopackAssetType.Full)
            throw new InvalidDataException("The requested package was not approved.");
        try
        {
            using var response = await SendAsync(_packageUrl, cancelToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new InvalidDataException("The update download has an unexpected size.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancelToken).ConfigureAwait(false))
            await using (var output = new FileStream(localFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                byte[] buffer = new byte[65536];
                long received = 0;
                int previous = -1;
                while (true)
                {
                    int count = await input.ReadAsync(buffer, cancelToken).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > release.Size) throw new InvalidDataException("The update exceeds its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancelToken).ConfigureAwait(false);
                    int percent = (int)(received * 100 / release.Size);
                    if (percent != previous) { progress(percent); previous = percent; }
                }
            }
            await release.VerifyPackageAsync(localFile, cancelToken).ConfigureAwait(false);
        }
        catch { if (File.Exists(localFile)) File.Delete(localFile); throw; }
    }

    private async Task<byte[]> GetBytesAsync(Uri url, int limit, CancellationToken token)
    {
        using var response = await SendAsync(url, token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The update response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > limit) throw new InvalidDataException("The update response is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("SwayMusic/" + AppVersion.Current);
        if (url.Host == "api.github.com") request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            response.Dispose();
            throw new HttpRequestException("GitHub temporarily limited update requests. Try again later.");
        }
        try { response.EnsureSuccessStatusCode(); return response; }
        catch { response.Dispose(); throw; }
    }
}
