using Eunomia.App.Secrets;

namespace Eunomia.Infra.Secrets.Windows.Tests;

/// <summary>Real Credential Manager and DPAPI (Windows only). They use a test prefix of their own.</summary>
public sealed class SecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eunomia-secrets-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsSecretStore _store;
    private readonly string _key = SecretKeys.Password(Guid.NewGuid());

    public SecretStoreTests()
    {
        _store = new WindowsSecretStore("Eunomia-tests/", _dir);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            _store.Delete(_key);
        }
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void SavesReadsAndDeletesInCredentialManager()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Credential Manager only exists on Windows.");
        Assert.Null(_store.Get(_key));

        _store.Set(_key, "pässwörd \"with\" quotes and €");
        Assert.Equal("pässwörd \"with\" quotes and €", _store.Get(_key));

        _store.Set(_key, "other");
        Assert.Equal("other", _store.Get(_key));

        _store.Delete(_key);
        Assert.Null(_store.Get(_key));
        _store.Delete(_key);
    }

    [Fact]
    public void EmptyPassword()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Credential Manager only exists on Windows.");

        _store.Set(_key, string.Empty);

        Assert.Equal(string.Empty, _store.Get(_key));
    }

    [Fact]
    public void LargeSecretGoesToDpapi()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI only exists on Windows.");
        var big = new string('k', 4000);

        _store.Set(_key, big);

        Assert.Equal(big, _store.Get(_key));
        var file = Assert.Single(Directory.GetFiles(_dir));
        Assert.DoesNotContain("kkkk", File.ReadAllText(file), StringComparison.Ordinal);

        _store.Set(_key, "pequeno");
        Assert.Equal("pequeno", _store.Get(_key));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void DpapiRoundTrip()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI only exists on Windows.");
        var data = "segredo"u8.ToArray();

        var protectedData = Dpapi.Protect(data);

        Assert.NotEqual(data, protectedData);
        Assert.Equal(data, Dpapi.Unprotect(protectedData));
    }
}
