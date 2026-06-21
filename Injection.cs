using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PerformanceOverlay;

/// <summary>
/// Drives the optional hook DLL: injects it into the foreground game (no-anti-cheat only)
/// and reads frame-present timestamps back from its shared-memory section.
///
/// Supports both 64-bit games (inject the x64 DLL directly) and 32-bit games (shell out to
/// a bundled 32-bit injector helper). The native helpers are embedded in this exe and
/// extracted to disk on first use. Everything fails soft: on any error we report no data
/// and the app falls back to ETW.
/// </summary>
public sealed class InjectionManager : IDisposable
{
    private readonly int _ownPid = Process.GetCurrentProcess().Id;
    private readonly Dictionary<int, bool> _injected = new(); // pid -> is64bit
    private readonly HashSet<int> _failed = new();

    private string? _dll64, _dll32, _inject32;

    private int _readerPid;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private uint _cap;
    private long _lastCount;

    public string? LastError { get; private set; }
    public uint LastApiMask { get; private set; }

    public InjectionManager()
    {
        _dll64 = ExtractAsset("PerfOverlayHook.dll");
        _dll32 = ExtractAsset("PerfOverlayHook32.dll");
        _inject32 = ExtractAsset("PerfOverlayInject32.exe");
    }

    public bool DllAvailable => _dll64 != null && File.Exists(_dll64);

