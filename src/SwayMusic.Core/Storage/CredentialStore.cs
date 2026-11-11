using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwayMusic.Core.Storage;

public sealed class CredentialStore : IDisposable
{
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly byte[] EnvelopeMagic = "\0SwayMusic.Account\0"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CredentialStore(string? dataDirectory = null) =>
        _path = Path.Combine(dataDirectory ?? LibraryStore.DefaultDirectory, "account.dpapi");

    public async Task<string?> LoadCookieAsync(CancellationToken cancellationToken = default)
        => (await LoadAccountAsync(cancellationToken).ConfigureAwait(false))?.CookieHeader;

    public async Task<AccountSession?> LoadAccountAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<AccountSession?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_path)) return null;
        byte[] encrypted;
        await using (var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (file.Length is <= 0 or > MaximumFileBytes)
                throw new InvalidDataException("The account credential file is invalid.");
            encrypted = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(encrypted, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] plaintext;
        try { plaintext = Transform(encrypted, protect: false); }
        catch (Win32Exception)
        {
            throw new InvalidDataException("The saved account credentials could not be read for this Windows user.");
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plaintext.Length == 0) throw new InvalidDataException("The saved account credentials are invalid.");
            AccountSession session;
            if (plaintext[0] == 0)
            {
                if (!plaintext.AsSpan().StartsWith(EnvelopeMagic))
                    throw new InvalidDataException("The saved account format is invalid.");
                AccountEnvelope? envelope = JsonSerializer.Deserialize<AccountEnvelope>(
                    plaintext.AsSpan(EnvelopeMagic.Length), EnvelopeOptions);
                if (envelope is not { Version: 1, Session: not null })
                    throw new InvalidDataException("The saved account format is unsupported.");
                session = envelope.Session;
            }
            else session = new AccountSession(StrictUtf8.GetString(plaintext));
            return Normalize(session);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new InvalidDataException("The saved account credentials are invalid.");
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task SaveCookieAsync(string? cookie, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveCoreAsync(cookie, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task SaveCoreAsync(string? cookie, CancellationToken cancellationToken)
    {
        if (cookie is null)
        {
            DeleteCore(cancellationToken);
            return;
        }
        string? normalizedCookie = NormalizeField(cookie, 65536);
        if (normalizedCookie is null)
        {
            DeleteCore(cancellationToken);
            return;
        }
        byte[] plaintext = StrictUtf8.GetBytes(normalizedCookie);
        try { await SaveProtectedCoreAsync(plaintext, cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task SaveAccountAsync(AccountSession? session, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session is null)
            {
                DeleteCore(cancellationToken);
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new AccountEnvelope(1, Normalize(session)), EnvelopeOptions);
            byte[] plaintext = new byte[EnvelopeMagic.Length + json.Length];
            try
            {
                EnvelopeMagic.CopyTo(plaintext, 0);
                json.CopyTo(plaintext, EnvelopeMagic.Length);
                await SaveProtectedCoreAsync(plaintext, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally { _gate.Release(); }
    }

    private void DeleteCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private async Task SaveProtectedCoreAsync(byte[] plaintext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encrypted = Transform(plaintext, protect: true);
        if (encrypted.Length > MaximumFileBytes)
            throw new ArgumentException("The account credentials exceed the storage limit.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, encrypted, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static AccountSession Normalize(AccountSession session)
    {
        string? cookie = NormalizeField(session.CookieHeader, 65536);
        if (cookie is null)
            throw new ArgumentException("The account must contain a Cookie request-header value.", nameof(session));
        if (session.AuthUser is < 0 or > 99)
            throw new ArgumentException("The account index must be between 0 and 99.", nameof(session));
        return session with
        {
            CookieHeader = cookie,
            VisitorData = NormalizeField(session.VisitorData, 8192),
            DataSyncId = NormalizeField(session.DataSyncId, 8192),
            DisplayName = NormalizeField(session.DisplayName, 512),
            Handle = NormalizeField(session.Handle, 256),
            ChannelId = NormalizeField(session.ChannelId, 256)
        };
    }

    private static string? NormalizeField(string? value, int maximumLength)
    {
        if (value is null) return null;
        if (value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException("Account fields must be bounded, single-line values.");
        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private sealed record AccountEnvelope(int Version, AccountSession? Session);

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Account credentials require Windows DPAPI.");
        var source = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        DataBlob destination = default;
        try
        {
            Marshal.Copy(input, 0, source.Data, input.Length);
            bool success = protect
                ? CryptProtectData(ref source, "SwayMusic account", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out destination)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out destination);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect or read the account credentials.");
            byte[] result = new byte[destination.Length];
            Marshal.Copy(destination.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (int i = 0; i < source.Length; i++) Marshal.WriteByte(source.Data, i, 0);
            Marshal.FreeHGlobal(source.Data);
            if (destination.Data != IntPtr.Zero)
            {
                for (int i = 0; i < destination.Length; i++) Marshal.WriteByte(destination.Data, i, 0);
                LocalFree(destination.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public void Dispose() => _gate.Dispose();
}
