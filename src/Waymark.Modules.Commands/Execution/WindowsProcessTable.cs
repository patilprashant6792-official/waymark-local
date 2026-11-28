using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Waymark.Modules.Commands.Execution;

/// <summary>
/// The Windows process list with each process's parent id, from a Toolhelp snapshot: no external tool and no WMI, one call that takes a few
/// milliseconds. Windows has no process tree of its own, only these parent ids, so <see cref="ProcessTree"/> walks them.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsProcessTable
{
    private const uint SnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>Every process as (id, parent id). Empty when the snapshot cannot be taken.</summary>
    public static IReadOnlyList<(int Pid, int Parent)> Read()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle || snapshot == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var result = new List<(int Pid, int Parent)>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return [];
            }

            do
            {
                result.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
            }
            while (Process32Next(snapshot, ref entry));

            return result;
        }
        catch (Win32Exception)
        {
            return [];
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    // PROCESSENTRY32W, field for field.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }
}
