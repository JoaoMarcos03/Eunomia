namespace Eunomia.Drivers.Abstractions;

/// <summary>Maintenance operations (Table tools, appendix B §2.2), in HeidiSQL's order.</summary>
public enum MaintenanceOperation
{
    Check,
    Analyze,
    Checksum,
    Optimize,
    Repair,
}

/// <summary>Options of the maintenance operations; each operation accepts only some of them (appendix B §2.2).</summary>
[Flags]
public enum MaintenanceOptions
{
    None = 0,
    Quick = 1,
    Fast = 2,
    Medium = 4,
    Extended = 8,
    Changed = 16,
    UseFrm = 32,
    ForUpgrade = 64,
}
