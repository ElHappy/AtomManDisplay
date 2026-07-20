// AtomMan Serial Display Daemon — C# + LibreHardwareMonitor
// ============================================================
// Protocol (from screen.py / README):
//   ENQ  (display → host):  AA 05 <SEQ_ASCII> CC 33 C3 3C
//   REPLY (host → display): AA <TileID> 00 <SEQ_ASCII> {ASCII payload} CC 33 C3 3C
//
// Tile map (SEQ ASCII → TileID):
//   '2' 0x32 → CPU  0x53      '6' 0x36 → DATE 0x6B
//   '3' 0x33 → GPU  0x36      '7' 0x37 → NET  0x27
//   '4' 0x34 → MEM  0x49      '9' 0x39 → VOL  0x10
//   '5' 0x35 → DISK 0x4F      '<' 0x3C → BAT  0x1A

using System.Diagnostics;
using System.IO.Ports;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using NAudio.CoreAudioApi;

namespace AtomManDisplay;

// ═══════════════════════════════════════════════════════════════
//  TILE CONSTANTS
// ═══════════════════════════════════════════════════════════════
static class Tile
{
    public const byte CPU = 0x53;
    public const byte GPU = 0x36;
    public const byte MEM = 0x49;
    public const byte DSK = 0x4F;
    public const byte DAT = 0x6B;
    public const byte NET = 0x27;
    public const byte VOL = 0x10;
    public const byte BAT = 0x1A;

    /// <summary>Fixed SEQ ASCII byte the host sends for each tile (steady-state).</summary>
    public static byte SeqFor(byte tileId) => tileId switch
    {
        CPU => (byte)'2',
        GPU => (byte)'3',
        MEM => (byte)'4',
        DSK => (byte)'5',
        DAT => (byte)'6',
        NET => (byte)'7',
        VOL => (byte)'9',
        BAT => (byte)'<',
        _   => (byte)'2',
    };
}

// ═══════════════════════════════════════════════════════════════
//  LIBREHARDWAREMONITOR WRAPPER
// ═══════════════════════════════════════════════════════════════
sealed class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;

    public float  CpuTemp    { get; private set; }
    public float  CpuUsage   { get; private set; }
    public float  CpuFreqMHz { get; private set; }
    public string CpuName    { get; private set; } = "CPU";

    public float  GpuTemp    { get; private set; }
    public float  GpuUsage   { get; private set; }
    public string GpuName    { get; private set; } = "GPU";

    public float  FanRpm     { get; private set; }
    public float  DiskTempC  { get; private set; }

    public HardwareMonitor()
    {
        PawnIo.EnsureRunning();

        _computer = new Computer
        {
            IsCpuEnabled         = true,
            IsGpuEnabled         = true,
            IsMemoryEnabled      = false,   // RAM metrics from Win32 API
            IsStorageEnabled     = true,    // Needed for disk temperature
            IsNetworkEnabled     = false,   // Network from .NET
            IsControllerEnabled  = true,    // Fan controllers (SuperIO, EC)
            IsMotherboardEnabled = true,    // Exposes SuperIO/EC sub-hardware for fan RPM
        };
        _computer.Open();

        // Cache hardware names on startup
        foreach (var hw in _computer.Hardware)
        {
            if (hw.HardwareType == HardwareType.Cpu)
            {
                CpuName = hw.Name;
            }
            else if (hw.HardwareType is HardwareType.GpuNvidia
                                     or HardwareType.GpuAmd
                                     or HardwareType.GpuIntel)
            {
                GpuName = hw.Name;
            }
        }
    }

    public void Refresh()
    {
        foreach (var hw in _computer.Hardware)
        {
            hw.Update();

            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    ReadCpu(hw);
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    ReadGpu(hw);
                    break;
                case HardwareType.Storage:
                    ReadStorage(hw);
                    break;
            }

            // Sub-hardware covers fan controllers (SuperIO, EC) under CPU and Motherboard
            foreach (var sub in hw.SubHardware)
            {
                sub.Update();
                ReadFan(sub);
            }

            ReadFan(hw);
        }

        // WMI fallback for fan RPM when no SuperIO/EC is detected by LHM
        if (FanRpm == 0) FanRpm = WinMetrics.GetWmiFanRpm();
    }

    private void ReadCpu(IHardware hw)
    {
        float temp = 0, usage = 0, freq = 0;
        var coreLoads = new List<float>();

        foreach (var s in hw.Sensors)
        {
            if (s.Value is null) continue;
            switch (s.SensorType)
            {
                case SensorType.Temperature:
                    if (s.Value > temp) temp = s.Value.Value;
                    break;

                case SensorType.Load
                    when s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase):
                    usage = s.Value.Value;
                    break;

                // Collect individual core/thread loads for averaging fallback
                case SensorType.Load
                    when s.Name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase):
                    coreLoads.Add(s.Value.Value);
                    break;

                case SensorType.Clock
                    when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase):
                    if (s.Value > freq) freq = s.Value.Value;
                    break;
            }
        }

        // LHM "CPU Total" is always 0 on Intel Core Ultra (Meteor Lake) — average cores instead
        if (usage == 0 && coreLoads.Count > 0)
            usage = coreLoads.Average();

        // Temp and freq fallbacks for CPUs LHM can't read via MSR yet
        if (temp == 0) temp = WinMetrics.GetAcpiCpuTempC();
        if (freq == 0) freq = WinMetrics.GetCpuFreqMHz();

        if (temp  > 0) CpuTemp    = temp;
        if (usage > 0) CpuUsage   = usage;
        if (freq  > 0) CpuFreqMHz = freq;
    }

    private void ReadGpu(IHardware hw)
    {
        float temp = 0, usage = 0, usageFallback = 0;
        foreach (var s in hw.Sensors)
        {
            if (s.Value is null) continue;
            switch (s.SensorType)
            {
                case SensorType.Temperature:
                    if (s.Value > temp) temp = s.Value.Value;
                    break;
                // Intel Arc exposes "D3D 3D" as its primary 3D load sensor
                case SensorType.Load
                    when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                      || s.Name.Contains("GPU",  StringComparison.OrdinalIgnoreCase)
                      || s.Name == "D3D 3D":
                    if (s.Value > usage) usage = s.Value.Value;
                    break;
                case SensorType.Load:
                    if (usageFallback == 0) usageFallback = s.Value.Value;
                    break;
            }
        }
        // Intel Arc iGPU (Meteor Lake+): IGCL/driver never reports temperature
        // as supported, so LHM's sensor never appears — read it via PMT instead.
        if (temp == 0 && GpuPmtTemp.TryRead(out float pmtTemp))
            temp = pmtTemp;

        if (temp  > 0) GpuTemp  = temp;
        float finalUsage = usage > 0 ? usage : usageFallback;
        if (finalUsage > 0) GpuUsage = finalUsage;
    }

    private void ReadStorage(IHardware hw)
    {
        foreach (var s in hw.Sensors)
        {
            // Skip threshold sensors like "Warning Temperature" and "Critical Temperature"
            if (s.SensorType == SensorType.Temperature && s.Value is > 0 &&
                !s.Name.Contains("Warning",  StringComparison.OrdinalIgnoreCase) &&
                !s.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase))
            {
                DiskTempC = s.Value.Value;
                return;
            }
        }
    }

    private void ReadFan(IHardware hw)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType == SensorType.Fan && s.Value is > 0)
            {
                FanRpm = s.Value.Value;
                return;
            }
        }
    }

    public void DumpSensors()
    {
        Console.WriteLine("\n═══ LibreHardwareMonitor sensor dump ═══");
        foreach (var hw in _computer.Hardware)
        {
            hw.Update();
            Console.WriteLine($"\n[{hw.HardwareType}] {hw.Name}");
            foreach (var s in hw.Sensors)
                Console.WriteLine($"  {s.SensorType,-15} \"{s.Name}\"  =  {s.Value?.ToString("F2") ?? "null"}");

            foreach (var sub in hw.SubHardware)
            {
                sub.Update();
                Console.WriteLine($"  └─ [{sub.HardwareType}] {sub.Name}");
                foreach (var s in sub.Sensors)
                    Console.WriteLine($"       {s.SensorType,-15} \"{s.Name}\"  =  {s.Value?.ToString("F2") ?? "null"}");
            }
        }
        Console.WriteLine("\n═══ WMI / ACPI fallbacks ═══");
        Console.WriteLine($"  CPU Freq PC    : {WinMetrics.GetCpuFreqMHz():F0} MHz");
        Console.WriteLine($"  WMI Fan RPM    : {WinMetrics.GetWmiFanRpm():F0}");
        Console.WriteLine($"  ACPI CPU Temp  : {WinMetrics.GetAcpiCpuTempC():F1} °C  (both methods)");
        Console.WriteLine(GpuPmtTemp.TryRead(out float pmtTemp)
            ? $"  PMT GPU Temp   : {pmtTemp:F1} °C  (GCD_MAX + calibration, Intel PMT/OOBMSM)"
            : "  PMT GPU Temp   : unavailable (not Meteor Lake+, or no OOBMSM device)");
        Console.WriteLine("\n  — Extended WMI scan —");
        WinMetrics.DumpWmiThermalFan();
        Console.WriteLine("═══════════════════════════════════════");
    }

    public void Dispose()
    {
        _computer.Close();
        GpuPmtTemp.Shutdown();
    }
}

