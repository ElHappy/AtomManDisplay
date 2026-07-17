// ═══════════════════════════════════════════════════════════════
//  PAWNIOLIB.DLL CLIENT — thin P/Invoke wrapper
//  Talks to the already-installed PawnIO kernel driver via its
//  user-mode client library (PawnIOLib.dll). Used to load a
//  compiled Pawn module blob (.bin) and call its exported IOCTLs.
//  Reference: PawnIOLib.h in https://github.com/namazso/PawnIO
// ═══════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

sealed class PawnIoClient : IDisposable
{
    // PawnIOLib.dll is installed under Program Files, not on the default DLL
    // search path — resolve it explicitly instead of relying on PATH.
    static PawnIoClient()
    {
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, assembly, searchPath) =>
        {
            if (name != "PawnIOLib.dll")
                return IntPtr.Zero;

            foreach (var candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PawnIO", "PawnIOLib.dll"),
            })
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;
            }

            return IntPtr.Zero; // fall through to default probing (PATH etc.)
        });
    }

    [DllImport("PawnIOLib.dll")]
    private static extern int pawnio_open(out IntPtr handle);

    [DllImport("PawnIOLib.dll")]
    private static extern int pawnio_close(IntPtr handle);

    [DllImport("PawnIOLib.dll")]
    private static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);

    [DllImport("PawnIOLib.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int pawnio_execute(
        IntPtr handle,
        string name,
        long[] inBuf, UIntPtr inSize,
        long[] outBuf, UIntPtr outSize,
        out UIntPtr returnSize);

    private IntPtr _handle;

    public PawnIoClient(byte[] moduleBlob)
    {
        int hr = pawnio_open(out _handle);
        if (hr < 0)
            throw new InvalidOperationException($"pawnio_open failed: 0x{hr:X8}");

        hr = pawnio_load(_handle, moduleBlob, (UIntPtr)moduleBlob.Length);
        if (hr < 0)
        {
            pawnio_close(_handle);
            _handle = IntPtr.Zero;
            throw new InvalidOperationException($"pawnio_load failed: 0x{hr:X8}");
        }
    }

    /// Calls a named IOCTL exported by the loaded module.
    public long[] Execute(string name, long[] input, int outCount)
    {
        var outBuf = new long[Math.Max(outCount, 1)];
        int hr = pawnio_execute(
            _handle, name,
            input, (UIntPtr)input.Length,
            outBuf, (UIntPtr)outCount,
            out _);

        if (hr < 0)
            throw new InvalidOperationException($"pawnio_execute(\"{name}\") failed: 0x{hr:X8}");

        return outBuf;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            pawnio_close(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