    /// <summary>Extract an embedded native asset to a writable folder; returns its path.</summary>
    private static string? ExtractAsset(string name)
    {
        try
        {
            // prefer a loose copy next to the exe (dev / xcopy), else extract the embedded one
            var local = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(local)) return local;

            var asm = Assembly.GetExecutingAssembly();
            using var rs = asm.GetManifestResourceStream(name);
            if (rs == null) return File.Exists(local) ? local : null;

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                   "PerformanceOverlay", "native");
            Directory.CreateDirectory(dir);
            var outp = Path.Combine(dir, name);
            if (!File.Exists(outp) || new FileInfo(outp).Length != rs.Length)
            {
                using var fs = File.Create(outp);
                rs.CopyTo(fs);
            }
            return outp;
        }
        catch { return null; }
    }

    /// <summary>PID of the foreground window's process, or 0 if it's us / none.</summary>
    public int ForegroundGamePid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out int pid);
        return pid == _ownPid ? 0 : pid;
    }

    /// <summary>Inject the hook into a process if not already done. Returns true if (now) injected.</summary>
    public bool EnsureInjected(int pid)
    {
        if (pid <= 0) return false;
        if (_injected.ContainsKey(pid)) return true;
        if (_failed.Contains(pid)) return false;

        try
        {
            if (!IsProcessAlive(pid)) return false;
            bool is64 = IsProcess64Bit(pid, out bool known);
            if (!known) { LastError = "could not determine game architecture (try admin)"; _failed.Add(pid); return false; }

            bool ok = is64 ? Inject64(pid) : Inject32(pid);
            if (!ok) { _failed.Add(pid); return false; }
            _injected[pid] = is64;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _failed.Add(pid);
            return false;
        }
    }

    /// <summary>Detect when a process we injected has exited (safety: caller disables injection).</summary>
    public bool CheckInjectedClosed()
    {
        if (_injected.Count == 0) return false;
        var dead = _injected.Keys.Where(pid => !IsProcessAlive(pid)).ToList();
        foreach (var pid in dead) { _injected.Remove(pid); _failed.Remove(pid); }
        return dead.Count > 0;
    }

    /// <summary>Forget all injection state (called when the toggle goes off).</summary>
    /// <summary>
    /// Turning injection off (manually or auto on game-close). We deliberately do NOT
    /// FreeLibrary the hook out of a running game — its present hooks are live function
    /// pointers into the DLL, so unloading mid-game would crash the game. Instead we just
    /// stop reading: the DLL stays dormant (only timestamping into its own shared memory)
    /// and unloads naturally when the game process exits. Safe to call any time.
    /// </summary>
    public void Reset()
    {
        _injected.Clear();
        _failed.Clear();
        CloseReader();
    }

    // ----- shared-memory reading -----

    public IReadOnlyList<double> Poll(int pid)
    {
        if (pid <= 0) return Array.Empty<double>();
        if (!OpenReader(pid)) return Array.Empty<double>();
        try
        {
            long count = _view!.ReadInt64(0);
            long qpcFreq = _view.ReadInt64(8);
            LastApiMask = _view.ReadUInt32(16);
            if (qpcFreq <= 0 || _cap == 0) return Array.Empty<double>();
            if (count <= _lastCount) { _lastCount = count; return Array.Empty<double>(); }

            long from = Math.Max(_lastCount, count - _cap);
            var outList = new List<double>((int)Math.Min(count - from, _cap));
            for (long i = from; i < count; i++)
            {
                long off = 24 + (i % _cap) * 8;
                long qpc = _view.ReadInt64(off);
                outList.Add(qpc * 1000.0 / qpcFreq);
            }
            _lastCount = count;
            return outList;
        }
        catch { CloseReader(); return Array.Empty<double>(); }
    }

    // Offset of the fpsCap field: after header (24 bytes) + ring (8 * RING_CAP).
    private const long FpsCapOffset = 24 + 8L * 2048;

    /// <summary>Write the FPS limit into the injected game's shared memory (0 = uncapped).</summary>
    public void SetFpsCap(int pid, int cap)
    {
        if (pid <= 0 || !OpenReader(pid)) return;
        try { _view!.Write(FpsCapOffset, cap); } catch { CloseReader(); }
    }

    private bool OpenReader(int pid)
    {
        if (_readerPid == pid && _view != null) return true;
        CloseReader();
        try
        {
            // ReadWrite so we can also push the FPS cap back to the hook.
            _mmf = MemoryMappedFile.OpenExisting($"Local\\PerfOverlayHook_{pid}", MemoryMappedFileRights.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            _cap = _view.ReadUInt32(20);
            _lastCount = 0;
            _readerPid = pid;
            return _cap > 0;
        }
        catch { CloseReader(); return false; }
    }

    private void CloseReader()
    {
        _view?.Dispose(); _view = null;
        _mmf?.Dispose(); _mmf = null;
        _readerPid = 0; _cap = 0; _lastCount = 0;
    }

    // ----- injection: 64-bit (direct) -----

    private bool Inject64(int pid)
    {
        if (_dll64 == null || !File.Exists(_dll64)) { LastError = "x64 hook DLL missing"; return false; }
        IntPtr proc = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
        if (proc == IntPtr.Zero) { LastError = "OpenProcess failed (try admin)"; return false; }
        try
        {
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(_dll64 + "\0");
            IntPtr remote = VirtualAllocEx(proc, IntPtr.Zero, (uint)bytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero) { LastError = "VirtualAllocEx failed"; return false; }
            try
            {
                if (!WriteProcessMemory(proc, remote, bytes, (uint)bytes.Length, out _)) { LastError = "WriteProcessMemory failed"; return false; }
                IntPtr loadLib = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
                if (loadLib == IntPtr.Zero) { LastError = "LoadLibraryW not found"; return false; }
                IntPtr thread = CreateRemoteThread(proc, IntPtr.Zero, 0, loadLib, remote, 0, out _);
                if (thread == IntPtr.Zero) { LastError = "CreateRemoteThread failed"; return false; }
                WaitForSingleObject(thread, 5000);
                CloseHandle(thread);
                return true;
            }
            finally { VirtualFreeEx(proc, remote, 0, MEM_RELEASE); }
        }
        finally { CloseHandle(proc); }
    }

    // ----- injection: 32-bit (via helper exe) -----

    private bool Inject32(int pid)
    {
        if (_inject32 == null || !File.Exists(_inject32) || _dll32 == null || !File.Exists(_dll32))
        { LastError = "32-bit injection helpers missing"; return false; }
        try
        {
            var psi = new ProcessStartInfo(_inject32) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(pid.ToString());
            psi.ArgumentList.Add(_dll32);
            using var p = Process.Start(psi);
            if (p == null) { LastError = "could not start 32-bit injector"; return false; }
            p.WaitForExit(8000);
            if (!p.HasExited) { LastError = "32-bit injector timed out"; return false; }
            if (p.ExitCode != 0) { LastError = $"32-bit injector error code {p.ExitCode}"; return false; }
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    // ----- helpers -----

    private static bool IsProcess64Bit(int pid, out bool known)
    {
        known = false;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return true;
        try
        {
            if (!IsWow64Process(h, out bool wow64)) return true;
            known = true;
            return !wow64; // on a 64-bit OS, not-WOW64 == native 64-bit process
        }
        finally { CloseHandle(h); }
    }

    private static bool IsProcessAlive(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return GetExitCodeProcess(h, out uint code) && code == STILL_ACTIVE; }
        finally { CloseHandle(h); }
    }

    public void Dispose() => CloseReader();

    // ----- P/Invoke -----
    private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint STILL_ACTIVE = 259;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process(IntPtr proc, out bool wow64);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr proc, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, uint size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, uint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr proc, IntPtr addr, byte[] buffer, uint size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr mod, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateRemoteThread(IntPtr proc, IntPtr attr, uint stack, IntPtr start, IntPtr param, uint flags, out IntPtr tid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
