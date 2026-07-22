using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <summary>
/// Stateless Windows process-level adapter for the mode-toggle priority-boost
/// WORKAROUND. Owns Toolhelp32 enumeration, direct-parent filtering, bounded PEB
/// command-line reading, exact <c>--type=renderer</c> token matching, and the
/// <c>OpenProcess</c>/<c>GetPriorityClass</c>/<c>SetPriorityClass</c> priority
/// calls. Every native handle is a SafeHandle owner.
///
/// <para>Uses no WMI/CIM, no management-object or shell process query, no
/// polling, and no background watcher. A missing API, access denial, malformed
/// PEB, architecture mismatch, or process race degrades to the shipped unbumped
/// reveal — every public method fails closed and never throws into the reveal
/// pipeline.</para>
///
/// <para>Reachable only through <see cref="ApplicateModeToggleRendererPriorityScope"/>,
/// the sole priority writer-owner.</para>
/// </summary>
internal sealed class WindowsWebViewRendererPriorityNative : IApplicateRendererPriorityNative
{
    // Bound on the untrusted, length-capped command line we read out of the
    // target PEB. Chromium renderer command lines are ~1-2 KB and carry the
    // --type=renderer token in the first few hundred characters, so a truncated
    // read never loses the discriminator.
    internal const int MaxCommandLineChars = 16384;

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint SYNCHRONIZE = 0x00100000;

    private const uint LeaseAccess =
        PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ | PROCESS_SET_INFORMATION | SYNCHRONIZE;

    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    private const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;

    private const uint WAIT_OBJECT_0 = 0x00000000;

    // 64-bit PEB / RTL_USER_PROCESS_PARAMETERS field offsets.
    private const int PebProcessParametersOffset = 0x20;
    private const int ProcessParametersCommandLineOffset = 0x70;

    private const string RendererTypeToken = "--type=renderer";

