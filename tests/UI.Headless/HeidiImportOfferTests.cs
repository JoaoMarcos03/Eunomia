using Avalonia.Headless.XUnit;
using Eunomia.App.Secrets;
using Eunomia.App.Sessions;
using Eunomia.App.Settings;
using Eunomia.Drivers.MySql;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Shell;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Primeiro arranque com o HeidiSQL instalado (Fase 13): pergunta uma vez se importa as sessões dele.</summary>
public sealed class HeidiImportOfferTests
{
    private static MainWindow Shell(SessionCatalog catalog, AppSettings settings, Func<string> import)
    {
        var log = new SqlLogBuffer(settings.Log);
        var context = new ShellContext(settings, new TestShell.MemorySettingsStore(settings), null, log)
        {
            Sessions = new ShellSessions
            {
                Catalog = catalog,
                Store = new MemoryStore(),
                Secrets = new MemorySecretStore(),
                Connector = new SessionConnector(new MySqlProvider(), new MemorySecretStore(), null, new SessionLogAdapter(log)),
            },
        };
        return new MainWindow(context) { ImportHeidiSqlRegistry = import };
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task PerguntaUmaVezSemSessoesEImporta()
    {
        var settings = new AppSettings();
        var catalog = new SessionCatalog();
        var imports = 0;
        var window = Shell(catalog, settings, () =>
        {
            imports++;
            catalog.Sessions.Add(new SessionDefinition { Name = "from-heidi" });
            return "1 session(s) imported.";
        });
        var questions = 0;
        window.HeidiImportQuestionOverride = () =>
        {
            questions++;
            return Task.FromResult(true);
        };

        await window.OfferHeidiSqlImportAsync();
        Assert.Equal((1, 1), (questions, imports));
        Assert.True(settings.SessionManager.HeidiImportOffered);

        // Segunda vez (ou com sessões): não pergunta.
        catalog.Sessions.Clear();
        await window.OfferHeidiSqlImportAsync();
        Assert.Equal(1, questions);
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task PreferenciasImportadasAplicamSeLogo()
    {
        var settings = new AppSettings();
        var catalog = new SessionCatalog();
        var window = Shell(catalog, settings, () =>
        {
            settings.Display.ObjectSizes = false;
            settings.Display.TreeFilters = false;
            settings.Data.FontName = "Verdana";
            settings.Panels.TreeWidth = 410;
            return "imported";
        });
        window.HeidiImportQuestionOverride = () => Task.FromResult(true);
        window.Show();

        await window.OfferHeidiSqlImportAsync();

        Assert.False(window.Tree.Tree.ShowSizes);
        Assert.False(window.Tree.FiltersVisible);
        Assert.Equal("Verdana", window.DataGrid.FontFamilyName);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task ComSessoesNaoPergunta()
    {
        var settings = new AppSettings();
        var catalog = new SessionCatalog();
        catalog.Sessions.Add(new SessionDefinition { Name = "mine" });
        var window = Shell(catalog, settings, () => "x");
        var asked = false;
        window.HeidiImportQuestionOverride = () =>
        {
            asked = true;
            return Task.FromResult(true);
        };

        await window.OfferHeidiSqlImportAsync();
        Assert.False(asked);
        Assert.False(settings.SessionManager.HeidiImportOffered);
    }

    private sealed class MemoryStore : ISessionStore
    {
        public SessionCatalog Load() => new();

        public void Save(SessionCatalog catalog)
        {
        }
    }
}
