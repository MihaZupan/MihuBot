using Hardware.Info;

#nullable enable

namespace MihuBot.Helpers.Diagnostics;

/// <summary>Samples MihuBot's own and the machine's CPU/memory usage for display in the web UI.</summary>
public sealed class SystemUsageService : PeriodicBackgroundService
{
    private const double BytesPerGB = 1024 * 1024 * 1024;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    private readonly Logger _logger;
    private readonly HardwareInfo _hardwareInfo = new();
    private bool _loggedHardwareInfoFailure;

    private TimeSpan _previousProcessorTime;
    private long _previousTimestamp;

    /// <summary>The most recent sample, or null until we've taken the two samples CPU usage needs.</summary>
    public SystemUsageInfo? Current { get; private set; }

    public SystemUsageService(Logger logger)
        : base(new PeriodicTaskOptions
        {
            Interval = SampleInterval,
            RunImmediately = true,
            FailureBackoff = TimeSpan.FromMinutes(1),
        }, logger)
    {
        _logger = logger;
    }

    protected override Task RunIterationAsync(CancellationToken cancellationToken)
    {
        using Process process = Process.GetCurrentProcess();

        TimeSpan processorTime = process.TotalProcessorTime;
        long timestamp = Stopwatch.GetTimestamp();

        MachineUsage machine = ReadMachineUsage();

        double memoryAvailableGB = machine.MemoryTotalGB ?? (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / BytesPerGB);

        if (_previousTimestamp != 0)
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(_previousTimestamp, timestamp);

            double processCpuUsage = elapsed > TimeSpan.Zero
                ? (processorTime - _previousProcessorTime) / elapsed
                : 0;

            Current = new SystemUsageInfo(
                CpuCoresAvailable: machine.CoreCount,
                ProcessCpuCoresUsed: Math.Clamp(processCpuUsage, 0, machine.CoreCount),
                MachineCpuCoresUsed: machine.CpuCoresUsed,
                MemoryAvailableGB: memoryAvailableGB,
                ProcessMemoryUsageGB: Math.Clamp(process.WorkingSet64 / BytesPerGB, 0, memoryAvailableGB),
                MachineMemoryUsageGB: machine.MemoryUsedGB);
        }

        _previousProcessorTime = processorTime;
        _previousTimestamp = timestamp;

        return Task.CompletedTask;
    }

    private readonly record struct MachineUsage(double? CpuCoresUsed, double CoreCount, double? MemoryUsedGB, double? MemoryTotalGB);

    /// <summary>
    /// Machine-wide usage, collected the same way runtime-utils jobs report theirs.
    /// Not being able to read it only costs us that part of the display, so it never fails the iteration.
    /// </summary>
    private MachineUsage ReadMachineUsage()
    {
        try
        {
            // RefreshCPUList measures over a short window of its own, so this is already a rate, not a counter.
            _hardwareInfo.RefreshCPUList(includePercentProcessorTime: true);
            _hardwareInfo.RefreshMemoryStatus();

            List<CpuCore> cores = _hardwareInfo.CpuList.FirstOrDefault()?.CpuCoreList ?? [];

            if (cores.Count == 0)
            {
                return new MachineUsage(null, Environment.ProcessorCount, null, null);
            }

            double coresUsed = cores.Sum(core => (double)core.PercentProcessorTime) / 100;

            MemoryStatus memory = _hardwareInfo.MemoryStatus;
            double? memoryTotalGB = memory.TotalPhysical == 0 ? null : memory.TotalPhysical / BytesPerGB;
            double? memoryUsedGB = memoryTotalGB is null
                ? null
                : Math.Max(memoryTotalGB.Value - (memory.AvailablePhysical / BytesPerGB), 0);

            return new MachineUsage(Math.Clamp(coresUsed, 0, cores.Count), cores.Count, memoryUsedGB, memoryTotalGB);
        }
        catch (Exception ex)
        {
            if (!_loggedHardwareInfoFailure)
            {
                _loggedHardwareInfoFailure = true;
                _logger.DebugLog($"{nameof(SystemUsageService)}: failed to obtain hardware info: {ex}");
            }

            return new MachineUsage(null, Environment.ProcessorCount, null, null);
        }
    }
}
