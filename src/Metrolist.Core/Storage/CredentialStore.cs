using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Metrolist.Core.Storage;

public sealed class CredentialStore : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CredentialStore(string? dataDirectory = null) =>
        _path = Path.Combine(dataDirectory ?? LibraryStore.DefaultDirectory, "account.dpapi");

    public async Task<string?> LoadCookieAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<string?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;
        if (new FileInfo(_path).Length > 1024 * 1024) throw new InvalidDataException("The account credential file is invalid.");
        byte[] encrypted = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        byte[] plaintext = Transform(encrypted, protect: false);
        try { return Encoding.UTF8.GetString(plaintext); }
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
        if (string.IsNullOrWhiteSpace(cookie))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }
        if (cookie.Length > 65536 || cookie.Contains('\r') || cookie.Contains('\n'))
            throw new ArgumentException("Use the Cookie request-header value on a single line.", nameof(cookie));
        byte[] plaintext = Encoding.UTF8.GetBytes(cookie.Trim());
        byte[] encrypted;
        try { encrypted = Transform(plaintext, protect: true); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
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

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Account credentials require Windows DPAPI.");
        var source = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        DataBlob destination = default;
        try
        {
            Marshal.Copy(input, 0, source.Data, input.Length);
            bool success = protect
                ? CryptProtectData(ref source, "Metrolist account", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out destination)
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
