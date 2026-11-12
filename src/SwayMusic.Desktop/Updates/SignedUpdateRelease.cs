using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Velopack;

namespace SwayMusic.Desktop.Updates;

public sealed record SignedUpdateRelease(int SchemaVersion, string PackageId, string Channel, string Architecture,
    string Version, string FileName, long Size, string Sha256, string Sha1, string Notes)
{
    public const string AppId = "SwayMusicDesktop";
    public const string StableChannel = "win-x64-stable";
    public const string ManifestName = "sway-update.json";
    public const string SignatureName = "sway-update.sig";
    public const long MaximumPackageSize = 1024L * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static SignedUpdateRelease Verify(byte[] manifest, string signature, string publicKey)
    {
        if (manifest.Length is 0 or > 131072 || signature.Length > 1024)
            throw new InvalidDataException("Update metadata exceeds its size limit.");
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKey);
        if (!key.VerifyData(manifest, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("The update publisher signature is invalid.");
        var release = JsonSerializer.Deserialize<SignedUpdateRelease>(manifest, Json)
            ?? throw new InvalidDataException("Update metadata is empty.");
        if (release.SchemaVersion != 1 || release.PackageId != AppId || release.Channel != StableChannel
            || release.Architecture != "win-x64" || !Regex.IsMatch(release.Version ?? "", @"^\d+\.\d+\.\d+$")
            || !Regex.IsMatch(release.FileName ?? "", @"^[A-Za-z0-9][A-Za-z0-9._-]*-full\.nupkg$")
            || release.Size <= 0 || release.Size > MaximumPackageSize
            || !Regex.IsMatch(release.Sha256 ?? "", "^[A-Fa-f0-9]{64}$")
            || !Regex.IsMatch(release.Sha1 ?? "", "^[A-Fa-f0-9]{40}$")
            || release.Notes is null || release.Notes.Length > 32768)
            throw new InvalidDataException("The update targets an unsupported product, channel, version or package.");
        _ = SemanticVersion.Parse(release.Version!);
        return release;
    }

    public VelopackAsset ToAsset() => new()
    {
        PackageId = PackageId, Version = SemanticVersion.Parse(Version), Type = VelopackAssetType.Full,
        FileName = FileName, Size = Size, SHA256 = Sha256.ToUpperInvariant(), SHA1 = Sha1.ToUpperInvariant(),
        NotesMarkdown = Notes
    };

    public async Task VerifyPackageAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != Size || !CryptographicOperations.FixedTimeEquals(
            await SHA256.HashDataAsync(file, token).ConfigureAwait(false), Convert.FromHexString(Sha256)))
            throw new CryptographicException("The update package failed verification. Download it again.");
    }
}

internal sealed record UpdateProof(string Manifest, string Signature)
{
    public SignedUpdateRelease Verify(string publicKey) => SignedUpdateRelease.Verify(
        Convert.FromBase64String(Manifest), Signature, publicKey);
}
