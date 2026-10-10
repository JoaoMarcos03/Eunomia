using Eunomia.App.Secrets;
using Eunomia.App.Sessions;
using Eunomia.Presentation.Sessions;

namespace Eunomia.Presentation.Tests;

/// <summary>Session Manager rules (appendix A §1.5): Save only with changes, the password rule, immediate saving.</summary>
public sealed class SessionManagerModelTests
{
    private sealed class MemoryStore : ISessionStore
    {
        public int Saves { get; private set; }

        public SessionCatalog Load() => new();

        public void Save(SessionCatalog catalog) => Saves++;
    }

    private static (SessionManagerModel Model, MemoryStore Store, MemorySecretStore Secrets) Create()
    {
        var store = new MemoryStore();
        var secrets = new MemorySecretStore();
        var catalog = new SessionCatalog();
        catalog.Sessions.Add(new SessionDefinition { Name = "prod", Folder = ["Clients"], Host = "prod.example.com", PasswordSaved = true });
        secrets.Set(SecretKeys.Password(catalog.Sessions[0].Id), "secret");
        return (new SessionManagerModel(catalog, store, secrets), store, secrets);
    }

    [Fact]
    public void SaveOnlyWithChanges()
    {
        var (m, store, _) = Create();
        m.SelectSession(m.Catalog.Sessions[0]);
        Assert.False(m.IsModified);
        Assert.Equal("secret", m.Password);

        m.Draft!.Port = 3307;
        Assert.True(m.IsModified);
        Assert.True(m.NeedsSavePrompt);

        m.Save();
        Assert.False(m.IsModified);
        Assert.Equal(3307, m.Catalog.Sessions[0].Port);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void OnlyAChangedPasswordDoesNotAskOrSave()
    {
        var (m, _, secrets) = Create();
        var s = m.Catalog.Sessions[0];
        m.SelectSession(s);

        m.Password = "typed-only";

        Assert.True(m.IsModified);
        Assert.False(m.NeedsSavePrompt);
        var (values, credentials) = m.ValuesForOpen();
        Assert.Equal("typed-only", credentials!.Password);
        Assert.Equal(s.Id, values.Id);
        m.SelectSession(s);
        Assert.Equal("secret", secrets.Get(SecretKeys.Password(s.Id)));
    }

    [Fact]
    public void NewSessionInTheSelectedFolderWithAnEmptyPassword()
    {
        var (m, store, secrets) = Create();
        m.SelectSession(m.Catalog.Sessions[0]);

        var created = m.NewSession(inSelectedFolder: true);

        Assert.Equal("Clients\\Unnamed", created.Path);
        Assert.Same(created, m.Selected);
        Assert.Equal(string.Empty, secrets.Get(SecretKeys.Password(created.Id)));
        Assert.Equal(1, store.Saves);
        Assert.Equal("Unnamed", m.NewSession(inSelectedFolder: false).Path);
    }

    [Fact]
    public void DeletingAFolderCleansUpCredentials()
    {
        var (m, _, secrets) = Create();
        var id = m.Catalog.Sessions[0].Id;
        m.SelectFolder(["Clients"]);

        m.DeleteSelected();

        Assert.Empty(m.Catalog.Sessions);
        Assert.Null(secrets.Get(SecretKeys.Password(id)));
        Assert.Null(m.Selected);
    }

    [Fact]
    public void DuplicateSavesTheFormValues()
    {
        var (m, _, secrets) = Create();
        m.SelectSession(m.Catalog.Sessions[0]);
        m.Draft!.Host = "replica.example.com";
        m.Password = "pw2";

        var copy = m.Duplicate("prod-replica");

        Assert.Equal("replica.example.com", copy.Host);
        Assert.Equal("prod.example.com", m.Catalog.Sessions[0].Host);
        Assert.Equal("pw2", secrets.Get(SecretKeys.Password(copy.Id)));
        Assert.Same(copy, m.Selected);
    }

    [Fact]
    public void PromptForCredentialsDoesNotKeepThePassword()
    {
        var (m, _, secrets) = Create();
        var s = m.Catalog.Sessions[0];
        m.SelectSession(s);
        m.Draft!.LoginPrompt = true;

        m.Save();

        Assert.Null(secrets.Get(SecretKeys.Password(s.Id)));
        Assert.False(s.PasswordSaved);
        Assert.Null(m.ValuesForOpen().Credentials);
    }

    [Fact]
    public void RenameAndMoveSave()
    {
        var (m, store, _) = Create();
        var tree = m.Catalog.BuildTree();
        var sessionNode = tree[0].Children[0];

        Assert.Null(m.Rename(sessionNode, "production"));
        Assert.Null(m.Move(m.Catalog.BuildTree()[0].Children[0], null));

        Assert.Equal("production", m.Catalog.Sessions[0].Path);
        Assert.Equal(2, store.Saves);
    }

    [Theory]
    [InlineData(10, "a few seconds ago")]
    [InlineData(60, "1 minute ago")]
    [InlineData(7200, "2 hours ago")]
    [InlineData(86400 * 3, "3 days ago")]
    public void LastConnectAsRelativeText(int seconds, string expected)
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, SessionManagerModel.Ago(now.AddSeconds(-seconds), now));
        Assert.Equal("unknown or never", SessionManagerModel.Ago(null, now));
    }
}
