using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomeNet;

internal static class Wlan
{
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, in Guid id, uint op, IntPtr reserved, out uint size, out IntPtr data, IntPtr opcodeType);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr data);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    public static int? Signal(string id)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParse(id, out var guid) ||
            WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return null;
        try
        {
            const uint rssi = 0x10000102;
            if (WlanQueryInterface(handle, in guid, rssi, IntPtr.Zero, out var size, out var data, IntPtr.Zero) != 0) return null;
            try
            {
                if (size < sizeof(int)) return null;
                var value = Marshal.ReadInt32(data);
                return value is >= -120 and < 0 ? value : null;
            }
            finally { WlanFreeMemory(data); }
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }
}

internal static class Child
{
    public static async Task<(int Code, string Output)> Run(string exe, IEnumerable<string> args, int seconds, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new IOException("Could not start child process.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output;
            await errors;
            return (process.ExitCode, stdout);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            throw;
        }
    }
}
