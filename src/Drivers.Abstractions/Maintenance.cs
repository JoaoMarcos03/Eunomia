namespace Eunomia.Drivers.Abstractions;

/// <summary>Operações do Maintenance (Table tools, anexo B §2.2), pela ordem do HeidiSQL.</summary>
public enum MaintenanceOperation
{
    Check,
    Analyze,
    Checksum,
    Optimize,
    Repair,
}

/// <summary>Opções das operações de manutenção; cada operação aceita só algumas (anexo B §2.2).</summary>
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
