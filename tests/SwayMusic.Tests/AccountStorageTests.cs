using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SwayMusic.Core.Storage;

public static class AccountStorageTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.GetFullPath(Path.Combine(temporaryRoot, "SwayMusic-account-tests-" + Guid.NewGuid().ToString("N")));
        Check(directory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase), "Account tests must stay inside the temporary directory.");
        Directory.CreateDirectory(directory);
        try
        {
            await TestCompatibilityAndMetadataAsync(directory);
            await TestValidationAndRollbackAsync(directory);
            await TestCorruptionAsync(directory);
            Check(!Directory.EnumerateFiles(directory, "*.tmp", SearchOption.AllDirectories).Any(),
                "Account writes must not leave temporary files.");
            Console.WriteLine("PASS account metadata DPAPI envelope, legacy cookies, sign-out, corruption, bounds, cancellation and rollback");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task TestCompatibilityAndMetadataAsync(string directory)
    {
        string file = Path.Combine(directory, "account.dpapi");
        const string cookie = "SAPISID=storage-test-secret; __Secure-3PAPISID=storage-test-secondary";
        using var store = new CredentialStore(directory);
        Check(await store.LoadAccountAsync() is null, "An absent account must remain signed out.");
        await store.SaveCookieAsync("  " + cookie + "  ");
        AccountSession? legacy = await store.LoadAccountAsync();
        Check(legacy == new AccountSession(cookie), "A legacy raw DPAPI cookie must load as an account without invented metadata.");
        Check(await store.LoadCookieAsync() == cookie, "The legacy cookie API must keep round-tripping existing files.");

        var session = new AccountSession(cookie, "visitor-storage-token", "sync-storage-token", 2,
            "Renée 音楽", "@storage-test", "UC-storage-test");
        Check(!session.ToString().Contains("storage-test", StringComparison.Ordinal), "Account diagnostics must redact credentials and identity metadata.");
        await store.SaveAccountAsync(session);
        Check(await store.LoadAccountAsync() == session, "All account fields must survive a protected-envelope round trip.");
        Check(await store.LoadCookieAsync() == cookie, "The cookie API must read a new account envelope.");
        using (var reopened = new CredentialStore(directory))
            Check(await reopened.LoadAccountAsync() == session, "Metadata must survive reopening the store.");
        byte[] protectedFile = await File.ReadAllBytesAsync(file);
        foreach (string marker in new[] { "storage-test-secret", "visitor-storage-token", "sync-storage-token", "Renée 音楽", "@storage-test", "UC-storage-test" })
            Check(protectedFile.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) < 0, "Cookies and metadata must never appear as plaintext on disk.");

        await store.SaveAccountAsync(session with { CookieHeader = "  " + cookie + "  ", DisplayName = "  Renée 音楽  ", Handle = "   " });
        Check(await store.LoadAccountAsync() == session with { Handle = null }, "Single-line account fields must normalize surrounding whitespace.");
        await store.SaveCookieAsync("SAPISID=replacement-test");
        Check(await store.LoadAccountAsync() == new AccountSession("SAPISID=replacement-test"),
            "The legacy save API must replace an envelope without retaining another account's metadata.");
        await store.SaveAccountAsync(null);
        Check(!File.Exists(file) && await store.LoadAccountAsync() is null && await store.LoadCookieAsync() is null,
            "Account sign-out must remove both metadata and cookies.");

        Task save = store.SaveAccountAsync(session);
        Task signOut = store.SaveAccountAsync(null);
        await Task.WhenAll(save, signOut);
        Check(await store.LoadAccountAsync() is null, "Sign-out queued behind an account save must remove the newly saved account.");
        await store.SaveAccountAsync(session);
        await store.SaveCookieAsync(null);
        Check(await store.LoadAccountAsync() is null, "The legacy sign-out API must also remove account envelopes.");
    }

    private static async Task TestValidationAndRollbackAsync(string directory)
    {
        string file = Path.Combine(directory, "account.dpapi");
        using var store = new CredentialStore(directory);
        var baseline = new AccountSession("SAPISID=baseline-test", "visitor", "sync", 1, "Baseline", "@baseline", "UC-baseline");
        await store.SaveAccountAsync(baseline);
        byte[] before = await File.ReadAllBytesAsync(file);
        AccountSession[] invalid =
        [
            baseline with { CookieHeader = null! },
            baseline with { CookieHeader = "  " },
            baseline with { CookieHeader = "SAPISID=test\r\nOther: header" },
            baseline with { CookieHeader = new string('c', 65537) },
            baseline with { VisitorData = new string('v', 8193) },
            baseline with { DataSyncId = new string('s', 8193) },
            baseline with { DisplayName = new string('d', 513) },
            baseline with { Handle = new string('h', 257) },
            baseline with { ChannelId = new string('i', 257) },
            baseline with { AuthUser = -1 },
            baseline with { AuthUser = 100 },
            baseline with { VisitorData = "visitor\r\ninjected" },
            baseline with { DataSyncId = "sync\nvalue" },
            baseline with { DisplayName = "name\nvalue" },
            baseline with { Handle = "@handle\tvalue" },
            baseline with { ChannelId = "channel\0value" }
        ];
        foreach (AccountSession account in invalid)
            await ExpectAsync<ArgumentException>(() => store.SaveAccountAsync(account));
        await ExpectAsync<ArgumentException>(() => store.SaveCookieAsync("\r\n"));
        await ExpectAsync<ArgumentException>(() => store.SaveCookieAsync(new string('c', 65537)));
        Check(await FileEqualsAsync(file, before), "Rejected account values must preserve prior protected credentials.");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => store.SaveAccountAsync(baseline with { DisplayName = "Canceled" }, canceled.Token));
        await ExpectAsync<OperationCanceledException>(() => store.SaveAccountAsync(null, canceled.Token));
        await ExpectAsync<OperationCanceledException>(() => store.SaveCookieAsync(null, canceled.Token));
        await ExpectAsync<OperationCanceledException>(() => store.LoadAccountAsync(canceled.Token));
        await ExpectAsync<OperationCanceledException>(() => store.LoadCookieAsync(canceled.Token));
        Check(await FileEqualsAsync(file, before), "Cancellation must not replace or delete an existing account.");

        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            await ExpectPersistenceFailureAsync(() => store.SaveAccountAsync(baseline with { DisplayName = "Must roll back" }));
        Check(await FileEqualsAsync(file, before) && await store.LoadAccountAsync() == baseline,
            "An atomic replacement failure must leave the original account readable.");
        Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "A failed replacement must remove its encrypted temporary file.");

        var maximum = new AccountSession(new string('c', 65536), new string('v', 8192), new string('s', 8192), 99,
            new string('d', 512), new string('h', 256), new string('i', 256));
        await store.SaveAccountAsync(maximum);
        Check(await store.LoadAccountAsync() == maximum, "Documented account-field limits and the highest account index must be accepted.");
        await store.SaveAccountAsync(null);
    }

    private static async Task TestCorruptionAsync(string directory)
    {
        string file = Path.Combine(directory, "account.dpapi");
        using var store = new CredentialStore(directory);
        foreach (string payload in new[]
        {
            "\0wrong-account-magic\0{}",
            "\0SwayMusic.Account\0{bad-json",
            "\0SwayMusic.Account\0{\"Version\":2,\"Session\":{\"CookieHeader\":\"SAPISID=test\"}}",
            "\0SwayMusic.Account\0{\"Version\":1,\"Session\":null}",
            "\0SwayMusic.Account\0{\"Version\":1,\"Session\":{}}",
            "\0SwayMusic.Account\0{\"Version\":1,\"Session\":{\"CookieHeader\":\"SAPISID=test\\r\\nInjected: header\"}}",
            "\0SwayMusic.Account\0{\"Version\":1,\"Session\":{\"CookieHeader\":\"SAPISID=test\",\"AuthUser\":100}}",
            "SAPISID=test\r\nInjected: header"
        })
        {
            byte[] plaintext = Encoding.UTF8.GetBytes(payload);
            byte[] encrypted;
            try { encrypted = ProtectTestPayload(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            await File.WriteAllBytesAsync(file, encrypted);
            await ExpectAsync<InvalidDataException>(() => store.LoadAccountAsync());
            await ExpectAsync<InvalidDataException>(() => store.LoadCookieAsync());
            Check(await FileEqualsAsync(file, encrypted), "Unreadable account data must not be modified or silently erased.");
        }
        await File.WriteAllBytesAsync(file, ProtectTestPayload([0xC3, 0x28]));
        await ExpectAsync<InvalidDataException>(() => store.LoadAccountAsync());
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
        await ExpectAsync<InvalidDataException>(() => store.LoadAccountAsync());
        await File.WriteAllBytesAsync(file, []);
        await ExpectAsync<InvalidDataException>(() => store.LoadAccountAsync());
        await File.WriteAllBytesAsync(file, new byte[1024 * 1024 + 1]);
        await ExpectAsync<InvalidDataException>(() => store.LoadAccountAsync());
        Check(new FileInfo(file).Length == 1024 * 1024 + 1, "An oversized credential file must be rejected without modification.");
        await store.SaveAccountAsync(null);
        Check(await store.LoadAccountAsync() is null, "Explicit sign-out must remove even an unreadable credential file.");
        await store.SaveAccountAsync(new AccountSession("SAPISID=recovered-test"));
        Check((await store.LoadAccountAsync())?.CookieHeader == "SAPISID=recovered-test", "A valid account must be savable after explicit corrupt-account removal.");
    }

    private static byte[] ProtectTestPayload(byte[] plaintext)
    {
        var source = new DataBlob { Length = plaintext.Length, Data = Marshal.AllocHGlobal(plaintext.Length) };
        DataBlob destination = default;
        try
        {
            Marshal.Copy(plaintext, 0, source.Data, plaintext.Length);
            if (!CryptProtectData(ref source, "SwayMusic isolated credential tests", IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, 1, out destination))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Isolated DPAPI test encryption failed.");
            byte[] result = new byte[destination.Length];
            Marshal.Copy(destination.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (int index = 0; index < source.Length; index++) Marshal.WriteByte(source.Data, index, 0);
            Marshal.FreeHGlobal(source.Data);
            if (destination.Data != IntPtr.Zero)
            {
                for (int index = 0; index < destination.Length; index++) Marshal.WriteByte(destination.Data, index, 0);
                LocalFree(destination.Data);
            }
        }
    }

    private static async Task<bool> FileEqualsAsync(string path, byte[] expected)
    {
        byte[] actual = await File.ReadAllBytesAsync(path);
        return expected.AsSpan().SequenceEqual(actual);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static async Task ExpectPersistenceFailureAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected the locked credential file to reject replacement.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
