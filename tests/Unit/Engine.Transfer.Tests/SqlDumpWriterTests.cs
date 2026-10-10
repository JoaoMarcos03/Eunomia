using System.Text;
using Eunomia.Engine.Results;

namespace Eunomia.Engine.Transfer.Tests;

/// <summary>Formato do dump do HeidiSQL (FAM-E06; anexo B §2.3.2–2.3.4).</summary>
public sealed class SqlDumpWriterTests
{
    private static readonly DumpHeader Header = new("127.0.0.1", "10.4.32-MariaDB - mariadb.org binary distribution", "Win64", "0.8.0");

    [Fact]
    public void DumpCompletoNoFormatoDoHeidiSql()
    {
        var options = new SqlDumpOptions { CreateDatabases = true, CreateTables = true, DropTables = true, Data = DumpDataMode.Insert };
        var text = Dump(options, w =>
        {
            w.WriteHeader(Header);
            w.WriteDatabase("loja", "CREATE DATABASE `loja` /*!40100 DEFAULT CHARACTER SET utf8mb4 */");
            w.WriteTable("loja", "t", "CREATE TABLE `t` (\n  `id` int(11) NOT NULL AUTO_INCREMENT,\n  `name` varchar(20) DEFAULT NULL,\n  PRIMARY KEY (`id`)\n) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4");
            Assert.True(w.BeginData("loja", "t", 2, approximate: true, ["id", "name"]));
            w.WriteRows(Block(IdName, (1, "a\"b"), (2, null)));
            w.EndData();
            w.WriteViewPlaceholder("loja", "v", [new DumpColumn("id", "int(11)", false), new DumpColumn("name", "varchar(20)", true, "utf8mb4_general_ci")]);
            w.WriteRoutine("loja", "procedure", "p", "CREATE DEFINER=`root`@`localhost` PROCEDURE `p`()\nBEGIN\n  SELECT 1;\nEND");
            w.WriteTrigger("loja", "trg", "STRICT_TRANS_TABLES", "CREATE DEFINER=`root`@`%` TRIGGER `trg` BEFORE INSERT ON `t` FOR EACH ROW SET NEW.name = 'x'");
            w.WriteView("loja", "v", "CREATE ALGORITHM=UNDEFINED DEFINER=`root`@`localhost` SQL SECURITY DEFINER VIEW `v` AS select `t`.`id` AS `id`,`t`.`name` AS `name` from `t`");
            w.WriteFooter();
        });

        const string expected = """
            -- --------------------------------------------------------
            -- Host:                         127.0.0.1
            -- Server version:               10.4.32-MariaDB - mariadb.org binary distribution
            -- Server OS:                    Win64
            -- Eunomia Version:              0.8.0
            -- --------------------------------------------------------

            /*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
            /*!40101 SET NAMES utf8 */;
            /*!50503 SET NAMES utf8mb4 */;
            /*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
            /*!40103 SET TIME_ZONE='+00:00' */;
            /*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
            /*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
            /*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;


            -- Dumping database structure for loja
            CREATE DATABASE IF NOT EXISTS `loja` /*!40100 DEFAULT CHARACTER SET utf8mb4 */;
            USE `loja`;

            -- Dumping structure for table loja.t
            DROP TABLE IF EXISTS `t`;
            CREATE TABLE IF NOT EXISTS `t` (
              `id` int(11) NOT NULL AUTO_INCREMENT,
              `name` varchar(20) DEFAULT NULL,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4;

            -- Dumping data for table loja.t: ~2 rows (approximately)
            INSERT INTO `t` (`id`, `name`) VALUES
            	(1, 'a"b'),
            	(2, NULL);

            -- Dumping structure for view loja.v
            DROP VIEW IF EXISTS `v`;
            -- Creating temporary table to overcome VIEW dependency errors
            CREATE TABLE `v` (
            	`id` INT(11) NOT NULL,
            	`name` VARCHAR(1) NULL COLLATE 'utf8mb4_general_ci'
            ) ENGINE=MyISAM;

            -- Dumping structure for procedure loja.p
            DROP PROCEDURE IF EXISTS `p`;
            DELIMITER //
            CREATE PROCEDURE `p`()
            BEGIN
              SELECT 1;
            END//
            DELIMITER ;

            -- Dumping structure for trigger loja.trg
            DROP TRIGGER IF EXISTS `trg`;
            SET @OLDTMP_SQL_MODE=@@SQL_MODE, SQL_MODE='STRICT_TRANS_TABLES';
            DELIMITER //
            CREATE TRIGGER `trg` BEFORE INSERT ON `t` FOR EACH ROW SET NEW.name = 'x'//
            DELIMITER ;
            SET SQL_MODE=@OLDTMP_SQL_MODE;

            -- Removing temporary table and create final VIEW structure
            DROP TABLE IF EXISTS `v`;
            CREATE ALGORITHM=UNDEFINED SQL SECURITY DEFINER VIEW `v` AS select `t`.`id` AS `id`,`t`.`name` AS `name` from `t`;

            /*!40103 SET TIME_ZONE=IFNULL(@OLD_TIME_ZONE, 'system') */;
            /*!40101 SET SQL_MODE=IFNULL(@OLD_SQL_MODE, '') */;
            /*!40014 SET FOREIGN_KEY_CHECKS=IFNULL(@OLD_FOREIGN_KEY_CHECKS, 1) */;
            /*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
            /*!40111 SET SQL_NOTES=IFNULL(@OLD_SQL_NOTES, 1) */;

            """;
        Assert.Equal(expected.ReplaceLineEndings("\r\n"), text);
    }

