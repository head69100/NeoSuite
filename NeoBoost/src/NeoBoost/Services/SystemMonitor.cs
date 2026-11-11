using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NeoBoost.Services;

public enum DiskKind
{
    Unknown,
    Hdd,
    Ssd,
}

/// <summary>Instantané de l'état du PC.</summary>
public sealed record SystemSnapshot(
    double CpuPercent,
    ulong RamTotal,
    ulong RamUsed,
    long DiskTotal,
    long DiskFree,
    TimeSpan Uptime,
    bool? OnBattery)
{
    public double RamPercent => RamTotal == 0 ? 0 : RamUsed * 100.0 / RamTotal;
    public double DiskFreePercent => DiskTotal == 0 ? 100 : DiskFree * 100.0 / DiskTotal;
}

/// <summary>Mesures système (processeur, mémoire, disque) sans dépendance aux compteurs de performance.</summary>
public static class SystemMonitor
{
    private static (ulong Idle, ulong Total)? _lastCpu;

    public static string SystemDrive { get; } = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    public static string CpuName { get; } = ReadCpuName();

    private static DiskKind? _systemDiskKind;

    /// <summary>Lit l'état actuel. Le pourcentage processeur est calculé depuis l'appel précédent.</summary>
    public static SystemSnapshot Sample()
    {
        var cpu = 0.0;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // Le temps noyau inclut le temps d'inactivité
            var total = kernel + user;
            if (_lastCpu is { } last && total > last.Total)
                cpu = 100.0 * (1 - (double)(idle - last.Idle) / (total - last.Total));
            _lastCpu = (idle, total);
        }

        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref mem);

        long diskTotal = 0, diskFree = 0;
        try
        {
            var drive = new DriveInfo(SystemDrive);
            diskTotal = drive.TotalSize;
            diskFree = drive.AvailableFreeSpace;
        }
        catch (IOException)
        {
        }

        bool? onBattery = GetSystemPowerStatus(out var power) && power.BatteryFlag != 128 // 128 = pas de batterie
            ? power.ACLineStatus == 0
            : null;

        return new SystemSnapshot(
            Math.Clamp(cpu, 0, 100),
            mem.TotalPhys,
            mem.TotalPhys - mem.AvailPhys,
            diskTotal,
            diskFree,
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            onBattery);
    }

    /// <summary>Type du disque qui porte Windows (SSD ou disque dur), lu une fois via WMI.</summary>
    public static async Task<DiskKind> GetSystemDiskKindAsync()
    {
        if (_systemDiskKind is { } known)
            return known;
        var kind = await Task.Run(ReadSystemDiskKind);
        _systemDiskKind = kind;
        return kind;
    }

    private static DiskKind ReadSystemDiskKind()
    {
        try
        {
            var letter = SystemDrive[0];
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var partitions = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'"));
            var diskNumber = partitions.Get().Cast<ManagementObject>().Select(p => Convert.ToUInt32(p["DiskNumber"])).FirstOrDefault(uint.MaxValue);
            if (diskNumber == uint.MaxValue)
                return DiskKind.Unknown;

            using var disks = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskNumber}'"));
            var media = disks.Get().Cast<ManagementObject>().Select(d => Convert.ToUInt16(d["MediaType"])).FirstOrDefault();
            return media switch
            {
                3 => DiskKind.Hdd,
                4 => DiskKind.Ssd,
                _ => DiskKind.Unknown,
            };
        }
        catch (Exception)
        {
            return DiskKind.Unknown;
        }
    }

    private static string ReadCpuName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Processeur";
    }

    public static string FormatBytes(double bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (1L << 30):0.0} Go",
        >= 1L << 20 => $"{bytes / (1L << 20):0} Mo",
        _ => $"{bytes / 1024:0} Ko",
    };

    public static string FormatDuration(TimeSpan span) => span.TotalDays >= 1
        ? $"{(int)span.TotalDays} j {span.Hours} h"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes:00}" : $"{span.Minutes} min";

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
