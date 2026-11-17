using System.IO.Compression;
using System.Text;
using Eunomia.App.Transfer;

namespace Eunomia.App.Tests;

/// <summary>
/// The check of the closed file at the end of an export (request of 16 Nov 2026: a 15 GB database gave 8.7 GB, "re-validate
/// ALL of the export"): a file or a ZIP that does not hold every byte the writer counted is an error, never a "Done".
/// </summary>
public sealed class SqlExportOutputTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "eunomia-unit", "export-" + Guid.NewGuid().ToString("N")[..8]);

    public SqlExportOutputTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly byte[] Sql = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("INSERT INTO `t` VALUES (1, 'ação');\r\n", 5000)));

    private string WriteZip(string name, byte[] sql)
    {
        var path = Path.Combine(_directory, name);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var entry = archive.CreateEntry("dump.sql", CompressionLevel.Optimal).Open();
            entry.Write(sql);
        }
        return path;
    }

    [Fact]
    public void ACompleteFileOrZipGivesItsSize()
    {
        var file = Path.Combine(_directory, "dump.sql");
        File.WriteAllBytes(file, Sql);
        var zip = WriteZip("dump.zip", Sql);

        Assert.Equal(Sql.Length, SqlExportJob.VerifyOutput(file, isZip: false, Sql.Length));
        var zipped = SqlExportJob.VerifyOutput(zip, isZip: true, Sql.Length);
        Assert.Equal(new FileInfo(zip).Length, zipped);
        Assert.True(zipped < Sql.Length / 10);
    }

    [Fact]
    public void AShortFileIsAnError()
    {
        var file = Path.Combine(_directory, "short.sql");
        File.WriteAllBytes(file, Sql[..^1]);

        var error = Assert.Throws<IOException>(() => SqlExportJob.VerifyOutput(file, isZip: false, Sql.Length));
        Assert.Contains("incomplete", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AZipWithAShorterEntryIsAnError()
    {
        var zip = WriteZip("shorter.zip", Sql[..^100]);

        var error = Assert.Throws<IOException>(() => SqlExportJob.VerifyOutput(zip, isZip: true, Sql.Length));
        Assert.Contains("incomplete", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A ZIP cut short (its directory never written) is an error with the reason, not an InvalidDataException.</summary>
    [Fact]
    public void ATruncatedZipIsAnError()
    {
        var zip = WriteZip("cut.zip", Sql);
        var bytes = File.ReadAllBytes(zip);
        File.WriteAllBytes(zip, bytes[..(bytes.Length / 2)]);

        var error = Assert.Throws<IOException>(() => SqlExportJob.VerifyOutput(zip, isZip: true, Sql.Length));
        Assert.StartsWith("The ZIP file is damaged", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AZipWithTwoEntriesIsAnError()
    {
        var zip = Path.Combine(_directory, "two.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "a.sql", "b.sql" })
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(Sql);
            }
        }

        Assert.Throws<IOException>(() => SqlExportJob.VerifyOutput(zip, isZip: true, Sql.Length));
    }
}
