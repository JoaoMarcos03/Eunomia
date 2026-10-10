using Eunomia.App.Secrets;
using Eunomia.Infra.Secrets.Linux;
using Eunomia.Infra.Secrets.MacOS;

namespace Eunomia.Infra.Secrets.Tests;

/// <summary>
/// Secret vaults on macOS and Linux and the in-memory fallback (phase 14). The Keychain and Secret Service tests
/// only run on the right system and are skipped when the vault is not available (for example, a runner without a keyring).
/// </summary>
public sealed class SecretStoreTests
{
    private static string TestKey() => "test/" + Guid.NewGuid().ToString("D");

    [Fact]
    public void InMemoryFallbackWhenTheVaultFails()
    {
        var warnings = new List<string>();
        var store = new ResilientSecretStore(new BrokenStore(), warnings.Add);
        Assert.True(store.IsPersistent);

        store.Set("conn/x", "s3cret");
        Assert.Equal("s3cret", store.Get("conn/x"));
        Assert.False(store.IsPersistent);
        Assert.Single(warnings);
        Assert.Contains("kept only until Eunomia closes", warnings[0], StringComparison.Ordinal);

        store.Delete("conn/x");
        Assert.Null(store.Get("conn/x"));
        Assert.Single(warnings);
    }

    [Fact]
    public void WithoutAVaultItStaysOnlyInMemory()
    {
        var store = new ResilientSecretStore(null);
        Assert.False(store.IsPersistent);
        store.Set("k", "v");
        Assert.Equal("v", store.Get("k"));
        Assert.False(new MemorySecretStore().IsPersistent);
    }

    [Fact]
    public void MacOSKeychain()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only.");
        if (OperatingSystem.IsMacOS())
        {
            var store = new MacKeychainSecretStore("Eunomia Tests");
            RoundTrip(store);
        }
    }

    [Fact]
    public void LinuxSecretService()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        if (OperatingSystem.IsLinux())
        {
            using var store = new SecretServiceStore("org.eunomia.Tests", "Eunomia Tests");
            Assert.SkipUnless(store.IsAvailable, "libsecret is not installed.");
            RoundTrip(store);
        }
    }

    private static void RoundTrip(ISecretStore store)
    {
        var key = TestKey();
        try
        {
            store.Set(key, "first");
        }
        catch (SecretStoreException ex)
        {
            Assert.Skip("System vault unavailable: " + ex.Message);
        }
        try
        {
            Assert.Equal("first", store.Get(key));
            // Replacing, with non-ASCII characters and quotes.
            store.Set(key, "pá\"ss'wörd 🔑");
            Assert.Equal("pá\"ss'wörd 🔑", store.Get(key));
            store.Set(key, string.Empty);
            Assert.Equal(string.Empty, store.Get(key));
        }
        finally
        {
            store.Delete(key);
        }
        Assert.Null(store.Get(key));
        // Deleting what does not exist is not an error.
        store.Delete(key);
    }

    private sealed class BrokenStore : ISecretStore
    {
        public string? Get(string key) => throw new SecretStoreException("Keyring locked.");

        public void Set(string key, string secret) => throw new SecretStoreException("Keyring locked.");

        public void Delete(string key) => throw new SecretStoreException("Keyring locked.");
    }
}
