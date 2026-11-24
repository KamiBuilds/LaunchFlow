using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NativeUiDriver
{
    public sealed class BuildOutcome
    {
        public string Category = "Harness";
        public string Reason = "OperationFailed";
        public int Remaining = -1;
        public int ExitCode = -1;
    }

    // This scope owns only the original build and its CreateProcess descendants.
    // Never used for installed application cleanup. No breakaway permission is set.
    public static class OwnedBuild
    {
        public static BuildOutcome Run(string executable, string arguments, string directory, string log, int timeout)
        {
            return RunCore(executable, arguments, directory, log, timeout, -1, null);
        }

        // Credential-free native preflight only; production always uses Run.
        public static BuildOutcome RunFaultProbe(string executable, string arguments, string directory, string log, int timeout, int faultAfter)
        {
            return RunCore(executable, arguments, directory, log, timeout, faultAfter, null);
        }

        // The native fixture receives the exact suspended child handle before verification/resume.
        public static BuildOutcome RunCreationProbe(string executable, string arguments, string directory, string log, int timeout, Action<IntPtr, uint> suspendedCreated)
        {
            if (suspendedCreated == null) throw new ArgumentNullException("suspendedCreated");
            return RunCore(executable, arguments, directory, log, timeout, -1, suspendedCreated);
        }

        private static BuildOutcome RunCore(string executable, string arguments, string directory, string log, int timeout, int faultAfter, Action<IntPtr, uint> suspendedCreated)
        {
            if (timeout < 1 || executable.IndexOf('"') >= 0) throw new ArgumentException("PathRejected");
            if (!LayoutValid()) throw new InvalidOperationException("HostRejected");
            BuildOutcome result = new BuildOutcome();
            IntPtr job = IntPtr.Zero, input = IntPtr.Zero, output = IntPtr.Zero;
            IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, jobs = IntPtr.Zero, environment = IntPtr.Zero;
            PROCESS_INFORMATION process = new PROCESS_INFORMATION();
            bool assigned = false, initialized = false;
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                Check(job != IntPtr.Zero);
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
                Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))));
                SECURITY_ATTRIBUTES security = new SECURITY_ATTRIBUTES();
                security.Length = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)); security.InheritHandle = true;
                input = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0x80, IntPtr.Zero);
                output = CreateFile(log, 0x40000000, 1, ref security, 1, 0x80, IntPtr.Zero);
                Check(Valid(input) && Valid(output));
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
                Check(size.ToInt64() > 0);
                attributes = Marshal.AllocHGlobal(size);
                Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref size)); initialized = true;
                handles = Marshal.AllocHGlobal(IntPtr.Size * 2);
                Marshal.WriteIntPtr(handles, 0, input); Marshal.WriteIntPtr(handles, IntPtr.Size, output);
                // Only NUL and the owned log are inherited, never the job or arbitrary host handles.
                Check(UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), handles, new IntPtr(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero));
                // JOB_LIST assigns at creation without inheriting the job handle. Unsupported hosts fail closed.
                jobs = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(jobs, job);
                Check(UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x2000D), jobs, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
                STARTUPINFOEX startup = new STARTUPINFOEX();
                startup.StartupInfo.Size = Marshal.SizeOf(typeof(STARTUPINFOEX));
                startup.StartupInfo.Flags = 0x100;
                startup.StartupInfo.Input = input; startup.StartupInfo.Output = output; startup.StartupInfo.Error = output;
                startup.Attributes = attributes;
                environment = Marshal.StringToHGlobalUni(BuildEnvironment());
                StringBuilder command = new StringBuilder("\"" + executable + "\" " + arguments);
                // Creation-time job assignment contains abrupt owner death even before managed registration.
                Check(CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                    0x4 | 0x400 | 0x80000 | 0x8000000, environment, directory, ref startup, out process));
                assigned = true;
                if (suspendedCreated != null) suspendedCreated(process.Process, process.ProcessId);
                if (faultAfter == -2) throw new InvalidOperationException("OperationFailed");
                bool inJob;
                Check(IsProcessInJob(process.Process, job, out inJob));
                Check(inJob);
                Check(ResumeThread(process.Thread) != UInt32.MaxValue);
                while (clock.ElapsedMilliseconds < timeout)
                {
                    if (faultAfter >= 0 && clock.ElapsedMilliseconds >= faultAfter) throw new InvalidOperationException("OperationFailed");
                    uint status = WaitForSingleObject(process.Process, 25);
                    Check(status == 0 || status == 258);
                    // Root exit alone is insufficient: compiler/build descendants must exit too.
                    if (status == 0 && Active(job) == 0)
                    {
                        uint exit; Check(GetExitCodeProcess(process.Process, out exit));
                        result.ExitCode = unchecked((int)exit);
                        result.Category = exit == 0 ? "None" : "Harness";
                        result.Reason = exit == 0 ? "None" : "OperationFailed";
                        break;
                    }
                    Thread.Sleep(25);
                }
                if (result.ExitCode == -1 && clock.ElapsedMilliseconds >= timeout)
                {
                    result.Category = "Timeout"; result.Reason = "Deadline";
                }
            }
            catch
            {
                result.Category = "Harness"; result.Reason = "OperationFailed";
            }
            finally
            {
                // Defensive direct-handle cleanup if creation failed with a returned handle.
                if (process.Process != IntPtr.Zero && !assigned)
                {
                    try
                    {
                        if (WaitForSingleObject(process.Process, 0) != 0) TerminateProcess(process.Process, 1);
                        result.Remaining = WaitForSingleObject(process.Process, 5000) == 0 ? 0 : -1;
                    }
                    catch { result.Remaining = -1; }
                }
                else if (assigned)
                {
                    try
                    {
                        // Terminate before querying on every unsuccessful path, even if accounting itself fails.
                        if (result.Category != "None")
                        {
                            TerminateJobObject(job, 1);
                            // Membership/query failure must not turn an empty job into proof of child exit.
                            if (WaitForSingleObject(process.Process, 0) != 0) TerminateProcess(process.Process, 1);
                        }
                        int active = Active(job);
                        uint rootStatus = 258;
                        Stopwatch cleanup = Stopwatch.StartNew();
                        do
                        {
                            active = Active(job);
                            rootStatus = WaitForSingleObject(process.Process, 0);
                            Check(rootStatus == 0 || rootStatus == 258);
                            if (active == 0 && rootStatus == 0) break;
                            Thread.Sleep(25);
                        } while (cleanup.ElapsedMilliseconds < 10000);
                        result.Remaining = rootStatus == 0 ? active : -1;
                    }
                    catch { result.Remaining = -1; }
                }
                else result.Remaining = 0; // No process was created.
                if (initialized) DeleteProcThreadAttributeList(attributes);
                // Last-handle close is a second containment boundary, not proof of observed exit.
                if (job != IntPtr.Zero) CloseHandle(job);
                if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
                if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
                if (Valid(input)) CloseHandle(input);
                if (Valid(output)) CloseHandle(output);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
                if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            }
            return result;
        }

        public static bool LayoutValid()
        {
            return IntPtr.Size == 8 && Marshal.SizeOf(typeof(STARTUPINFOEX)) == 112
                && Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)) == 144
                && Marshal.SizeOf(typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)) == 48
                && Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)) == 24;
        }

        private static string BuildEnvironment()
        {
            List<string> entries = new List<string>();
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = (string)entry.Key;
                if (!key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) entries.Add(key + "=" + (string)entry.Value);
            }
            entries.Sort(StringComparer.OrdinalIgnoreCase);
            return String.Join("\0", entries.ToArray()) + "\0\0";
        }
        private static bool Valid(IntPtr handle) { return handle != IntPtr.Zero && handle != new IntPtr(-1); }
        private static void Check(bool success) { if (!success) throw new InvalidOperationException("OperationFailed"); }
        private static int Active(IntPtr job)
        {
            JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting;
            Check(QueryInformationJobObject(job, 1, out accounting, (uint)Marshal.SizeOf(typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)), IntPtr.Zero));
            return checked((int)accounting.ActiveProcesses);
        }
        [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
        [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
        {
            public int Size; public string Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public ushort ShowWindow, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long UserTimeLimit, JobTimeLimit; public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
            public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, uint length, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(IntPtr job, uint exit);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFile(string name, uint access, uint share, ref SECURITY_ATTRIBUTES security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exit);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint exit);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    }
}
