using System.Globalization;

namespace Eunomia.App.Settings;

/// <summary>
/// What this computer has (logical processors and memory) and the limits it puts on the result memory settings (request
/// of 16 Nov 2026: "optimise for weaker PCs, with the resources adjusted to the PC"). The limits only lower a setting:
/// from 8 GB of memory up, the defaults (256 MB per result, 1 GB for all) are used as they are, and so is any value the
/// user set below the limit. <see cref="DataOptions.AdjustMemoryToComputer"/> turns them off.
/// </summary>
public sealed record MachineProfile(int Processors, long MemoryBytes)
{
    private const long Mib = 1024L * 1024;
    private const long Gib = 1024 * Mib;

    /// <summary>Nothing known: no limit is lowered.</summary>
    public static MachineProfile Unknown { get; } = new(0, 0);

    /// <summary>
    /// The memory the process can use (physical memory, or the job or container limit), from the runtime; 0 when it
    /// cannot tell, and then nothing is lowered.
    /// </summary>
    public static MachineProfile Detect() => new(Environment.ProcessorCount, Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes));

    /// <summary>All the results in memory together: an eighth of the memory, at least 256 MB.</summary>
    public int GlobalResultMemoryLimitMb => MemoryBytes <= 0 ? int.MaxValue : (int)Math.Clamp(MemoryBytes / 8 / Mib, 256, int.MaxValue);

    /// <summary>One result: a quarter of <see cref="GlobalResultMemoryLimitMb"/>, at least 64 MB.</summary>
    public int ResultMemoryLimitMb => MemoryBytes <= 0 ? int.MaxValue : Math.Max(64, GlobalResultMemoryLimitMb / 4);

    /// <summary>The memory for one result that is used: the setting, lowered to the limit when the adjustment is on.</summary>
    public int ResultMemoryMb(DataOptions data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.AdjustMemoryToComputer ? Math.Min(data.ResultMemoryMb, ResultMemoryLimitMb) : data.ResultMemoryMb;
    }

    /// <summary>The memory for all results that is used: the setting, lowered to the limit when the adjustment is on.</summary>
    public int GlobalResultMemoryMb(DataOptions data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.AdjustMemoryToComputer ? Math.Min(data.GlobalResultMemoryMb, GlobalResultMemoryLimitMb) : data.GlobalResultMemoryMb;
    }

    /// <summary>"8 processors, 15.8 GB of memory", for the preferences and the diagnostics log.</summary>
    public string Description => string.Create(
        CultureInfo.InvariantCulture,
        $"{(Processors > 0 ? Processors.ToString(CultureInfo.InvariantCulture) : "unknown")} processor{(Processors == 1 ? string.Empty : "s")}, {(MemoryBytes > 0 ? (MemoryBytes / (double)Gib).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : "unknown amount")} of memory");
}
