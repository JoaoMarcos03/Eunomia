using Eunomia.App.Data;

namespace Eunomia.App.Tests;

/// <summary>Save BLOB to file and Image preview: the cell's bytes, the format by its magic bytes and the suggested name.</summary>
public sealed class BlobContentTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

    [Fact]
    public void BinarioVemDoHexETextoEmUtf8()
    {
        Assert.Equal(new byte[] { 0xCA, 0xFE, 0x00 }, BlobContent.ToBytes("0xCAFE00", binary: true));
        Assert.Equal(new byte[] { 0xCA, 0xFE }, BlobContent.ToBytes("0xcafe", binary: true));
        Assert.Empty(BlobContent.ToBytes("0x", binary: true));
        Assert.Equal("ação\n"u8.ToArray(), BlobContent.ToBytes("ação\n", binary: false));
        Assert.Equal("0xCAFE"u8.ToArray(), BlobContent.ToBytes("0xCAFE", binary: false));
    }

    [Theory]
    [InlineData("CAFE")]
    [InlineData("0xCAF")]
    [InlineData("0xZZ")]
    public void BinarioSemHexValidoEhRecusado(string raw)
    {
        Assert.Throws<FormatException>(() => BlobContent.ToBytes(raw, binary: true));
    }

    [Fact]
    public void FormatoPelosPrimeirosBytes()
    {
        var bmp = new byte[30];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        bmp[14] = 40;

        Assert.Equal(ImageFormat.Png, BlobContent.ImageFormatOf(Png));
        Assert.Equal(ImageFormat.Jpeg, BlobContent.ImageFormatOf([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal(ImageFormat.Gif, BlobContent.ImageFormatOf("GIF89a..."u8));
        Assert.Equal(ImageFormat.Gif, BlobContent.ImageFormatOf("GIF87a"u8));
        Assert.Equal(ImageFormat.Bmp, BlobContent.ImageFormatOf(bmp));
        Assert.Null(BlobContent.ImageFormatOf("BMW is a car brand, not a bitmap"u8));
        Assert.Null(BlobContent.ImageFormatOf([0x89, 0x50]));
        Assert.Null(BlobContent.ImageFormatOf([]));
    }

    [Theory]
    [InlineData("photo", true, "photo.png")]
    [InlineData("notes", false, "notes.txt")]
    [InlineData("a/b:c*?", false, "a_b_c__.txt")]
    [InlineData("  ", false, "blob.txt")]
    public void NomeSugeridoPelaColuna(string column, bool binary, string expected)
    {
        Assert.Equal(expected, BlobContent.SuggestedFileName(column, binary, Png));
    }

    [Fact]
    public void BinarioDesconhecidoFicaComBin()
    {
        Assert.Equal("data.bin", BlobContent.SuggestedFileName("data", binary: true, [0x01, 0x02]));
    }
}