    public RendererPriorityDiscovery Discover(int browserProcessId)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8 || browserProcessId <= 0)
        {
            // 32-bit hosts use a different PEB layout; this adapter targets the
            // x64 production runtime only. Fail closed to the shipped reveal.
            return RendererPriorityDiscovery.Unsupported;
        }

        var openedHandles = new List<(int Pid, SafeProcessHandle Handle)>();
        var leases = new List<RendererPriorityLease>();
        var transferred = false;
        try
        {
            using var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot.IsInvalid)
            {
                return RendererPriorityDiscovery.Fail("snapshot", Marshal.GetLastWin32Error());
            }

            var directChildPids = CollectDirectChildPids(snapshot, browserProcessId);
            if (directChildPids.Count == 0)
            {
                return RendererPriorityDiscovery.NoMatch;
            }

            // Open each direct child ONCE (inspection + mutation share the handle),
            // read its command line, and classify. Any unreadable direct child
            // makes the selected set incomplete -> abort and unwind.
            var candidates = new List<RendererProcessCandidate>(directChildPids.Count);
            foreach (var pid in directChildPids)
            {
                var handle = OpenProcess(LeaseAccess, false, (uint)pid);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return RendererPriorityDiscovery.Fail("open-inspect", Marshal.GetLastWin32Error());
                }

                openedHandles.Add((pid, handle));
                if (!TryReadCommandLine(handle, out var commandLine))
                {
                    return RendererPriorityDiscovery.Fail("read-command-line", Marshal.GetLastWin32Error());
                }

                candidates.Add(new RendererProcessCandidate(pid, browserProcessId, commandLine));
            }

            var selectedPids = SelectRendererChildren(browserProcessId, candidates);
            if (selectedPids.Count == 0)
            {
                return RendererPriorityDiscovery.NoMatch;
            }

            var selectedSet = new HashSet<int>(selectedPids);
            foreach (var (pid, handle) in openedHandles)
            {
                if (!selectedSet.Contains(pid))
                {
                    continue;
                }

                var priorityClass = GetPriorityClass(handle);
                if (priorityClass == 0)
                {
                    return RendererPriorityDiscovery.Fail("get-priority", Marshal.GetLastWin32Error());
                }

                leases.Add(new RendererPriorityLease(pid, (int)priorityClass, handle));
            }

            transferred = true;
            return RendererPriorityDiscovery.Success(leases);
        }
        catch (Exception ex)
        {
            return RendererPriorityDiscovery.Fail("discover:" + ex.GetType().Name, 0);
        }
        finally
        {
            // On any non-success return, close EVERY opened handle. On success,
            // close only the non-selected inspection handles; selected mutation
            // handles were transferred into the leases and are owned by the scope.
            var retained = new HashSet<SafeProcessHandle>();
            if (transferred)
            {
                foreach (var lease in leases)
                {
                    if (lease.Handle is SafeProcessHandle retainedHandle)
                    {
                        retained.Add(retainedHandle);
                    }
                }
            }

            foreach (var (_, handle) in openedHandles)
            {
                if (!retained.Contains(handle))
                {
                    handle.Dispose();
                }
            }
        }
    }

    public bool TryApplyAboveNormal(RendererPriorityLease lease)
    {
        if (lease.Handle is not SafeProcessHandle handle || handle.IsInvalid || handle.IsClosed)
        {
            return false;
        }

        try
        {
            if (!SetPriorityClass(handle, ABOVE_NORMAL_PRIORITY_CLASS))
            {
                return false;
            }

            // Verify the write took: read back the class on the same handle.
            return GetPriorityClass(handle) == ABOVE_NORMAL_PRIORITY_CLASS;
        }
        catch
        {
            return false;
        }
    }

    public RendererPriorityRestoreResult RestoreNormal(RendererPriorityLease lease)
    {
        if (lease.Handle is not SafeProcessHandle handle || handle.IsInvalid || handle.IsClosed)
        {
            // No live handle to restore through: the safety invariant (not left
            // AboveNormal) cannot be violated via a handle we cannot reach.
            return RendererPriorityRestoreResult.Exited;
        }

        try
        {
            if (IsProcessExited(handle))
            {
                return RendererPriorityRestoreResult.Exited;
            }

            if (!SetPriorityClass(handle, NORMAL_PRIORITY_CLASS))
            {
                // Re-check liveness: a process that exited between the check and
                // the set is terminally safe, not a restore failure.
                return IsProcessExited(handle)
                    ? RendererPriorityRestoreResult.Exited
                    : RendererPriorityRestoreResult.Fail("set-normal", Marshal.GetLastWin32Error());
            }

            // The load-bearing safety check is "no renderer left AboveNormal".
            // A readback of Normal is success; a readback BELOW Normal (Chromium
            // re-demoted the still-hidden outgoing renderer on a later visibility
            // event) also satisfies the invariant. Only a still-AboveNormal
            // readback is a genuine restore failure.
            var readback = GetPriorityClass(handle);
            if (readback == ABOVE_NORMAL_PRIORITY_CLASS)
            {
                return RendererPriorityRestoreResult.Fail("verify-normal", 0);
            }

            return RendererPriorityRestoreResult.Restored;
        }
        catch (Exception ex)
        {
            return RendererPriorityRestoreResult.Fail("restore:" + ex.GetType().Name, 0);
        }
    }

    public void CloseLease(RendererPriorityLease lease)
    {
        if (lease.Handle is SafeProcessHandle handle && !handle.IsClosed)
        {
            handle.Dispose();
        }
    }

    /// <summary>Pure classification: from a set of direct-child candidates, select
    /// those whose command line carries an exact <c>--type=renderer</c> argument
    /// token. Also re-applies the direct-parent filter so the same predicate the
    /// production <see cref="Discover"/> path uses is unit-testable in isolation.</summary>
    internal static IReadOnlyList<int> SelectRendererChildren(
        int browserProcessId,
        IEnumerable<RendererProcessCandidate> candidates)
    {
        var selected = new List<int>();
        foreach (var candidate in candidates)
        {
            if (candidate.ProcessId <= 0 || candidate.ParentProcessId != browserProcessId)
            {
                continue;
            }

            if (HasExactRendererTypeToken(candidate.CommandLine))
            {
                selected.Add(candidate.ProcessId);
            }
        }

        return selected;
    }

    /// <summary>Exact-token test for <c>--type=renderer</c>. Command-line contents
    /// are untrusted: length-bounded, parsed as data (never executed), quote-aware,
    /// and rejected when oversized or malformed.</summary>
    internal static bool HasExactRendererTypeToken(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine) || commandLine.Length > MaxCommandLineChars)
        {
            return false;
        }

        var inQuotes = false;
        var start = -1;
        for (var i = 0; i <= commandLine.Length; i++)
        {
            var c = i < commandLine.Length ? commandLine[i] : ' ';
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (start >= 0)
                {
                    if (TokenEqualsRenderer(commandLine, start, i))
                    {
                        return true;
                    }

                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        return false;
    }

    private static bool TokenEqualsRenderer(string source, int start, int end)
    {
        // Compare [start,end) against RendererTypeToken, skipping any embedded
        // quote characters (the exe path is quoted; argument tokens are not, but
        // a defensive skip keeps the comparison exact).
        var tokenIndex = 0;
        for (var i = start; i < end; i++)
        {
            var c = source[i];
            if (c == '"')
            {
                continue;
            }

            if (tokenIndex >= RendererTypeToken.Length || c != RendererTypeToken[tokenIndex])
            {
                return false;
            }

            tokenIndex++;
        }

        return tokenIndex == RendererTypeToken.Length;
    }

    private static List<int> CollectDirectChildPids(SafeSnapshotHandle snapshot, int browserProcessId)
    {
        var pids = new List<int>();
        var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
        if (!Process32First(snapshot, ref entry))
        {
            return pids;
        }

        do
        {
            if (entry.th32ParentProcessID == (uint)browserProcessId
                && entry.th32ProcessID != 0
                && entry.th32ProcessID != (uint)browserProcessId)
            {
                pids.Add((int)entry.th32ProcessID);
            }
        }
        while (Process32Next(snapshot, ref entry));

        return pids;
    }

    private static bool TryReadCommandLine(SafeProcessHandle handle, out string commandLine)
    {
        commandLine = string.Empty;
        var pbi = default(PROCESS_BASIC_INFORMATION);
        var status = NtQueryInformationProcess(
            handle,
            infoClass: 0,
            ref pbi,
            Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
            out _);
        if (status < 0 || pbi.PebBaseAddress == IntPtr.Zero)
        {
            return false;
        }

        if (!TryReadPointer(handle, pbi.PebBaseAddress + PebProcessParametersOffset, out var processParameters)
            || processParameters == IntPtr.Zero)
        {
            return false;
        }

        // UNICODE_STRING at ProcessParameters+0x70 (64-bit): Length(2), MaxLength(2),
        // pad(4), Buffer(8).
        if (!TryReadBytes(handle, processParameters + ProcessParametersCommandLineOffset, 16, out var unicodeString))
        {
            return false;
        }

        int lengthBytes = BitConverter.ToUInt16(unicodeString, 0);
        var bufferAddress = new IntPtr(BitConverter.ToInt64(unicodeString, 8));
        if (lengthBytes == 0 || bufferAddress == IntPtr.Zero)
        {
            // A live process with an empty command line is validly readable.
            return true;
        }

        if (lengthBytes > MaxCommandLineChars * 2)
        {
            lengthBytes = MaxCommandLineChars * 2;
        }

        if ((lengthBytes & 1) != 0)
        {
            lengthBytes--;
        }

        if (!TryReadBytes(handle, bufferAddress, lengthBytes, out var buffer))
        {
            return false;
        }

        commandLine = System.Text.Encoding.Unicode.GetString(buffer, 0, lengthBytes);
        return true;
    }

    private static bool TryReadPointer(SafeProcessHandle handle, IntPtr address, out IntPtr value)
    {
        value = IntPtr.Zero;
        if (!TryReadBytes(handle, address, IntPtr.Size, out var buffer))
        {
            return false;
        }

        value = new IntPtr(BitConverter.ToInt64(buffer, 0));
        return true;
    }

    private static bool TryReadBytes(SafeProcessHandle handle, IntPtr address, int count, out byte[] buffer)
    {
        buffer = new byte[count];
        if (count <= 0)
        {
            return false;
        }

        var ok = ReadProcessMemory(handle, address, buffer, (IntPtr)count, out var read);
        return ok && read == (IntPtr)count;
    }

    private static bool IsProcessExited(SafeProcessHandle handle)
        => WaitForSingleObject(handle, 0) == WAIT_OBJECT_0;

    internal readonly record struct RendererProcessCandidate(int ProcessId, int ParentProcessId, string? CommandLine);

    private sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeSnapshotHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeSnapshotHandle snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeSnapshotHandle snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        SafeProcessHandle process,
        IntPtr baseAddress,
        byte[] buffer,
        IntPtr size,
        out IntPtr numberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(SafeProcessHandle process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle process,
        int infoClass,
        ref PROCESS_BASIC_INFORMATION information,
        int informationLength,
        out int returnLength);
}
