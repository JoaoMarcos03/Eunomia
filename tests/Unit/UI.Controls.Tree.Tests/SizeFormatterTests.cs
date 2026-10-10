using System.Globalization;
using Eunomia.UI.Controls.Common.Formatting;

namespace Eunomia.UI.Controls.Tree.Tests;

public sealed class SizeFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1000, "1.0 KiB")]
    [InlineData(1023, "1.0 KiB")]
    [InlineData(1_000_000, "1.0 MiB")]
    [InlineData(999_999, "976.6 KiB")]
    [InlineData(196608, "192.0 KiB")]
    [InlineData(3_328_000_000, "3.1 GiB")]
    [InlineData(76_336_332, "72.8 MiB")]
    public void OneDecimalThresholdsOf1000AndDivisionBy1024(long bytes, string expected)
    {
        Assert.Equal(expected, SizeFormatter.Format(bytes, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void UsesTheLocaleSeparator()
    {
        Assert.Equal("192,0 KiB", SizeFormatter.Format(196608, CultureInfo.GetCultureInfo("pt-PT")));
    }
}
