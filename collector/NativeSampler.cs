using System.Runtime.InteropServices;
using Telltale.Collector.Interop;

namespace Telltale.Collector;

public sealed record ProcessSnapshot(
    int Pid,
    long CreateTimeTicks,
    string Name,
    long KernelTime,
    long UserTime,
    long WorkingSetBytes,
    long PrivateBytes,
    long IoReadBytes,
    long IoWriteBytes,
    long IoOtherBytes,
    int ThreadCount,
    int HandleCount);

public interface IProcessSampler
{
    List<ProcessSnapshot> Sample();
    bool IsNative { get; }
}

public sealed class NativeSampler : IProcessSampler
{
    private static bool _validated;
    private static bool _validationPassed;

    public bool IsNative => true;

    public static bool TryValidate(ILogger logger)
    {
        if (_validated) return _validationPassed;
        _validated = true;

        if (!NtDefs.ValidateLayout())
        {
            logger.LogWarning("NtQuerySystemInformation struct layout validation failed. Falling back to managed sampler.");
            _validationPassed = false;
            return false;
        }

        try
        {
            var sampler = new NativeSampler();
            var results = sampler.Sample();
            if (results.Count == 0)
            {
                logger.LogWarning("Native sampler returned no processes. Falling back to managed sampler.");
                _validationPassed = false;
                return false;
            }

            using var self = System.Diagnostics.Process.GetCurrentProcess();
            bool foundSelf = results.Any(p => p.Pid == self.Id);
            if (!foundSelf)
            {
                logger.LogWarning("Native sampler did not find the current process. Falling back to managed sampler.");
                _validationPassed = false;
                return false;
            }

            logger.LogInformation("Native sampler validated. Found {Count} processes.", results.Count);
            _validationPassed = true;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Native sampler validation failed. Falling back to managed sampler.");
            _validationPassed = false;
            return false;
        }
    }

    public List<ProcessSnapshot> Sample()
    {
        int bufferSize = 1024 * 1024;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            while (true)
            {
                buffer = Marshal.AllocHGlobal(bufferSize);
                uint status = NtDefs.NtQuerySystemInformation(
                    NtDefs.SystemProcessInformation, buffer, bufferSize, out int returnLength);

                if (status == NtDefs.STATUS_INFO_LENGTH_MISMATCH)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                    bufferSize = returnLength + 65536;
                    continue;
                }

                if (status != NtDefs.STATUS_SUCCESS)
                    throw new InvalidOperationException($"NtQuerySystemInformation failed with status 0x{status:X8}");

                break;
            }

            return ParseProcesses(buffer);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<ProcessSnapshot> ParseProcesses(IntPtr buffer)
    {
        var results = new List<ProcessSnapshot>(256);
        IntPtr current = buffer;

        while (true)
        {
            var info = Marshal.PtrToStructure<NtDefs.SYSTEM_PROCESS_INFORMATION>(current);
            int pid = (int)(long)info.UniqueProcessId;

            string name = pid == 0 ? "Idle" :
                info.ImageName.Buffer != IntPtr.Zero && info.ImageName.Length > 0
                    ? Marshal.PtrToStringUni(info.ImageName.Buffer, info.ImageName.Length / 2) ?? $"PID {pid}"
                    : $"PID {pid}";

            results.Add(new ProcessSnapshot(
                Pid: pid,
                CreateTimeTicks: info.CreateTime,
                Name: name,
                KernelTime: info.KernelTime,
                UserTime: info.UserTime,
                WorkingSetBytes: (long)info.WorkingSetSize,
                PrivateBytes: (long)info.PrivatePageCount,
                IoReadBytes: info.ReadTransferCount,
                IoWriteBytes: info.WriteTransferCount,
                IoOtherBytes: info.OtherTransferCount,
                ThreadCount: (int)info.NumberOfThreads,
                HandleCount: (int)info.HandleCount));

            if (info.NextEntryOffset == 0) break;
            current = IntPtr.Add(current, (int)info.NextEntryOffset);
        }

        return results;
    }
}
