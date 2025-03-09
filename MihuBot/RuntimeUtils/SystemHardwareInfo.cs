namespace MihuBot.RuntimeUtils;

public sealed record SystemHardwareInfo(double CpuUsage, double CpuCoresAvailable, double MemoryUsageGB, double MemoryAvailableGB)
{
    public int CpuUsagePercentage => (int)(CpuUsage / CpuCoresAvailable * 100);
    public int MemoryUsagePercentage => (int)(MemoryUsageGB / MemoryAvailableGB * 100);
}
