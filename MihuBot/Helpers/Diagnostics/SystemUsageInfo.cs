#nullable enable

namespace MihuBot.Helpers.Diagnostics;

/// <summary>
/// A snapshot of how much of the machine MihuBot and everything else running on it are using.
/// Machine-wide numbers are null on platforms where we can't obtain them.
/// </summary>
public sealed record SystemUsageInfo(
    double CpuCoresAvailable,
    double ProcessCpuCoresUsed,
    double? MachineCpuCoresUsed,
    double MemoryAvailableGB,
    double ProcessMemoryUsageGB,
    double? MachineMemoryUsageGB)
{
    public int ProcessCpuPercentage => Percentage(ProcessCpuCoresUsed, CpuCoresAvailable);
    public int MachineCpuPercentage => Percentage(MachineCpuCoresUsed ?? ProcessCpuCoresUsed, CpuCoresAvailable);

    public int ProcessMemoryPercentage => Percentage(ProcessMemoryUsageGB, MemoryAvailableGB);
    public int MachineMemoryPercentage => Percentage(MachineMemoryUsageGB ?? ProcessMemoryUsageGB, MemoryAvailableGB);

    private static int Percentage(double used, double available) =>
        available > 0 ? Math.Clamp((int)(used / available * 100), 0, 100) : 0;
}
