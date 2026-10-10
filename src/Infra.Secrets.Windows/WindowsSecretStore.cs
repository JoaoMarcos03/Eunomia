using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using Eunomia.App.Secrets;

namespace Eunomia.Infra.Secrets.Windows;

/// <summary>
/// Segredos no Windows Credential Manager (secção 23.2): <c>CRED_TYPE_GENERIC</c>, <c>CRED_PERSIST_LOCAL_MACHINE</c>,
/// target <c>Eunomia/&lt;chave&gt;</c> (visível e removível pelo utilizador no Gestor de Credenciais). Segredos
/// maiores do que o limite de 2.560 bytes do blob vão para um ficheiro DPAPI (scope do utilizador, com entropia da
/// aplicação) na pasta indicada.
/// </summary>
public sealed partial class WindowsSecretStore(string targetPrefix, string largeSecretsDirectory) : ISecretStore
{
    public const int MaxBlobBytes = 2560;
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string TargetPrefix { get; } = targetPrefix;

    public string? Get(string key)
    {
        var target = TargetPrefix + key;
        if (CredReadW(target, CredTypeGeneric, 0, out var handle))
        {
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(handle);
                if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                {
                    return string.Empty;
                }
                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.Unicode.GetString(bytes);
            }
            finally
            {
                CredFree(handle);
            }
        }
        var error = Marshal.GetLastPInvokeError();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error);
        }
        var file = LargeSecretPath(key);
        return File.Exists(file) ? Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(file))) : null;
    }

    public void Set(string key, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > MaxBlobBytes)
        {
            Delete(key);
            Directory.CreateDirectory(largeSecretsDirectory);
            File.WriteAllBytes(LargeSecretPath(key), Dpapi.Protect(Encoding.UTF8.GetBytes(secret)));
            return;
        }
        DeleteLargeSecret(key);
        var target = TargetPrefix + key;
        var blob = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        var targetPtr = Marshal.StringToHGlobalUni(target);
        var userPtr = Marshal.StringToHGlobalUni("Eunomia");
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = userPtr,
            };
            if (!CredWriteW(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            // O segredo não fica em memória não gerida depois de escrito.
            unsafe
            {
                new Span<byte>((void*)blob, bytes.Length).Clear();
            }
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
        }
    }

    public void Delete(string key)
    {
        if (!CredDeleteW(TargetPrefix + key, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error);
            }
        }
        DeleteLargeSecret(key);
    }

    private void DeleteLargeSecret(string key)
    {
        var file = LargeSecretPath(key);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    private string LargeSecretPath(string key) =>
        Path.Combine(largeSecretsDirectory, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(TargetPrefix + key))) + ".bin");

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(ref Credential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}

/// <summary>DPAPI em scope do utilizador com entropia da aplicação (nunca <c>CRYPTPROTECT_LOCAL_MACHINE</c>).</summary>
public static partial class Dpapi
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Eunomia/secrets/v1");

    public static byte[] Protect(byte[] data) => Transform(data, protect: true);

    public static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static unsafe byte[] Transform(byte[] data, bool protect)
    {
        fixed (byte* input = data)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new DataBlob { Size = (uint)data.Length, Data = (IntPtr)input };
            var entropyBlob = new DataBlob { Size = (uint)Entropy.Length, Data = (IntPtr)entropy };
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                LocalFree(outBlob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint Size;
        public IntPtr Data;
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