// ═══════════════════════════════════════════════════════════════
//  PAWNIO DRIVER MANAGER
//  LHM 0.9.5+ requires the PawnIO kernel driver for temperature
//  sensors on Intel Core Ultra (Meteor Lake) and other modern CPUs.
//  The PawnIO installer leaves the service registration and the .sys
//  in the Windows DriverStore even after uninstall, so we can simply
//  start the service without needing to re-install anything.
// ═══════════════════════════════════════════════════════════════
static class PawnIo
{
    const string Name = "PawnIO";

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern IntPtr OpenSCManager(string? machine, string? db, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool StartService(IntPtr svc, uint argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CloseServiceHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatus(IntPtr svc, out SERVICE_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted,
                    dwWin32ExitCode, dwServiceSpecificExitCode,
                    dwCheckPoint, dwWaitHint;
    }

    const uint SC_MANAGER_CONNECT    = 0x0001;
    const uint SERVICE_START         = 0x0010;
    const uint SERVICE_QUERY         = 0x0004;
    const uint SERVICE_RUNNING       = 0x00000004;
    const int  ERROR_ALREADY_RUNNING = 1056;

    public static void EnsureRunning()
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            Console.WriteLine($"[PawnIO] Cannot open SCM (run as Administrator). Error={Marshal.GetLastWin32Error()}");
            return;
        }
        try
        {
            IntPtr svc = OpenService(scm, Name, SERVICE_START | SERVICE_QUERY);
            if (svc == IntPtr.Zero)
            {
                Console.WriteLine("[PawnIO] Service not registered — install PawnIO once to enable temperature sensors.");
                return;
            }
            try
            {
                QueryServiceStatus(svc, out var st);
                if (st.dwCurrentState == SERVICE_RUNNING)
                {
                    Console.WriteLine("[PawnIO] Already running.");
                    return;
                }

                bool ok = StartService(svc, 0, IntPtr.Zero);
                if (ok)
                    Console.WriteLine("[PawnIO] Driver started — temperature sensors enabled.");
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_ALREADY_RUNNING)
                        Console.WriteLine("[PawnIO] Already running.");
                    else
                        Console.WriteLine($"[PawnIO] StartService failed. Error={err}");
                }
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }
}

