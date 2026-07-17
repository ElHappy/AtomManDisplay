// ═══════════════════════════════════════════════════════════════
//  INTEL GPU TILE TEMPERATURE — via PMT telemetry (PawnIO)
//
//  Meteor Lake's integrated Arc Graphics tile doesn't expose a
//  temperature sensor through LHM/IGCL (Intel's driver reports the
//  telemetry item as unsupported for this GPU/driver combination —
//  see LibreHardwareMonitor PR #2218) nor through i915 hwmon
//  (explicitly discrete-GPU-only upstream). The value IS present
//  on-die though, exposed via Intel PMT (Platform Monitoring
//  Technology) telemetry behind the OOBMSM PCI function (00:0A.0),
//  reachable with the same PawnIO kernel driver already used for
//  CPU MSR temperature (see PawnIo.EnsureRunning()).
//
//  BAR0 offset 0x6388/0x6389 (GCD_MIN/GCD_MAX — "Graphics Compute
//  Die") matches Intel's own published telemetry spec
//  (github.com/intel/Intel-PMT, xml/MTL/0/mtl_aggregator.xml,
//  Container_20) byte-for-byte — confirmed by diffing a live BAR0
//  dump at idle vs under GPU load. The raw GCD_MAX byte read a few
//  degrees below what HWiNFO64 reports for the same instant across
//  three measurements (44/55/60°C actual vs 39/48/54 raw), likely a
//  calibration/PVT offset applied downstream that isn't in the raw
//  telemetry byte. Applied as a fixed +4°C correction below — this
//  was derived from a single machine and may need adjustment on
//  other silicon samples.
// ═══════════════════════════════════════════════════════════════

using System;
using System.IO;

static class GpuPmtTemp
{
    const long Bar0GcdDword       = 0x6388; // dword containing GCD_MIN (byte 0) / GCD_MAX (byte 1)
    const int  CalibrationOffsetC = 4;

    private static PawnIoClient? _client;
    private static bool _unavailable;

    /// Reads the Meteor Lake GPU tile temperature via Intel PMT telemetry.
    /// Returns false if unsupported (non-Meteor Lake CPU, no OOBMSM device,
    /// PawnIO driver not running) — caller should fall back to 0/unknown.
    public static bool TryRead(out float celsius)
    {
        celsius = 0;
        if (_unavailable) return false;

        if (_client == null)
        {
            string binPath = Path.Combine(AppContext.BaseDirectory, "PawnIoModules", "IntelOOBMSM.bin");
            if (!File.Exists(binPath))
            {
                _unavailable = true;
                return false;
            }

            try
            {
                _client = new PawnIoClient(File.ReadAllBytes(binPath));
            }
            catch
            {
                _unavailable = true; // wrong CPU family, no PCI device at 00:0A.0, driver not running, etc.
                return false;
            }
        }

        try
        {
            var outVal = _client.Execute("ioctl_read_dword", new long[] { Bar0GcdDword }, 1);
            uint dword = (uint)outVal[0];
            byte gcdMax = (byte)((dword >> 8) & 0xFF);
            if (gcdMax == 0xFF) return false; // register not decoding

            celsius = gcdMax + CalibrationOffsetC;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Shutdown()
    {
        _client?.Dispose();
        _client = null;
    }
}
