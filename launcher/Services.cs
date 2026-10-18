using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteveFish.Launcher;

/// <summary>
/// A child process (server or bridge) with its output streamed line by line. All children join a Windows
/// job object that kills them if the launcher itself dies, so nothing is left running in the background.
/// </summary>
internal sealed class ManagedProcess : IDisposable
{
    private readonly string _name;
    private Process? _p;

    public event Action<string>? Line;
    public event Action<int>? Exited;

    public ManagedProcess(string name) => _name = name;

    public bool Running => _p is { HasExited: false };

    public void Start(string exe, string args, string workDir, IDictionary<string, string>? env = null)
    {
        if (Running) return;
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Line?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Line?.Invoke(e.Data); };
        p.Exited += (_, _) => Exited?.Invoke(SafeExitCode(p));
        p.Start();
        Job.Add(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _p = p;
        Line?.Invoke($"[launcher] started {_name} (pid {p.Id})");
    }

    public void Send(string line)
    {
        if (Running) _p!.StandardInput.WriteLine(line);
    }

    /// <summary>Asks nicely (stdin command) then kills after the timeout.</summary>
    public async Task StopAsync(string? stopCommand, TimeSpan timeout)
    {
        if (!Running) return;
        var p = _p!;
        try
        {
            if (stopCommand != null) { p.StandardInput.WriteLine(stopCommand); p.StandardInput.Flush(); }
            else p.StandardInput.Close(); // node: stdin close isn't a signal, so fall through to the timeout
        }
        catch (Exception) { /* already gone */ }
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            Line?.Invoke($"[launcher] {_name} didn't stop in {timeout.TotalSeconds:F0}s; ending it");
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
        }
    }

    public void Kill()
    {
        try { if (Running) _p!.Kill(entireProcessTree: true); } catch (Exception) { }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch (Exception) { return -1; }
    }

    public void Dispose()
    {
        Kill();
        _p?.Dispose();
    }

    // ---------- job object: children die with the launcher ----------

    private static class Job
    {
        private static readonly IntPtr Handle = Create();

        private static IntPtr Create()
        {
            var h = CreateJobObject(IntPtr.Zero, null);
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
            var len = Marshal.SizeOf(info);
            var ptr = Marshal.AllocHGlobal(len);
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(h, 9 /* ExtendedLimitInformation */, ptr, (uint)len);
            Marshal.FreeHGlobal(ptr);
            return h;
        }

        public static void Add(Process p)
        {
            try { AssignProcessToJobObject(Handle, p.Handle); } catch (Exception) { }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr a, string? name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoType, IntPtr info, uint len);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
    }
}
