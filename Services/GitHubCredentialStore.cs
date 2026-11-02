using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.Security.Credentials;

namespace DevDesk.Services;

public interface IGitHubCredentialStore
{
    bool HasToken { get; }
    string? ReadToken();
    void SaveToken(string token);
    void DeleteToken();
}

public interface IProtectedGitHubTokenStore
{
    string? ReadToken();
    void SaveToken(string token);
    void DeleteToken();
}

public sealed class GitHubCredentialStore : IGitHubCredentialStore
{
    private readonly IProtectedGitHubTokenStore _primary;
    private readonly IProtectedGitHubTokenStore _fallback;
    private readonly object _gate = new();

    public GitHubCredentialStore(string dataDirectory)
        : this(
            new PasswordVaultTokenStore(),
            new DpapiGitHubCredentialStore(Path.Combine(dataDirectory, "github-token.dpapi")))
    {
    }

    public GitHubCredentialStore(
        IProtectedGitHubTokenStore primary,
        IProtectedGitHubTokenStore fallback)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public bool HasToken => !string.IsNullOrEmpty(ReadToken());

    public string? ReadToken()
    {
        lock (_gate)
        {
            try
            {
                var token = _primary.ReadToken();
                if (!string.IsNullOrEmpty(token))
                {
                    return token;
                }
            }
            catch (Exception)
            {
            }

            var fallbackToken = _fallback.ReadToken();
            if (string.IsNullOrEmpty(fallbackToken))
            {
                return null;
            }

            try
            {
                _primary.SaveToken(fallbackToken);
                _fallback.DeleteToken();
            }
            catch (Exception)
            {
                // The fallback remains protected and readable on the next launch.
            }

            return fallbackToken;
        }
    }

    public void SaveToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        lock (_gate)
        {
            try
            {
                _primary.SaveToken(token);
            }
            catch (Exception)
            {
                SaveFallbackOrThrow(token);
                return;
            }

            try
            {
                _fallback.DeleteToken();
            }
            catch (Exception)
            {
                try
                {
                    _primary.DeleteToken();
                }
                catch (Exception)
                {
                    throw new IOException("A secure credential store could not replace the previous token.");
                }

                SaveFallbackOrThrow(token);
            }
        }
    }

    public void DeleteToken()
    {
        lock (_gate)
        {
            Exception? primaryFailure = null;
            try
            {
                _primary.DeleteToken();
            }
            catch (Exception ex)
            {
                primaryFailure = ex;
            }

            try
            {
                _fallback.DeleteToken();
            }
            catch (Exception ex)
            {
                throw new IOException("The protected GitHub credential could not be fully removed.", ex);
            }

            if (primaryFailure is not null)
            {
                throw new IOException("The GitHub credential could not be fully removed.", primaryFailure);
            }
        }
    }

    private void SaveFallbackOrThrow(string token)
    {
        try
        {
            _fallback.SaveToken(token);
        }
        catch (Exception)
        {
            throw new IOException("A secure credential store is unavailable. The token was not saved.");
        }
    }
}

internal sealed class PasswordVaultTokenStore : IProtectedGitHubTokenStore
{
    private const string Resource = "DevDesk.GitHub";
    private const string CredentialName = "personal-access-token";
    private readonly PasswordVault _vault = new();

    public string? ReadToken()
    {
        try
        {
            var credential = _vault.Retrieve(Resource, CredentialName);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            return null;
        }
    }

    public void SaveToken(string token)
    {
        DeleteToken();
        _vault.Add(new PasswordCredential(Resource, CredentialName, token));
    }

    public void DeleteToken()
    {
        try
        {
            _vault.Remove(_vault.Retrieve(Resource, CredentialName));
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490))
        {
        }
    }
}

public sealed class DpapiGitHubCredentialStore : IProtectedGitHubTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DevDesk.GitHub.Token.v1");
    private readonly string _path;

    public DpapiGitHubCredentialStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public string? ReadToken()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(_path);
        var plainBytes = Unprotect(protectedBytes);
        try
        {
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public void SaveToken(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var plainBytes = Encoding.UTF8.GetBytes(token);
        var protectedBytes = Protect(plainBytes);
        var tempPath = _path + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, protectedBytes);
            if (File.Exists(_path))
            {
                try
                {
                    File.Replace(tempPath, _path, null, ignoreMetadataErrors: true);
                }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
                {
                    File.Move(tempPath, _path, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, _path);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    public void DeleteToken()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static byte[] Protect(byte[] bytes) => Transform(bytes, protect: true);

    private static byte[] Unprotect(byte[] bytes) => Transform(bytes, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        var entropyPointer = Marshal.AllocHGlobal(Entropy.Length);
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            Marshal.Copy(Entropy, 0, entropyPointer, Entropy.Length);
            var inputBlob = new DataBlob { Length = input.Length, Data = inputPointer };
            var entropyBlob = new DataBlob { Length = Entropy.Length, Data = entropyPointer };
            DataBlob outputBlob;
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, "DevDesk GitHub token", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob);
            if (!succeeded)
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                var result = new byte[outputBlob.Length];
                Marshal.Copy(outputBlob.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                LocalFree(outputBlob.Data);
            }
        }
        finally
        {
            Marshal.Copy(new byte[input.Length], 0, inputPointer, input.Length);
            Marshal.Copy(new byte[Entropy.Length], 0, entropyPointer, Entropy.Length);
            Marshal.FreeHGlobal(inputPointer);
            Marshal.FreeHGlobal(entropyPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
