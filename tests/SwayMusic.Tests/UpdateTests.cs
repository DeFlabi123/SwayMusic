using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwayMusic.Core.Storage;
using SwayMusic.Desktop.Updates;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;

namespace SwayMusic.Tests;

public static class UpdateTests
{
    public static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SwayMusic-updates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var fixture = new Fixture();
            var valid = fixture.Release;
            Check(fixture.Verify(valid) == valid, "A correctly signed release must verify.");
            var bytes = fixture.Manifest(valid);
            bytes[^2] ^= 1;
            Throws(() => SignedUpdateRelease.Verify(bytes, fixture.Sign(fixture.Manifest(valid)), fixture.PublicKey));
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Throws(() => SignedUpdateRelease.Verify(fixture.Manifest(valid), fixture.Sign(fixture.Manifest(valid)), other.ExportSubjectPublicKeyInfoPem()));
            foreach (var invalid in new[] { valid with { PackageId = "OtherApp" }, valid with { Channel = "beta" },
                valid with { Architecture = "win-arm64" }, valid with { FileName = "../bad-full.nupkg" },
                valid with { Version = "1.1.0-beta" }, valid with { Size = long.MaxValue }, valid with { Sha256 = "bad" } })
                Throws(() => fixture.Verify(invalid));
            string package = Path.Combine(directory, valid.FileName);
            await File.WriteAllBytesAsync(package, fixture.Payload);
            await valid.VerifyPackageAsync(package, CancellationToken.None);
            var damaged = fixture.Payload.ToArray(); damaged[0] ^= 1;
            await File.WriteAllBytesAsync(package, damaged);
            await ThrowsAsync(() => valid.VerifyPackageAsync(package, CancellationToken.None));