    [Fact]
    public void SemComentariosNemEstruturaSoDados()
    {
        var options = new SqlDumpOptions { AddComments = false, Data = DumpDataMode.Replace, WrapInTransactions = true };
        var text = Dump(options, w =>
        {
            w.WriteDatabase("loja", "CREATE DATABASE `loja`");
            w.WriteTable("loja", "t", "CREATE TABLE `t` (`id` int)");
            w.BeginData("loja", "t", 0, approximate: false, ["id", "name"]);
            w.WriteRows(Block(IdName, (7, "x")));
            w.EndData();
        });

        Assert.Equal("\r\nBEGIN;\r\nREPLACE INTO `t` (`id`, `name`) VALUES\r\n\t(7, 'x');\r\nCOMMIT;\r\n", text);
    }

    [Fact]
    public void NoDataEscreveSoOComentario()
    {
        var text = Dump(new SqlDumpOptions(), w => Assert.False(w.BeginData("d", "t", 5, true, ["id"])));

        Assert.Equal("\r\n-- Data exporting was unselected.\r\n", text);
    }

    [Fact]
    public void DeleteInsertComRemocaoDoAutoIncrement()
    {
        var options = new SqlDumpOptions { Data = DumpDataMode.DeleteInsert, RemoveAutoIncrement = true, CreateTables = true };
        var text = Dump(options, w =>
        {
            w.WriteTable("d", "t", "CREATE TABLE `t` (\n  `id` int NOT NULL AUTO_INCREMENT\n) ENGINE=InnoDB AUTO_INCREMENT=42 DEFAULT CHARSET=utf8mb4");
            w.BeginData("d", "t", 1234567, approximate: false, ["id", "name"]);
            w.EndData();
        });

        Assert.Contains("`id` int NOT NULL AUTO_INCREMENT\r\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;", text, StringComparison.Ordinal);
        Assert.Contains("-- Dumping data for table d.t: 1,234,567 rows\r\nDELETE FROM `t`;\r\n/*!50000 ALTER TABLE `t` AUTO_INCREMENT = 1 */;\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InsertFechaAo90PorCentoDoTamanhoOuAoNumeroDeLinhas()
    {
        var rows = Enumerable.Range(1, 25).Select(i => (i, (string?)new string('x', 100))).ToArray();
        // 1 KB × 0,9 = 921 bytes: o prefixo e 7 linhas de ~110 bytes.
        var bySize = Dump(new SqlDumpOptions { Data = DumpDataMode.Insert, MaxInsertSizeKb = 1 }, w => WriteAll(w, rows));
        var byRows = Dump(new SqlDumpOptions { Data = DumpDataMode.Insert, MaxRowsPerInsert = 10 }, w => WriteAll(w, rows));
        var single = Dump(new SqlDumpOptions { Data = DumpDataMode.InsertIgnore, MaxInsertSizeKb = 0 }, w => WriteAll(w, rows[..2]));

        var statements = bySize.Split("INSERT INTO", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        Assert.Equal(4, statements.Count);
        Assert.All(statements, s => Assert.True(Encoding.UTF8.GetByteCount("INSERT INTO" + s) <= 921 + 3, s.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(3, byRows.Split("INSERT INTO", StringSplitOptions.None).Length - 1);
        Assert.EndsWith(
            "INSERT IGNORE INTO `t` (`id`, `name`) VALUES (1, '" + new string('x', 100) + "');\r\nINSERT IGNORE INTO `t` (`id`, `name`) VALUES (2, '" + new string('x', 100) + "');\r\n",
            single,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LiteraisPorTipo()
    {
        var schema = new ResultSchema(
        [
            new ColumnInfo("i", "INT", TypeCategory.Integer, StorageKind.Int64, false, true),
            new ColumnInfo("b", "BIT", TypeCategory.Integer, StorageKind.Int64, false, true),
            new ColumnInfo("d", "DECIMAL", TypeCategory.Real, StorageKind.Decimal, false, true, Scale: 2),
            new ColumnInfo("f", "FLOAT", TypeCategory.Real, StorageKind.Double, false, true),
            new ColumnInfo("g", "DOUBLE", TypeCategory.Real, StorageKind.Double, false, true),
            new ColumnInfo("u", "BIGINT UNSIGNED", TypeCategory.Integer, StorageKind.String, false, true),
            new ColumnInfo("s", "VARCHAR", TypeCategory.Text, StorageKind.String, false, true),
            new ColumnInfo("x", "BLOB", TypeCategory.Binary, StorageKind.Binary, false, true),
            new ColumnInfo("e", "VARBINARY", TypeCategory.Binary, StorageKind.Binary, false, true),
            new ColumnInfo("t", "DATETIME", TypeCategory.Temporal, StorageKind.String, false, true),
        ]);
        var builder = new RowBlockBuilder(schema, 0, 0, 2);
        builder.SetInt64(0, 0, -42);
        builder.SetInt64(0, 1, 5);
        builder.SetDecimalUnscaled(0, 2, -5);
        builder.SetDouble(0, 3, 0.1f);
        builder.SetDouble(0, 4, 0.1 + 0.2);
        builder.SetString(0, 5, "18446744073709551615");
        builder.SetString(0, 6, "a'b\\c\r\nd\0e ação");
        builder.SetBytes(0, 7, [0x00, 0xAB, 0x10]);
        builder.SetBytes(0, 8, []);
        builder.SetString(0, 9, "0000-00-00 00:00:00");
        for (var c = 0; c < schema.Count; c++)
        {
            builder.SetNull(1, c);
        }
        var text = Dump(new SqlDumpOptions { Data = DumpDataMode.Insert }, w =>
        {
            w.BeginData("d", "t", 2, false, [.. schema.Columns.Select(c => c.Name)]);
            w.WriteRows(builder.Build());
            w.EndData();
        });

        Assert.Contains(
            "\t(-42, b'101', -0.05, 0.1, 0.30000000000000004, 18446744073709551615, 'a\\'b\\\\c\\r\\nd\\0e ação', _binary 0x00AB10, '', '0000-00-00 00:00:00'),\r\n"
            + "\t(NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);\r\n",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TipoDaTabelaTemporariaDaView()
    {
        Assert.Equal("VARCHAR(1)", SqlDumpWriter.PlaceholderType("varchar(255)"));
        Assert.Equal("INT(10) UNSIGNED", SqlDumpWriter.PlaceholderType("int(10) unsigned"));
        Assert.Equal("ENUM('a','Bb')", SqlDumpWriter.PlaceholderType("enum('a','Bb')"));
        Assert.Equal("CHAR(3)", SqlDumpWriter.PlaceholderType("char(3)"));
    }

    [Fact]
    public void SaidaEmBlocosGrandesComOsBytesContados()
    {
        using var stream = new MemoryStream();
        using (var output = new Utf8Output(stream, bufferSize: 64, leaveOpen: true))
        {
            var text = string.Concat(Enumerable.Repeat("ação 😀 ", 50));
            output.Write(text);
            output.Write(new byte[200]);
            Assert.Equal(Encoding.UTF8.GetByteCount(text) + 200, output.BytesWritten);
        }
        Assert.Equal(string.Concat(Enumerable.Repeat("ação 😀 ", 50)) + new string('\0', 200), Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static readonly ResultSchema IdName = new(
    [
        new ColumnInfo("id", "INT", TypeCategory.Integer, StorageKind.Int64, true, false),
        new ColumnInfo("name", "VARCHAR", TypeCategory.Text, StorageKind.String, false, true),
    ]);

    private static RowBlock Block(ResultSchema schema, params (int Id, string? Name)[] rows)
    {
        var builder = new RowBlockBuilder(schema, 0, 0, rows.Length);
        for (var r = 0; r < rows.Length; r++)
        {
            builder.SetInt64(r, 0, rows[r].Id);
            if (rows[r].Name is { } name)
            {
                builder.SetString(r, 1, name);
            }
            else
            {
                builder.SetNull(r, 1);
            }
        }
        return builder.Build();
    }

    private static void WriteAll(SqlDumpWriter writer, (int, string?)[] rows)
    {
        writer.BeginData("d", "t", rows.Length, false, ["id", "name"]);
        // Em dois blocos, para o INSERT continuar entre blocos.
        writer.WriteRows(Block(IdName, rows[..(rows.Length / 2)]));
        writer.WriteRows(Block(IdName, rows[(rows.Length / 2)..]));
        writer.EndData();
    }

    private static string Dump(SqlDumpOptions options, Action<SqlDumpWriter> write)
    {
        using var stream = new MemoryStream();
        using (var output = new Utf8Output(stream, leaveOpen: true))
        {
            write(new SqlDumpWriter(output, options));
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
