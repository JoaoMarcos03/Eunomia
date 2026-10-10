using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Eunomia.App.Secrets;

namespace Eunomia.Infra.Secrets.MacOS;

/// <summary>
/// Secrets in the macOS Keychain (section 21.8; phase 14): <c>kSecClassGenericPassword</c> items with the service
/// <c>Eunomia</c> and the key as the account (visible and removable by the user in Keychain Access). Uses the
/// Security.framework <c>SecItem*</c> API through P/Invoke (no extra packages). Keychain errors (locked, refused)
/// arrive as <see cref="SecretStoreException"/>.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacKeychainSecretStore(string service = "Eunomia") : ISecretStore
{
    private const string SecurityLibrary = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;
    private const uint CFStringEncodingUtf8 = 0x08000100;

    private static readonly Lazy<Symbols> Native = new(Symbols.Load);

    public string Service { get; } = service;

    public string? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var s = Native.Value;
        using var cf = new CfScope();
        var query = cf.Dictionary(
            (s.Class, s.ClassGenericPassword), (s.AttrService, cf.String(Service)), (s.AttrAccount, cf.String(key)),
            (s.ReturnData, s.True), (s.MatchLimit, s.MatchLimitOne));
        var status = SecItemCopyMatching(query, out var data);
        if (status == ErrSecItemNotFound)
        {
            return null;
        }
        Check(status, "read");
        try
        {
            var length = (int)CFDataGetLength(data);
            if (length == 0)
            {
                // Empty data comes without a pointer (CFDataGetBytePtr returns NULL).
                return string.Empty;
            }
            var bytes = new byte[length];
            Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length);
            var text = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes);
            return text;
        }
        finally
        {
            CFRelease(data);
        }
    }

    public void Set(string key, string secret)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(secret);
        var s = Native.Value;
        using var cf = new CfScope();
        var bytes = Encoding.UTF8.GetBytes(secret);
        var value = cf.Data(bytes);
        Array.Clear(bytes);
        var query = cf.Dictionary((s.Class, s.ClassGenericPassword), (s.AttrService, cf.String(Service)), (s.AttrAccount, cf.String(key)));
        // The Keychain ignores a SecItemUpdate with empty data (the old password stayed): without content, delete and create again.
        var status = secret.Length == 0 ? SecItemDelete(query) : SecItemUpdate(query, cf.Dictionary((s.ValueData, value)));
        if (secret.Length == 0 && status is 0 or ErrSecItemNotFound)
        {
            status = ErrSecItemNotFound;
        }
        if (status == ErrSecItemNotFound)
        {
            var add = cf.Dictionary(
                (s.Class, s.ClassGenericPassword), (s.AttrService, cf.String(Service)), (s.AttrAccount, cf.String(key)),
                (s.AttrLabel, cf.String($"{Service} ({key})")), (s.ValueData, value));
            status = SecItemAdd(add, IntPtr.Zero);
        }
        Check(status, "write");
    }

    public void Delete(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var s = Native.Value;
        using var cf = new CfScope();
        var status = SecItemDelete(cf.Dictionary((s.Class, s.ClassGenericPassword), (s.AttrService, cf.String(Service)), (s.AttrAccount, cf.String(key))));
        if (status != ErrSecItemNotFound)
        {
            Check(status, "delete");
        }
    }

    private static void Check(int status, string operation)
    {
        if (status != 0)
        {
            throw new SecretStoreException($"The macOS Keychain could not {operation} the secret (OSStatus {status}).");
        }
    }

    /// <summary>Constants exported by the frameworks (CFStringRef and the CFDictionary callbacks).</summary>
    private sealed record Symbols(
        IntPtr Class, IntPtr ClassGenericPassword, IntPtr AttrService, IntPtr AttrAccount, IntPtr AttrLabel, IntPtr ValueData,
        IntPtr ReturnData, IntPtr MatchLimit, IntPtr MatchLimitOne, IntPtr True, IntPtr KeyCallBacks, IntPtr ValueCallBacks)
    {
        public static Symbols Load()
        {
            var security = NativeLibrary.Load(SecurityLibrary);
            var cf = NativeLibrary.Load(CoreFoundationLibrary);
            IntPtr Value(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
            return new Symbols(
                Value(security, "kSecClass"), Value(security, "kSecClassGenericPassword"), Value(security, "kSecAttrService"),
                Value(security, "kSecAttrAccount"), Value(security, "kSecAttrLabel"), Value(security, "kSecValueData"),
                Value(security, "kSecReturnData"), Value(security, "kSecMatchLimit"), Value(security, "kSecMatchLimitOne"),
                Value(cf, "kCFBooleanTrue"),
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
        }
    }

    /// <summary>Core Foundation objects created for a request, released at the end.</summary>
    private sealed class CfScope : IDisposable
    {
        private readonly List<IntPtr> _owned = [];

        public IntPtr String(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return Own(CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, CFStringEncodingUtf8, false));
        }

        public IntPtr Data(byte[] value) => Own(CFDataCreate(IntPtr.Zero, value, value.Length));

        public IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] pairs)
        {
            var keys = pairs.Select(p => p.Key).ToArray();
            var values = pairs.Select(p => p.Value).ToArray();
            var s = Native.Value;
            return Own(CFDictionaryCreate(IntPtr.Zero, keys, values, keys.Length, s.KeyCallBacks, s.ValueCallBacks));
        }

        private IntPtr Own(IntPtr value)
        {
            if (value == IntPtr.Zero)
            {
                throw new SecretStoreException("Core Foundation could not allocate an object for the Keychain request.");
            }
            _owned.Add(value);
            return value;
        }

        public void Dispose()
        {
            for (var i = _owned.Count - 1; i >= 0; i--)
            {
                CFRelease(_owned[i]);
            }
            _owned.Clear();
        }
    }

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemAdd(IntPtr attributes, IntPtr result);

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemDelete(IntPtr query);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial IntPtr CFStringCreateWithBytes(IntPtr allocator, byte[] bytes, nint numBytes, uint encoding, [MarshalAs(UnmanagedType.U1)] bool isExternalRepresentation);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint numValues, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial nint CFDataGetLength(IntPtr data);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial IntPtr CFDataGetBytePtr(IntPtr data);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial void CFRelease(IntPtr value);
}