            var handler = new GithubHandler(fixture);
            using var client = new HttpClient(handler);
            var source = new SignedGithubSource(client, fixture.PublicKey);
            var feed = await source.GetReleaseFeed(new NullVelopackLogger(), SignedUpdateRelease.AppId, SignedUpdateRelease.StableChannel);
            Check(feed.Assets.Length == 1 && feed.Assets[0].Type == VelopackAssetType.Full, "Only an authenticated full package may enter the SDK feed.");
            string partial = Path.Combine(directory, "download.partial");
            await source.DownloadReleaseEntry(new NullVelopackLogger(), feed.Assets[0], partial, _ => { });
            Check((await File.ReadAllBytesAsync(partial)).SequenceEqual(fixture.Payload), "The actual downloaded bytes must match the signed package.");
            Check(!handler.LeakedCredentials, "Update requests must not carry authentication or cookies.");
            handler.CorruptPayload = true;
            await ThrowsAsync(() => source.DownloadReleaseEntry(new NullVelopackLogger(), feed.Assets[0], partial, _ => { }));
            Check(!File.Exists(partial), "A corrupt partial package must be removed.");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ThrowsAsync(() => source.DownloadReleaseEntry(new NullVelopackLogger(), feed.Assets[0], partial, _ => { }, cancelled.Token));
            }
            handler.InvalidSignature = true;
            await ThrowsAsync(() => source.GetReleaseFeed(new NullVelopackLogger(), SignedUpdateRelease.AppId, SignedUpdateRelease.StableChannel));
            handler.InvalidSignature = false;
            handler.RateLimited = true;
            await ThrowsAsync(() => source.GetReleaseFeed(new NullVelopackLogger(), SignedUpdateRelease.AppId, SignedUpdateRelease.StableChannel));
            handler.RateLimited = false;
            handler.CorruptPayload = false;

            // A pending update must be authenticated again without any network access after relaunch.
            await File.WriteAllBytesAsync(package, fixture.Payload);
            var proof = new UpdateProof(Convert.ToBase64String(fixture.Manifest(valid)), fixture.Sign(fixture.Manifest(valid)));
            await AtomicFile.WriteJsonAsync(Path.Combine(directory, "sway-pending-update.json"), proof);
            var locator = new TestVelopackLocator(SignedUpdateRelease.AppId, "1.0.0", directory, directory,
                directory, Path.Combine(directory, "Update.exe"), SignedUpdateRelease.StableChannel);
            int previousRequests = handler.Requests;
            using (var service = new VelopackUpdateService(locator, handler, fixture.PublicKey))
            {
                await service.InitializeAsync(CancellationToken.None);
                Check(service.Status.CanInstall && service.Status.AvailableVersion == "1.1.0", "A signed cached update must restore as ready.");
                Check(handler.Requests == previousRequests, "Restoring a pending update must work offline.");
                await File.WriteAllBytesAsync(package, damaged);
                await ThrowsAsync(() => service.ValidatePendingAsync(CancellationToken.None));
                Check(!service.Status.CanInstall, "Tampered cached packages must lose install eligibility.");
            }
            using (var badPending = new VelopackUpdateService(locator, new GithubHandler(fixture), fixture.PublicKey))
            {
                await badPending.InitializeAsync(CancellationToken.None);
                Check(badPending.Status.Phase == UpdatePhase.Failed && !badPending.Status.CanInstall,
                    "A corrupt package left by another run cannot be offered for installation.");
            }
            string empty = Path.Combine(directory, "empty-install");
            Directory.CreateDirectory(empty);
            var emptyLocator = new TestVelopackLocator(SignedUpdateRelease.AppId, "1.0.0", empty, empty, empty,
                Path.Combine(empty, "Update.exe"), SignedUpdateRelease.StableChannel);
            using (var malformed = new VelopackUpdateService(emptyLocator, new GithubHandler(fixture) { MalformedRelease = true }, fixture.PublicKey))
            {
                await malformed.CheckAsync(CancellationToken.None);
                Check(malformed.Status.Phase == UpdatePhase.Failed, "Malformed release responses must stay within the updater error state.");
            }
            Console.WriteLine("PASS updates: publisher signatures, target/path bounds, verified downloads, corruption cleanup, cancellation, rate limits and offline pending verification");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public byte[] Payload { get; } = RandomNumberGenerator.GetBytes(4096);
        public string PublicKey => _key.ExportSubjectPublicKeyInfoPem();
        public SignedUpdateRelease Release => new(1, SignedUpdateRelease.AppId, SignedUpdateRelease.StableChannel,
            "win-x64", "1.1.0", "SwayMusicDesktop-1.1.0-full.nupkg", Payload.Length,
            Convert.ToHexString(SHA256.HashData(Payload)), Convert.ToHexString(SHA1.HashData(Payload)), "Fixture release notes");
        public byte[] Manifest(SignedUpdateRelease release) => JsonSerializer.SerializeToUtf8Bytes(release, SignedUpdateRelease.Json);
        public string Sign(byte[] bytes) => Convert.ToBase64String(_key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        public SignedUpdateRelease Verify(SignedUpdateRelease release)
        {
            var bytes = Manifest(release);
            return SignedUpdateRelease.Verify(bytes, Sign(bytes), PublicKey);
        }
        public void Dispose() => _key.Dispose();
    }

    private sealed class GithubHandler(Fixture fixture) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool LeakedCredentials { get; private set; }
        public bool CorruptPayload { get; set; }
        public bool InvalidSignature { get; set; }
        public bool RateLimited { get; set; }
        public bool MalformedRelease { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            LeakedCredentials |= request.Headers.Authorization is not null || request.Headers.Contains("Cookie");
            if (RateLimited) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            byte[] body;
            string name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.Host == "api.github.com")
            {
                var names = new[] { SignedUpdateRelease.ManifestName, SignedUpdateRelease.SignatureName, fixture.Release.FileName };
                var assets = names.Select(n => new { name = n, browser_download_url = SignedGithubSource.Repository + "/releases/download/v1.1.0/" + n });
                body = MalformedRelease ? "[{}]"u8.ToArray()
                    : JsonSerializer.SerializeToUtf8Bytes(new[] { new { tag_name = "v1.1.0", draft = false, prerelease = false, assets } });
            }
            else if (name == SignedUpdateRelease.ManifestName) body = fixture.Manifest(fixture.Release);
            else if (name == SignedUpdateRelease.SignatureName)
                body = Encoding.UTF8.GetBytes(InvalidSignature ? Convert.ToBase64String(new byte[64]) : fixture.Sign(fixture.Manifest(fixture.Release)));
            else { body = fixture.Payload.ToArray(); if (CorruptPayload) body[0] ^= 1; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is CryptographicException or InvalidDataException or FormatException) { return; }
        throw new InvalidOperationException("An invalid update should have been rejected.");
    }
    private static async Task ThrowsAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is CryptographicException or InvalidDataException or HttpRequestException or OperationCanceledException) { return; }
        throw new InvalidOperationException("An invalid or interrupted update should have failed.");
    }
}
