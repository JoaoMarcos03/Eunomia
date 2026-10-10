using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Eunomia.App.Secrets;

namespace Eunomia.Infra.Secrets.Linux;

/// <summary>
/// Segredos no Secret Service do freedesktop (GNOME Keyring, KWallet 5.97+, KeePassXC) pela libsecret (secção 21.8;
/// Fase 14): uma password por chave no keyring por omissão, com o atributo <c>eunomia-key</c> e a etiqueta
/// <c>Eunomia (chave)</c>, visíveis no Seahorse/KWalletManager. P/Invoke à <c>libsecret-1.so.0</c> e à
/// <c>libglib-2.0.so.0</c> (sem pacotes extra). Sem a biblioteca, <see cref="IsAvailable"/> é falso; sem serviço a
/// correr, as operações lançam <see cref="SecretStoreException"/>.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class SecretServiceStore : ISecretStore, IDisposable
{
    private const string SecretLibrary = "libsecret-1.so.0";
    private const string GlibLibrary = "libglib-2.0.so.0";
    private const string KeyAttribute = "eunomia-key";

    // SecretSchema (libsecret/secret-schema.h): name, flags, 32 × {name, type}, reserved, reserved1..7.
    private const int SchemaSize = 8 + 8 + (32 * 16) + 8 + (7 * 8);

    private readonly IntPtr _schema;
    private readonly IntPtr _schemaName;
    private readonly IntPtr _attributeName;
    private readonly IntPtr _strHash;
    private readonly IntPtr _strEqual;
    private readonly string _label;

    public SecretServiceStore(string schemaName = "org.eunomia.Secret", string label = "Eunomia")
    {
        _label = label;
        if (IntPtr.Size != 8 || !NativeLibrary.TryLoad(SecretLibrary, out _) || !NativeLibrary.TryLoad(GlibLibrary, out var glib))
        {
            return;
        }
        _strHash = NativeLibrary.GetExport(glib, "g_str_hash");
        _strEqual = NativeLibrary.GetExport(glib, "g_str_equal");
        _schema = Marshal.AllocHGlobal(SchemaSize);
        unsafe
        {
            new Span<byte>((void*)_schema, SchemaSize).Clear();
        }
        _schemaName = Marshal.StringToCoTaskMemUTF8(schemaName);
        _attributeName = Marshal.StringToCoTaskMemUTF8(KeyAttribute);
        Marshal.WriteIntPtr(_schema, 0, _schemaName);
        // flags = SECRET_SCHEMA_NONE (0); attributes[0] = { "eunomia-key", SECRET_SCHEMA_ATTRIBUTE_STRING (0) }.
        Marshal.WriteIntPtr(_schema, 16, _attributeName);
        IsAvailable = true;
    }

    /// <summary>A libsecret existe nesta máquina (o serviço pode, mesmo assim, não estar a correr).</summary>
    public bool IsAvailable { get; }

    public string? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureAvailable();
        return WithAttributes(key, attributes =>
        {
            var result = secret_password_lookupv_sync(_schema, attributes, IntPtr.Zero, out var error);
            ThrowOnError(error, "read");
            if (result == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                return Marshal.PtrToStringUTF8(result);
            }
            finally
            {
                // Apaga a memória antes de a libertar.
                secret_password_free(result);
            }
        });
    }

    public void Set(string key, string secret)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(secret);
        EnsureAvailable();
        WithAttributes(key, attributes =>
        {
            var label = Marshal.StringToCoTaskMemUTF8($"{_label} ({key})");
            var password = Marshal.StringToCoTaskMemUTF8(secret);
            try
            {
                var ok = secret_password_storev_sync(_schema, attributes, IntPtr.Zero, label, password, IntPtr.Zero, out var error);
                ThrowOnError(error, "write");
                if (!ok)
                {
                    throw new SecretStoreException("The Secret Service could not write the secret.");
                }
            }
            finally
            {
                ZeroAndFree(password);
                Marshal.FreeCoTaskMem(label);
            }
            return (string?)null;
        });
    }

    public void Delete(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureAvailable();
        WithAttributes(key, attributes =>
        {
            // Devolve FALSE sem erro quando não havia nada para apagar.
            _ = secret_password_clearv_sync(_schema, attributes, IntPtr.Zero, out var error);
            ThrowOnError(error, "delete");
            return (string?)null;
        });
    }

    public void Dispose()
    {
        if (_schema != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_schema);
            Marshal.FreeCoTaskMem(_schemaName);
            Marshal.FreeCoTaskMem(_attributeName);
        }
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new SecretStoreException("libsecret is not installed: no system keyring is available.");
        }
    }

    /// <summary>Tabela GHashTable <c>{"eunomia-key": key}</c> para um pedido (as strings são libertadas no fim).</summary>
    private string? WithAttributes(string key, Func<IntPtr, string?> request)
    {
        var table = g_hash_table_new(_strHash, _strEqual);
        var name = Marshal.StringToCoTaskMemUTF8(KeyAttribute);
        var value = Marshal.StringToCoTaskMemUTF8(key);
        try
        {
            g_hash_table_insert(table, name, value);
            return request(table);
        }
        finally
        {
            g_hash_table_unref(table);
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(value);
        }
    }

    private static unsafe void ZeroAndFree(IntPtr text)
    {
        var p = (byte*)text;
        while (*p != 0)
        {
            *p++ = 0;
        }
        Marshal.FreeCoTaskMem(text);
    }

    /// <summary>GError: <c>{ GQuark domain; gint code; gchar *message; }</c>.</summary>
    private static void ThrowOnError(IntPtr error, string operation)
    {
        if (error == IntPtr.Zero)
        {
            return;
        }
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "unknown error";
        g_error_free(error);
        throw new SecretStoreException($"The Secret Service could not {operation} the secret: {message}");
    }

    [LibraryImport(SecretLibrary)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password, IntPtr cancellable, out IntPtr error);

    [LibraryImport(SecretLibrary)]
    private static partial IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [LibraryImport(SecretLibrary)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [LibraryImport(SecretLibrary)]
    private static partial void secret_password_free(IntPtr password);

    [LibraryImport(GlibLibrary)]
    private static partial IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr keyEqualFunc);

    [LibraryImport(GlibLibrary)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

    [LibraryImport(GlibLibrary)]
    private static partial void g_hash_table_unref(IntPtr table);

    [LibraryImport(GlibLibrary)]
    private static partial void g_error_free(IntPtr error);
}
