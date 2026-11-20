using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Eunomia.UI.Controls.SqlEditor;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Auto upper case leaves object names that look like keywords alone: in a table list after a comma, and after REFERENCES,
/// TRUNCATE, TABLES, TO, ON and USE.
/// </summary>
public sealed class AutoUppercaseObjectNameTests
{
    [AvaloniaTheory]
    [InlineData("select * from a, status ", "SELECT * FROM a, status ")]
    [InlineData("select a, now() from t ", "SELECT a, NOW() FROM t ")]
    [InlineData("alter table t add foreign key (s) references status (id) on delete cascade ", "ALTER TABLE t ADD FOREIGN KEY (s) REFERENCES status (id) ON DELETE CASCADE ")]
    [InlineData("truncate status ", "TRUNCATE status ")]
    [InlineData("truncate table status ", "TRUNCATE TABLE status ")]
    [InlineData("lock tables status read ", "LOCK TABLES status READ ")]
    [InlineData("rename table x to status ", "RENAME TABLE x TO status ")]
    [InlineData("create trigger t before insert on status for each row set @a = 1 ", "CREATE TRIGGER t BEFORE INSERT ON status FOR EACH row SET @a = 1 ")]
    [InlineData("use status ", "USE status ")]
    [InlineData("insert into t values (1) on duplicate key update a = 1 ", "INSERT INTO t VALUES (1) ON DUPLICATE KEY UPDATE a = 1 ")]
    [InlineData("select * from a join b on a.id = b.id where a.x = 1 ", "SELECT * FROM a JOIN b ON a.id = b.id WHERE a.x = 1 ")]
    public void ObjectNamesKeepTheirCase(string typed, string expected)
    {
        var editor = new SqlEditorControl();
        var window = new Window { Width = 900, Height = 300, Content = editor };
        window.Show();
        window.CaptureRenderedFrame();
        editor.Editor.TextArea.Focus();
        foreach (var c in typed)
        {
            window.KeyTextInput(c.ToString());
        }

        Assert.Equal(expected, editor.Text);
        window.Close();
    }
}
