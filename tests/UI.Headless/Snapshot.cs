using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Visual snapshots (section 25.1): compares the frame with the PNG baseline in <c>tests/UI.Headless/Snapshots</c>.
/// Tolerance: a pixel counts as different if any channel differs by more than <see cref="ChannelTolerance"/>;
/// the test fails if more than <see cref="MaxDifferentRatio"/> of the pixels differ (text rendering
/// may vary slightly between machines). With <c>EUNOMIA_UPDATE_SNAPSHOTS=1</c> the baselines are written again.
/// On failure, the actual PNG is kept in <c>bin/.../snapshots-actual/</c>.
/// </summary>
internal static class Snapshot
{
    public const int ChannelTolerance = 24;
    public const double MaxDifferentRatio = 0.005;

    public static string Directory { get; } = Path.Combine(FindRepoRoot(), "tests", "UI.Headless", "Snapshots");

    public static void Match(WriteableBitmap? frame, string name)
    {
        Assert.NotNull(frame);
        var png = Encode(frame);
        var baselinePath = Path.Combine(Directory, name + ".png");

        // The baselines are Windows' (Segoe UI); on other systems the fonts change the text pixel by pixel. There the screen is
        // drawn anyway (catching exceptions and broken layouts) and the PNG goes to snapshots-actual/<system> for review.
        if (!OperatingSystem.IsWindows())
        {
            var platformDir = Path.Combine(AppContext.BaseDirectory, "snapshots-actual", OperatingSystem.IsMacOS() ? "macos" : "linux");
            System.IO.Directory.CreateDirectory(platformDir);
            File.WriteAllBytes(Path.Combine(platformDir, name + ".png"), png);
            return;
        }

        if (Environment.GetEnvironmentVariable("EUNOMIA_UPDATE_SNAPSHOTS") == "1")
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(baselinePath, png);
            return;
        }

        Assert.True(File.Exists(baselinePath), $"Missing baseline {baselinePath}. Generate it with EUNOMIA_UPDATE_SNAPSHOTS=1 and review the PNG before committing.");

        // Both images go through the same PNG decoder, so they have the same pixel format.
        var (actualPixels, actualSize) = Decode(png);
        var (expectedPixels, expectedSize) = Decode(File.ReadAllBytes(baselinePath));
        var different = 0;
        if (actualSize == expectedSize)
        {
            for (var i = 0; i < actualPixels.Length; i += 4)
            {
                if (Math.Abs(actualPixels[i] - expectedPixels[i]) > ChannelTolerance
                    || Math.Abs(actualPixels[i + 1] - expectedPixels[i + 1]) > ChannelTolerance
                    || Math.Abs(actualPixels[i + 2] - expectedPixels[i + 2]) > ChannelTolerance
                    || Math.Abs(actualPixels[i + 3] - expectedPixels[i + 3]) > ChannelTolerance)
                {
                    different++;
                }
            }
        }
        var ratio = actualSize == expectedSize ? different / (actualPixels.Length / 4.0) : 1.0;
        if (ratio > MaxDifferentRatio)
        {
            var outDir = Path.Combine(AppContext.BaseDirectory, "snapshots-actual");
            System.IO.Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, name + ".png"), png);
        }
        Assert.True(
            ratio <= MaxDifferentRatio,
            $"Snapshot {name}: {ratio:P2} of the pixels differ (max {MaxDifferentRatio:P2}); size {actualSize} vs {expectedSize}.");
    }

    private static byte[] Encode(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static (byte[] Pixels, PixelSize Size) Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var bitmap = new Bitmap(stream);
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }
        return (pixels, size);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eunomia.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Eunomia.slnx) not found.");
    }
}
