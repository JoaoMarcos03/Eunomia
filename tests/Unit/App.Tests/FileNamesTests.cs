using Eunomia.App.Export;

namespace Eunomia.App.Tests;

/// <summary>File names made from server or user names: Windows rules on every system.</summary>
public sealed class FileNamesTests
{
    [Theory]
    [InlineData("users", "users")]
    [InlineData("a\"b<c>d|e:f*g?h\\i/j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("name. . ", "name")]
    [InlineData("CON", "CON_")]
    [InlineData("con.txt", "con_.txt")]
    [InlineData("con .txt", "con_ .txt")]
    [InlineData("Nul", "Nul_")]
    [InlineData("COM1", "COM1_")]
    [InlineData("lpt9.log", "lpt9_.log")]
    [InlineData("COM0", "COM0")]
    [InlineData("COM10", "COM10")]
    [InlineData("console", "console")]
    [InlineData("my.con", "my.con")]
    public void Safe(string name, string expected) => Assert.Equal(expected, FileNames.Safe(name));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("  ")]
    public void EmptyNamesGetTheFallback(string name)
    {
        Assert.Equal("_", FileNames.Safe(name));
        Assert.Equal("blob", FileNames.Safe(name, empty: "blob"));
    }
}
