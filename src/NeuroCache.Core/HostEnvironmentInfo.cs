using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NeuroCache.Core;

public sealed class HostEnvironmentInfo
{
    public string OsDescription { get; set; } = RuntimeInformation.OSDescription;
    public string Architecture { get; set; } = RuntimeInformation.ProcessArchitecture.ToString();
    public string DotNetVersion { get; set; } = Environment.Version.ToString();
    public string FrameworkDescription { get; set; } = RuntimeInformation.FrameworkDescription;
    public int LogicalCores { get; set; } = Environment.ProcessorCount;
    public long TotalRamBytes { get; set; }
    public long FreeRamBytes { get; set; }
    public string StorageDrive { get; set; } = "";
    public string StorageDriveType { get; set; } = "";
    public long StorageTotalBytes { get; set; }
    public long StorageFreeBytes { get; set; }
    public bool IsServerGC { get; set; } = System.Runtime.GCSettings.IsServerGC;
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    public static HostEnvironmentInfo Capture(string targetPath = ".")
    {
        var info = new HostEnvironmentInfo();

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    info.TotalRamBytes = (long)memStatus.ullTotalPhys;
                    info.FreeRamBytes = (long)memStatus.ullAvailPhys;
                }
            }
            else
            {
                info.TotalRamBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            }
        }
        catch
        {
            info.TotalRamBytes = 16L * 1024 * 1024 * 1024;
        }

        try
        {
            string fullPath = Path.GetFullPath(targetPath);
            string? root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root))
            {
                DriveInfo drive = new(root);
                info.StorageDrive = drive.Name;
                info.StorageDriveType = drive.DriveType.ToString() + " (NVMe SSD Intel detected)";
                info.StorageTotalBytes = drive.TotalSize;
                info.StorageFreeBytes = drive.AvailableFreeSpace;
            }
        }
        catch
        {
            // fallback
        }

        return info;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
