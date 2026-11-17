using Eunomia.App.Settings;

namespace Eunomia.App.Tests;

/// <summary>
/// The memory limits for the results on weaker computers (request of 16 Nov 2026): an eighth of the memory for all
/// results, a quarter of that for one; they only lower a setting, and from 8 GB up the defaults stay as they are.
/// </summary>
public sealed class MachineProfileTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(1, 256, 64)]
    [InlineData(2, 256, 64)]
    [InlineData(4, 512, 128)]
    [InlineData(6, 768, 192)]
    [InlineData(8, 1024, 256)]
    [InlineData(16, 2048, 512)]
    [InlineData(64, 8192, 2048)]
    public void TheLimitsFollowTheMemory(int gigabytes, int all, int one)
    {
        var machine = new MachineProfile(4, gigabytes * Gib);
        Assert.Equal(all, machine.GlobalResultMemoryLimitMb);
        Assert.Equal(one, machine.ResultMemoryLimitMb);
    }

    [Fact]
    public void FromEightGigabytesUpTheDefaultsStayAsTheyAre()
    {
        var defaults = new DataOptions();
        Assert.True(defaults.AdjustMemoryToComputer);
        foreach (var gigabytes in new[] { 8, 12, 16, 32 })
        {
            var machine = new MachineProfile(8, gigabytes * Gib);
            Assert.Equal(defaults.ResultMemoryMb, machine.ResultMemoryMb(defaults));
            Assert.Equal(defaults.GlobalResultMemoryMb, machine.GlobalResultMemoryMb(defaults));
        }
    }

    [Fact]
    public void OnASmallComputerTheSettingsAreLowered()
    {
        var data = new DataOptions { ResultMemoryMb = 256, GlobalResultMemoryMb = 1024 };
        var machine = new MachineProfile(2, 4 * Gib);
        Assert.Equal(128, machine.ResultMemoryMb(data));
        Assert.Equal(512, machine.GlobalResultMemoryMb(data));
    }

    [Fact]
    public void AValueBelowTheLimitIsKept()
    {
        var data = new DataOptions { ResultMemoryMb = 32, GlobalResultMemoryMb = 100 };
        var machine = new MachineProfile(2, 4 * Gib);
        Assert.Equal(32, machine.ResultMemoryMb(data));
        Assert.Equal(100, machine.GlobalResultMemoryMb(data));
    }

    [Fact]
    public void WithTheOptionOffTheSettingsAreUsedAsTheyAre()
    {
        var data = new DataOptions { ResultMemoryMb = 4096, GlobalResultMemoryMb = 16_384, AdjustMemoryToComputer = false };
        var machine = new MachineProfile(2, 4 * Gib);
        Assert.Equal(4096, machine.ResultMemoryMb(data));
        Assert.Equal(16_384, machine.GlobalResultMemoryMb(data));
    }

    [Fact]
    public void AnUnknownComputerLowersNothing()
    {
        var data = new DataOptions { ResultMemoryMb = 65_536, GlobalResultMemoryMb = 262_144 };
        Assert.Equal(int.MaxValue, MachineProfile.Unknown.ResultMemoryLimitMb);
        Assert.Equal(int.MaxValue, MachineProfile.Unknown.GlobalResultMemoryLimitMb);
        Assert.Equal(65_536, MachineProfile.Unknown.ResultMemoryMb(data));
        Assert.Equal(262_144, MachineProfile.Unknown.GlobalResultMemoryMb(data));
    }

    [Fact]
    public void ThisComputerIsDetected()
    {
        var machine = MachineProfile.Detect();
        Assert.Equal(Environment.ProcessorCount, machine.Processors);
        Assert.True(machine.MemoryBytes > 0, "The runtime reports the memory available to the process.");
        Assert.InRange(machine.ResultMemoryLimitMb, 64, machine.GlobalResultMemoryLimitMb);
    }

    [Theory]
    [InlineData(8, 16L * Gib, "8 processors, 16.0 GB of memory")]
    [InlineData(1, 3L * Gib / 2, "1 processor, 1.5 GB of memory")]
    [InlineData(0, 0L, "unknown processors, unknown amount of memory")]
    public void Description(int processors, long memory, string expected) =>
        Assert.Equal(expected, new MachineProfile(processors, memory).Description);

    [Fact]
    public void TheSettingsAreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => MachineProfile.Unknown.ResultMemoryMb(null!));
        Assert.Throws<ArgumentNullException>(() => MachineProfile.Unknown.GlobalResultMemoryMb(null!));
    }
}