// ═══════════════════════════════════════════════════════════════
//  WIN32 HELPERS  (RAM, Disk, Battery)
// ═══════════════════════════════════════════════════════════════
static class WinMetrics
{
    // ── Memory ──────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public record MemInfo(double UsedGB, double AvailGB, double TotalGB, int UsagePct);

    public static MemInfo GetMemory()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref ms);
        double total = ms.ullTotalPhys / (1024.0 * 1024 * 1024);
        double avail = ms.ullAvailPhys / (1024.0 * 1024 * 1024);
        double used  = total - avail;
        int    pct   = (int)Math.Round(100.0 * used / Math.Max(1, total));
        return new(Math.Round(used, 1), Math.Round(avail, 1), Math.Round(total, 1), pct);
    }

    // ── Disk ─────────────────────────────────────────────────────
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    public record DiskInfo(long UsedGB, long TotalGB, int UsagePct);

    public static DiskInfo GetDisk(string path = "C:\\")
    {
        GetDiskFreeSpaceEx(path, out _, out ulong total, out ulong free);
        const double gb = 1024.0 * 1024 * 1024;
        long totalGB = (long)(total / gb);
        long freeGB  = (long)(free  / gb);
        long usedGB  = totalGB - freeGB;
        int  pct     = totalGB > 0 ? (int)Math.Round(100.0 * usedGB / totalGB) : 0;
        return new(usedGB, totalGB, pct);
    }

    // ── Battery ──────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte  ACLineStatus;
        public byte  BatteryFlag;
        public byte  BatteryLifePercent;
        public byte  SystemStatusFlag;
        public uint  BatteryLifeTime;
        public uint  BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    /// <summary>
    /// Returns battery percentage 0-100, or 177 if no battery present (desktop).
    /// </summary>
    public static int GetBatteryPct()
    {
        GetSystemPowerStatus(out var s);
        if (s.BatteryFlag == 128) return 177;   // 128 = NoSystemBattery sentinel
        return s.BatteryLifePercent;            // already 0-100
    }

    // ── RAM vendor (WMI) ─────────────────────────────────────────
    private static string? _ramVendorCache;
    public static string GetRamVendor()
    {
        if (_ramVendorCache != null) return _ramVendorCache;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer FROM Win32_PhysicalMemory");
            foreach (ManagementObject obj in searcher.Get())
            {
                var m = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(m) &&
                    m is not "Unknown" and not "Not Specified" and not "To Be Filled By O.E.M.")
                {
                    return _ramVendorCache = NormalizeVendor(m);
                }
            }
        }
        catch { }
        return _ramVendorCache = "Memory";
    }

    // ── Disk label (WMI) ─────────────────────────────────────────
    private static string? _diskLabelCache;
    public static string GetDiskLabel()
    {
        if (_diskLabelCache != null) return _diskLabelCache;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Model FROM Win32_DiskDrive");
            foreach (ManagementObject obj in searcher.Get())
            {
                var model = obj["Model"]?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(model))
                    return _diskLabelCache = model;
            }
        }
        catch { }
        return _diskLabelCache = "Disk";
    }

    private static string NormalizeVendor(string m) => m
        .Replace("Micron Technology", "Micron")
        .Replace("Samsung Electronics", "Samsung")
        .Replace("HYNIX", "SK hynix")
        .Replace("Hynix",  "SK hynix")
        .Trim();

    // ── CPU Temperature — two fallbacks for LHM-unsupported CPUs ─────────────
    public static float GetAcpiCpuTempC()
    {
        // Attempt 1: MSAcpi_ThermalZoneTemperature (root\WMI)
        try
        {
            using var s1 = new ManagementObjectSearcher(
                @"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            float maxC = 0;
            foreach (ManagementObject obj in s1.Get())
            {
                float c = (float)(Convert.ToDouble(obj["CurrentTemperature"]) / 10.0 - 273.15);
                if (c > maxC && c < 150) maxC = c;
            }
            if (maxC > 0) return maxC;
        }
        catch { }

        // Attempt 2: Win32 Perf Counter thermal zones (root\CIMV2)
        try
        {
            using var s2 = new ManagementObjectSearcher(
                "SELECT HighPrecisionTemperature FROM " +
                "Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            float maxC = 0;
            foreach (ManagementObject obj in s2.Get())
            {
                ulong raw = Convert.ToUInt64(obj["HighPrecisionTemperature"]);
                float c   = (float)(raw / 10.0 - 273.15);
                if (c > maxC && c < 150) maxC = c;
            }
            if (maxC > 0) return maxC;
        }
        catch { }

        return 0;
    }

    // ── CPU Frequency via Performance Counter ─────────────────────────────────
    private static PerformanceCounter? _freqCounter;
    public static float GetCpuFreqMHz()
    {
        try
        {
            _freqCounter ??= new PerformanceCounter(
                "Processor Information", "Processor Frequency", "_Total");
            return _freqCounter.NextValue();
        }
        catch { return 0; }
    }

    // ── Fan RPM — multiple fallbacks ──────────────────────────────────────────
    public static float GetWmiFanRpm()
    {
        // Win32_Fan (rarely populated on modern systems)
        try
        {
            using var s1 = new ManagementObjectSearcher("SELECT DesiredSpeed FROM Win32_Fan");
            foreach (ManagementObject obj in s1.Get())
            {
                float rpm = Convert.ToSingle(obj["DesiredSpeed"] ?? 0);
                if (rpm > 0) return rpm;
            }
        }
        catch { }

        // Win32_PerfFormattedData_Counters_FanInformation (some OEM boards)
        try
        {
            using var s2 = new ManagementObjectSearcher(
                "SELECT RotationsPerMinute FROM Win32_PerfFormattedData_Counters_FanInformation");
            foreach (ManagementObject obj in s2.Get())
            {
                float rpm = Convert.ToSingle(obj["RotationsPerMinute"] ?? 0);
                if (rpm > 0) return rpm;
            }
        }
        catch { }

        return 0;
    }

    // ── WMI namespace / class scanner used by --debug ─────────────────────────
    public static void DumpWmiThermalFan()
    {
        // Perf counter thermal zones (shows zone names + temps)
        Console.WriteLine("\n  [Win32_PerfFormattedData_Counters_ThermalZoneInformation]");
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name,HighPrecisionTemperature FROM " +
                "Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            foreach (ManagementObject obj in s.Get())
            {
                ulong raw = Convert.ToUInt64(obj["HighPrecisionTemperature"]);
                float c   = (float)(raw / 10.0 - 273.15);
                Console.WriteLine($"    Name={obj["Name"]}  Temp={c:F1} °C");
            }
        }
        catch (Exception ex) { Console.WriteLine($"    Error: {ex.Message}"); }

        // All ACPI thermal zones with names
        Console.WriteLine("\n  [MSAcpi_ThermalZoneTemperature — root\\WMI]");
        try
        {
            using var s = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT InstanceName,CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject obj in s.Get())
            {
                float c = (float)(Convert.ToDouble(obj["CurrentTemperature"]) / 10.0 - 273.15);
                Console.WriteLine($"    Zone={obj["InstanceName"]}  Temp={c:F1} °C");
            }
        }
        catch (Exception ex) { Console.WriteLine($"    Error: {ex.Message}"); }

        // Fan perf counter
        Console.WriteLine("\n  [Win32_PerfFormattedData_Counters_FanInformation]");
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name,RotationsPerMinute FROM " +
                "Win32_PerfFormattedData_Counters_FanInformation");
            foreach (ManagementObject obj in s.Get())
                Console.WriteLine($"    Name={obj["Name"]}  RPM={obj["RotationsPerMinute"]}");
        }
        catch (Exception ex) { Console.WriteLine($"    Error: {ex.Message}"); }

        // Scan root\WMI for any class with Fan or Thermal in the name
        Console.WriteLine("\n  [root\\WMI classes matching Fan|Thermal|Temp|Intel|Power]");
        try
        {
            var scope = new ManagementScope(@"root\wmi");
            scope.Connect();
            using var query = new ManagementObjectSearcher(
                scope, new ObjectQuery("SELECT * FROM meta_class"));
            foreach (ManagementClass cls in query.Get().Cast<ManagementClass>())
            {
                string n = cls.ClassPath.ClassName;
                if (n.Contains("Fan",     StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("Thermal", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("Temp",    StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("Intel",   StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("Power",   StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"    {n}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"    Error: {ex.Message}"); }
    }
}

// ═══════════════════════════════════════════════════════════════
//  VOLUME  (NAudio Core Audio API)
// ═══════════════════════════════════════════════════════════════
static class VolumeHelper
{
    public static int GetMasterVolumePct()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
        }
        catch { return -1; }
    }
}

// ═══════════════════════════════════════════════════════════════
//  NETWORK THROUGHPUT
// ═══════════════════════════════════════════════════════════════
sealed class NetMeter
{
    private NetworkInterface? _iface;
    private long     _lastRx, _lastTx;
    private DateTime _lastTime = DateTime.UtcNow;
    private DateTime _lastPickAttempt = DateTime.MinValue;

    public string IfaceName { get; private set; } = "N/A";

    public NetMeter() => Pick();

    private static bool IsVirtual(NetworkInterface n)
    {
        static bool Has(string s, string sub) =>
            s.Contains(sub, StringComparison.OrdinalIgnoreCase);
        return Has(n.Name,        "vEthernet")
            || Has(n.Name,        "WSL")
            || Has(n.Name,        "Hyper-V")
            || Has(n.Description, "Hyper-V")
            || Has(n.Description, "Virtual")
            || Has(n.Description, "Pseudo");
    }

    private void Pick()
    {
        _lastPickAttempt = DateTime.UtcNow;
        _iface = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                     && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                     && !IsVirtual(n))
            .OrderByDescending(n =>
            {
                // Interfaces with a default gateway are connected to the internet
                bool hasGateway = n.GetIPProperties().GatewayAddresses.Count > 0;
                int typeScore   = n.NetworkInterfaceType == NetworkInterfaceType.Ethernet        ? 3 :
                                  n.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ? 3 :
                                  n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211   ? 2 : 1;
                return (hasGateway ? 10 : 0) + typeScore;
            })
            .FirstOrDefault();

        IfaceName = _iface?.Name ?? "N/A";
        if (_iface is not null)
        {
            var s = _iface.GetIPv4Statistics();
            _lastRx = s.BytesReceived;
            _lastTx = s.BytesSent;
        }
        _lastTime = DateTime.UtcNow;
    }

    public (double RxKBs, double TxKBs) GetRates()
    {
        // No adapter was up yet the last time we looked (e.g. this process was
        // launched by a boot-time service before Wi-Fi/Ethernet finished
        // coming up) — keep retrying instead of staying stuck at N/A forever.
        if (_iface is null)
        {
            if ((DateTime.UtcNow - _lastPickAttempt).TotalSeconds < 5) return (0, 0);
            Pick();
            if (_iface is null) return (0, 0);
        }
        try
        {
            var s  = _iface.GetIPv4Statistics();
            var now = DateTime.UtcNow;
            double dt = Math.Max(0.001, (now - _lastTime).TotalSeconds);
            double rx = Math.Max(0, (s.BytesReceived - _lastRx) / dt / 1024.0);
            double tx = Math.Max(0, (s.BytesSent     - _lastTx) / dt / 1024.0);
            _lastRx = s.BytesReceived;
            _lastTx = s.BytesSent;
            _lastTime = now;
            return (rx, tx);
        }
        catch { Pick(); return (0, 0); }
    }

    public static string Fmt(double kBs)
    {
        if (kBs < 1024)          return $"{kBs:F1} K/s";
        if (kBs < 1024 * 1024)   return $"{kBs / 1024:F1} M/s";
        return                           $"{kBs / 1024 / 1024:F1} G/s";
    }
}

// ═══════════════════════════════════════════════════════════════
//  OPENWEATHER  (optional, cached)
// ═══════════════════════════════════════════════════════════════
record WeatherData(int WeatherN, int Lo, int Hi, string Zone, string Desc);

sealed class WeatherCache
{
    private readonly string _apiKey;
    private readonly string _location;
    private readonly string _units;
    private readonly int    _refreshSec;
    private WeatherData?    _data;
    private DateTime        _lastFetch  = DateTime.MinValue;
    private bool            _warnedNoKey;

    public WeatherData? Current => _data;

    public WeatherCache(string apiKey, string location,
                        string units = "metric", int refreshSec = 600)
    {
        _apiKey     = apiKey.Trim();
        _location   = location.Trim();
        _units      = units;
        _refreshSec = refreshSec;
    }

    public void RefreshIfStale()
    {
        if ((DateTime.UtcNow - _lastFetch).TotalSeconds < _refreshSec) return;
        _lastFetch = DateTime.UtcNow;  // prevent rapid retries on failure

        if (string.IsNullOrEmpty(_apiKey))
        {
            if (!_warnedNoKey)
            {
                Console.WriteLine("[Weather] No API key — DATE tile will have blank weather fields.");
                Console.WriteLine("[Weather] Set env var OW_API_KEY to enable weather.");
                _warnedNoKey = true;
            }
            return;
        }

        // Fetch in background — don't block the serial loop
        Task.Run(async () =>
        {
            try
            {
                var result = await FetchAsync();
                if (result is not null)
                {
                    _data = result;
                    Console.WriteLine($"[Weather] Updated: code={result.WeatherN} " +
                                      $"lo={result.Lo} hi={result.Hi} zone={result.Zone}");
                }
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("401"))
            {
                Console.WriteLine("[Weather] 401 Unauthorized — API key inválida o aún no activada.");
                Console.WriteLine("[Weather] Las claves nuevas de OpenWeatherMap tardan hasta 2 horas en activarse.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Weather] Error: {ex.Message}");
            }
        });
    }

    private async Task<WeatherData?> FetchAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.Add("User-Agent", "AtomManDisplay/1.0");

        var (lat, lon, zone) = await ResolveLocationAsync(http);
        if (lat == 0 && lon == 0) return null;

        // ── Current conditions (free tier: data/2.5/weather) ─────────────
        string currentUrl = $"https://api.openweathermap.org/data/2.5/weather?" +
                            $"lat={lat:F6}&lon={lon:F6}&units={_units}&lang=en&appid={_apiKey}";
        string currentJson = await GetStringOrThrow(http, currentUrl);

        using var currentDoc = JsonDocument.Parse(currentJson);
        var wArr    = currentDoc.RootElement.GetProperty("weather")[0];
        int  owId   = wArr.GetProperty("id").GetInt32();
        string icon = wArr.GetProperty("icon").GetString() ?? "";
        string desc = wArr.GetProperty("description").GetString() ?? "";
        int weatherN = MapOwId(owId, icon);

        // ── 5-day / 3-hour forecast for today's lo/hi (free tier: data/2.5/forecast) ──
        string forecastUrl = $"https://api.openweathermap.org/data/2.5/forecast?" +
                             $"lat={lat:F6}&lon={lon:F6}&units={_units}&lang=en&appid={_apiKey}";
        string forecastJson = await GetStringOrThrow(http, forecastUrl);

        using var forecastDoc = JsonDocument.Parse(forecastJson);
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        double lo = double.MaxValue, hi = double.MinValue;

        foreach (var entry in forecastDoc.RootElement.GetProperty("list").EnumerateArray())
        {
            string dt = entry.GetProperty("dt_txt").GetString() ?? "";
            if (!dt.StartsWith(today)) continue;
            var main = entry.GetProperty("main");
            double tMin = main.GetProperty("temp_min").GetDouble();
            double tMax = main.GetProperty("temp_max").GetDouble();
            if (tMin < lo) lo = tMin;
            if (tMax > hi) hi = tMax;
        }

        // Fallback: if no entries matched today use current temp for both
        if (lo == double.MaxValue)
        {
            double cur = currentDoc.RootElement.GetProperty("main")
                                               .GetProperty("temp").GetDouble();
            lo = hi = cur;
        }

        return new WeatherData(weatherN, (int)Math.Round(lo), (int)Math.Round(hi),
                               Ascii(zone), Ascii(desc));
    }

    private static async Task<string> GetStringOrThrow(HttpClient http, string url)
    {
        var resp = await http.GetAsync(url);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"HTTP {(int)resp.StatusCode} from {new Uri(url).Host}: {body}");
        return body;
    }

    private async Task<(double lat, double lon, string zone)> ResolveLocationAsync(HttpClient http)
    {
        // Already lat,lon?
        var parts = _location.Split(',');
        if (parts.Length == 2
            && double.TryParse(parts[0].Trim(), out double la)
            && double.TryParse(parts[1].Trim(), out double lo))
            return (la, lo, _location);

        // City geocoding
        string url  = $"https://api.openweathermap.org/geo/1.0/direct?" +
                      $"q={Uri.EscapeDataString(_location)}&limit=1&appid={_apiKey}";
        string json = await GetStringOrThrow(http, url);
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement;
        if (arr.GetArrayLength() == 0) return (0, 0, "");

        var ent  = arr[0];
        double lat2 = ent.GetProperty("lat").GetDouble();
        double lon2 = ent.GetProperty("lon").GetDouble();
        string name = ent.TryGetProperty("name",    out var n) ? n.GetString() ?? "" : _location;
        string cc   = ent.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
        string zone = cc.Length > 0 ? $"{name},{cc}" : name;
        return (lat2, lon2, zone);
    }

    // Same mapping as Python _map_openweather_id_to_weatherN()
    private static int MapOwId(int id, string icon)
    {
        bool day = icon.EndsWith("d", StringComparison.OrdinalIgnoreCase);
        return id switch
        {
            800            => day ? 1 : 3,
            801            => day ? 5 : 6,
            802            => day ? 7 : 8,
            803 or 804     => 9,
            202 or 212 or 232 => 16,
            _ when id / 100 == 2 => 11,
            _ when id / 100 == 3 => 13,
            500            => 13,
            501            => 14,
            502 or 503 or 504 => 15,
            511            => 19,
            520 or 521 or 522 or 531 => 10,
            _ when id / 100 == 5 => 14,
            600            => 22,
            601            => 23,
            602 or 621 or 622 => 24,
            620            => 21,
            611 or 612 or 615 or 616 => 20,
            _ when id / 100 == 6 => 22,
            701 or 741     => 30,
            711 or 721     => 31,
            731 or 751     => 27,
            761 or 762     => 26,
            771            => 33,
            781            => 36,
            _ when id / 100 == 7 => 31,
            _              => 99,
        };
    }

    private static string Ascii(string s) =>
        new string(s.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray()).Replace(";", ",");
}

// ═══════════════════════════════════════════════════════════════
//  PAYLOAD BUILDERS  (match Python p_xxx() functions exactly)
// ═══════════════════════════════════════════════════════════════
static class Payloads
{
    public static string Cpu(HardwareMonitor hw)
    {
        int    temp    = (int)Math.Round(hw.CpuTemp);
        int    usage   = (int)Math.Round(hw.CpuUsage);
        long   freqKHz = (long)(hw.CpuFreqMHz * 1000);
        // {CPU:<name>;Tempr:<°C>;Useage:<pct>;Freq:<kHz>;Tempr1:<°C>;}
        return $"{{CPU:{hw.CpuName};Tempr:{temp};Useage:{usage};Freq:{freqKHz};Tempr1:{temp};}}";
    }

    public static string Gpu(HardwareMonitor hw)
    {
        int temp  = (int)Math.Round(hw.GpuTemp);
        int usage = (int)Math.Round(hw.GpuUsage);
        // {GPU:<name>;Tempr:<°C>;Useage:<pct>}
        return $"{{GPU:{hw.GpuName};Tempr:{temp};Useage:{usage}}}";
    }

    public static string Mem()
    {
        var m      = WinMetrics.GetMemory();
        string ven = WinMetrics.GetRamVendor();
        // {Memory:<vendor>;Used:<GB>;Available:<GB>;Total:<GB>;Useage:<pct>}
        return $"{{Memory:{ven};Used:{m.UsedGB};Available:{m.AvailGB};Total:{m.TotalGB};Useage:{m.UsagePct}}}";
    }

    public static string Disk(HardwareMonitor hw)
    {
        var d      = WinMetrics.GetDisk("C:\\");
        string lbl = WinMetrics.GetDiskLabel();
        int temp   = (int)Math.Round(hw.DiskTempC);
        // {DiskName:<label>;Tempr:<°C>;UsageSpace:<GB>;AllSpace:<GB>;Usage:<pct>}
        return $"{{DiskName:{lbl};Tempr:{temp};UsageSpace:{d.UsedGB};AllSpace:{d.TotalGB};Usage:{d.UsagePct}}}";
    }

    public static string Date(WeatherCache weather)
    {
        var now  = DateTime.Now;
        // Panel week: 0=Sunday … 6=Saturday  (matches .NET DayOfWeek enum)
        int week = (int)now.DayOfWeek;

        var w = weather.Current;
        if (w is not null)
            return $"{{Date:{now:yyyy/MM/dd};Time:{now:HH:mm:ss};Week:{week};" +
                   $"Weather:{w.WeatherN};TemprLo:{w.Lo},TemprHi:{w.Hi},Zone:{w.Zone},Desc:{w.Desc}}}";

        return $"{{Date:{now:yyyy/MM/dd};Time:{now:HH:mm:ss};Week:{week};" +
               $"Weather:;TemprLo:,TemprHi:,Zone:,Desc:}}";
    }

    public static string Net(HardwareMonitor hw, NetMeter net)
    {
        var (rx, tx) = net.GetRates();
        int rpm      = (int)Math.Round(hw.FanRpm);
        // {SPEED:<rpm>;NETWORK:<rx>,<tx>}
        return $"{{SPEED:{rpm};NETWORK:{NetMeter.Fmt(rx)},{NetMeter.Fmt(tx)}}}";
    }

    public static string Vol()
    {
        int vol = VolumeHelper.GetMasterVolumePct();
        return $"{{VOLUME:{vol}}}";
    }

    public static string Bat()
    {
        int pct = WinMetrics.GetBatteryPct();
        return $"{{Battery:{pct}}}";
    }
}

// ═══════════════════════════════════════════════════════════════
//  SERIAL PROTOCOL
// ═══════════════════════════════════════════════════════════════
static class Protocol
{
    private static readonly byte[] Trailer = [0xCC, 0x33, 0xC3, 0x3C];

    /// <summary>Build a reply frame: AA tileId 00 seq {payload_latin1} CC33C33C</summary>
    public static byte[] BuildReply(byte tileId, byte seq, string payload)
    {
        byte[] text  = Encoding.Latin1.GetBytes(payload);
        byte[] frame = new byte[4 + text.Length + 4];
        frame[0] = 0xAA;
        frame[1] = tileId;
        frame[2] = 0x00;
        frame[3] = seq;
        text.CopyTo(frame, 4);
        Trailer.CopyTo(frame, 4 + text.Length);
        return frame;
    }

    /// <summary>
    /// Read one ENQ from the display: AA 05 SEQ CC33C33C
    /// Returns the SEQ byte or null on timeout / bad frame.
    /// </summary>
    public static byte? ReadEnq(SerialPort port)
    {
        try
        {
            if (port.ReadByte() != 0xAA) return null;
            if (port.ReadByte() != 0x05) return null;
            int seq = port.ReadByte();
            if (seq < 0) return null;
            for (int i = 0; i < 4; i++)
                if (port.ReadByte() != Trailer[i]) return null;
            return (byte)seq;
        }
        catch (TimeoutException) { return null; }
    }

    private static bool IsAsciiSeq(byte b) => b is >= 0x30 and <= 0x39 or 0x3C;

    /// <summary>
    /// Send CPU/GPU/MEM tiles in rotation while waiting for 3 valid ENQs
    /// within 2 seconds — this matches Python's unlock_attempt() logic.
    /// </summary>
    public static bool UnlockAttempt(SerialPort port, HardwareMonitor hw,
                                     WeatherCache weather, NetMeter net,
                                     int attemptIdx, double windowSec)
    {
        Console.WriteLine($"[Attempt {attemptIdx}] Unlock window {windowSec:F0}s — echoing CPU/GPU/MEM");
        var deadline = DateTime.UtcNow.AddSeconds(windowSec);

        byte[] rotation = [Tile.CPU, Tile.GPU, Tile.MEM];
        int    idx       = 0;
        int    bootReplies = 0;
        var    enqTimes  = new Queue<DateTime>();

        while (DateTime.UtcNow < deadline)
        {
            hw.Refresh();
            var seq = ReadEnq(port);
            if (seq is null) continue;

            // Prune ENQ timestamps older than 2 s
            enqTimes.Enqueue(DateTime.UtcNow);
            while (enqTimes.Count > 0 &&
                   (DateTime.UtcNow - enqTimes.Peek()).TotalSeconds > 2.0)
                enqTimes.Dequeue();

            byte   tileId  = rotation[idx % rotation.Length];
            string payload = BuildPayload(tileId, hw, weather, net);
            byte[] frame   = BuildReply(tileId, seq.Value, payload);
            port.Write(frame, 0, frame.Length);

            if (IsAsciiSeq(seq.Value)) bootReplies++;
            idx++;

            if (bootReplies >= 3 && enqTimes.Count >= 5)
            {
                Console.WriteLine($"[Attempt {attemptIdx}] Display activated.");
                return true;
            }
        }

        Console.WriteLine($"[Attempt {attemptIdx}] Timed out — no activation.");
        return false;
    }

    public static string BuildPayload(byte tileId, HardwareMonitor hw,
                                      WeatherCache weather, NetMeter net)
        => tileId switch
        {
            Tile.CPU => Payloads.Cpu(hw),
            Tile.GPU => Payloads.Gpu(hw),
            Tile.MEM => Payloads.Mem(),
            Tile.DSK => Payloads.Disk(hw),
            Tile.DAT => Payloads.Date(weather),
            Tile.NET => Payloads.Net(hw, net),
            Tile.VOL => Payloads.Vol(),
            Tile.BAT => Payloads.Bat(),
            _        => "",
        };
}

// ═══════════════════════════════════════════════════════════════
//  DASHBOARD  (optional, console)
// ═══════════════════════════════════════════════════════════════
static class Dashboard
{
    private static DateTime _lastPrint = DateTime.MinValue;

    public static void Render(HardwareMonitor hw, WeatherCache weather, NetMeter net,
                              bool show, double minIntervalSec = 1.0)
    {
        if (!show) return;
        if ((DateTime.Now - _lastPrint).TotalSeconds < minIntervalSec) return;
        _lastPrint = DateTime.Now;

        Console.Clear();
        Console.WriteLine($"AtomMan   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine(new string('─', 60));
        Console.WriteLine($"CPU Model  : {hw.CpuName}");
        Console.WriteLine($"CPU Temp   : {hw.CpuTemp:F0} °C");
        Console.WriteLine($"CPU Usage  : {hw.CpuUsage:F0} %");
        Console.WriteLine($"CPU Freq   : {hw.CpuFreqMHz * 1000:F0} kHz");
        Console.WriteLine();
        Console.WriteLine($"GPU Model  : {hw.GpuName}");
        Console.WriteLine($"GPU Temp   : {hw.GpuTemp:F0} °C");
        Console.WriteLine($"GPU Usage  : {hw.GpuUsage:F0} %");
        Console.WriteLine();
        var m = WinMetrics.GetMemory();
        Console.WriteLine($"RAM Vendor : {WinMetrics.GetRamVendor()}");
        Console.WriteLine($"RAM Used   : {m.UsedGB} GB  /  {m.TotalGB} GB  ({m.UsagePct} %)");
        Console.WriteLine();
        var d = WinMetrics.GetDisk("C:\\");
        Console.WriteLine($"Disk Label : {WinMetrics.GetDiskLabel()}");
        Console.WriteLine($"Disk Temp  : {hw.DiskTempC:F0} °C");
        Console.WriteLine($"Disk Used  : {d.UsedGB} GB  /  {d.TotalGB} GB  ({d.UsagePct} %)");
        Console.WriteLine();
        var (rx, tx) = net.GetRates();
        Console.WriteLine($"Net Iface  : {net.IfaceName}");
        Console.WriteLine($"Net RX/TX  : {NetMeter.Fmt(rx)}  /  {NetMeter.Fmt(tx)}");
        Console.WriteLine($"Fan Speed  : {hw.FanRpm:F0} RPM");
        Console.WriteLine($"Volume     : {VolumeHelper.GetMasterVolumePct()} %");
        Console.WriteLine($"Battery    : {WinMetrics.GetBatteryPct()} %");
        Console.WriteLine();
        var w = weather.Current;
        if (w is not null)
        {
            Console.WriteLine($"Weather    : ONLINE  (code {w.WeatherN})");
            Console.WriteLine($"Temp Lo/Hi : {w.Lo} / {w.Hi} °C");
            Console.WriteLine($"Zone       : {w.Zone}");
            Console.WriteLine($"Desc       : {w.Desc}");
        }
        else
        {
            Console.WriteLine("Weather    : OFFLINE / no API key");
        }
        Console.WriteLine(new string('─', 60));
    }
}

// ═══════════════════════════════════════════════════════════════
//  ENTRY POINT
// ═══════════════════════════════════════════════════════════════
class Program
{
    // Loaded first — other static fields call Env() during initialization
    private static readonly Dictionary<string, string> _dotEnv = LoadDotEnv();

    // ── Configuration — edit here or use environment variables ──
    private const  string DefaultPort     = "COM3";
    private const  int    BaudRate        = 115200;
    private const  int    UnlockAttempts  = 3;
    private const  double UnlockWindowSec = 5.0;
    private const  double StartDelaySec   = 3.0;
    private const  int    PostWriteMs     = 6;    // matches Python POST_WRITE_SLEEP 0.006 s

    // Weather: set OW_API_KEY env var (free tier at openweathermap.org)
    private static readonly string ApiKey   = Env("OW_API_KEY",   "");
    private static readonly string Location = Env("OW_LOCATION",  "Monterrey,MX");
    private static readonly string Units    = Env("OW_UNITS",     "metric");

    // Tile rotation — same order as Python FULL_ROT
    private static readonly byte[] Rotation =
    [
        Tile.CPU, Tile.GPU, Tile.MEM, Tile.DSK,
        Tile.DAT, Tile.NET, Tile.VOL, Tile.BAT,
    ];

    static void Main(string[] args)
    {
        bool showDashboard = args.Contains("--dashboard");
        bool debugSensors  = args.Contains("--debug");
        string portName    = Env("ATOMMAN_PORT", DefaultPort);

        // First non-flag arg can be port name (e.g. "COM4")
        foreach (var a in args)
            if (a.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) portName = a;

        Console.WriteLine($"[AtomMan] Port={portName}  Baud={BaudRate}");
        Console.WriteLine("[AtomMan] Initialising LibreHardwareMonitor...");
        Console.WriteLine("[AtomMan] NOTE: Run as Administrator for CPU/GPU temperatures.");

        using var hw = new HardwareMonitor();

        if (debugSensors)
        {
            hw.DumpSensors();
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
            return;
        }
        var weather  = new WeatherCache(ApiKey, Location, Units);
        var net      = new NetMeter();

        // Graceful Ctrl-C / SIGTERM
        bool shutdown = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown = true; };

        // Pre-fetch weather in background immediately
        weather.RefreshIfStale();

        // ── Start delay ─────────────────────────────────────────
        Console.WriteLine($"[AtomMan] Waiting {StartDelaySec}s for USB CDC driver...");
        Thread.Sleep((int)(StartDelaySec * 1000));

        // ── Open serial port ────────────────────────────────────
        using var port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout  = 1000,
            WriteTimeout = 1000,
            DtrEnable    = true,
            RtsEnable    = false,
        };

        try
        {
            port.Open();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AtomMan] Cannot open {portName}: {ex.Message}");
            Console.WriteLine("[AtomMan] Check Device Manager for the correct COM port.");
            return;
        }

        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        Console.WriteLine($"[AtomMan] {portName} opened.");

        // ── Unlock phase ─────────────────────────────────────────
        bool activated = false;
        for (int i = 1; i <= UnlockAttempts && !shutdown; i++)
        {
            activated = Protocol.UnlockAttempt(port, hw, weather, net,
                                               i, UnlockWindowSec);
            if (activated) break;

            // DTR toggle to reset display state (same as Python)
            port.DtrEnable = false;
            Thread.Sleep(50);
            port.DtrEnable = true;
            Thread.Sleep(300);
        }

        if (!activated)
            Console.WriteLine("[WARN] Display may not be fully activated; continuing anyway.");
        else
            Console.WriteLine("[OK] Display activated — steady state.");

        // ── Steady-state loop ─────────────────────────────────────
        int tileIdx = 0;
        while (!shutdown)
        {
            hw.Refresh();
            weather.RefreshIfStale();

            var seq = Protocol.ReadEnq(port);

            if (seq is null)
            {
                // No ENQ — render dashboard if enabled
                Dashboard.Render(hw, weather, net, showDashboard);
                continue;
            }

            // Pick next tile in rotation; use fixed per-tile SEQ (steady state)
            byte tileId  = Rotation[tileIdx % Rotation.Length];
            byte seqByte = Tile.SeqFor(tileId);
            tileIdx++;

            string payload = Protocol.BuildPayload(tileId, hw, weather, net);
            byte[] frame   = Protocol.BuildReply(tileId, seqByte, payload);

            port.Write(frame, 0, frame.Length);
            Thread.Sleep(PostWriteMs);

            Dashboard.Render(hw, weather, net, showDashboard);
        }

        Console.WriteLine("[AtomMan] Shutting down.");
        port.Close();
    }

    // Reads from .env file first, then falls back to system environment variables.
    private static Dictionary<string, string> LoadDotEnv()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(AppContext.BaseDirectory, ".env");

        // Also look next to the .csproj when running via "dotnet run"
        if (!File.Exists(path))
        {
            string? dir = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('/', '\\'));
            while (dir is not null)
            {
                string candidate = Path.Combine(dir, ".env");
                if (File.Exists(candidate)) { path = candidate; break; }
                string parent = Path.GetDirectoryName(dir)!;
                if (parent == dir) break;
                dir = parent;
            }
        }

        if (!File.Exists(path)) return map;

        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            if (key.Length > 0) map[key] = val;
        }

        Console.WriteLine($"[Config] Loaded .env from {path}");
        return map;
    }

    private static string Env(string key, string def)
    {
        if (_dotEnv.TryGetValue(key, out string? v) && v.Length > 0) return v;
        return Environment.GetEnvironmentVariable(key) is { Length: > 0 } ev ? ev : def;
    }
}