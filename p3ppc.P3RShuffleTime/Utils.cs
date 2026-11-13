using p3ppc.P3RShuffleTime.Configuration;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace p3ppc.P3RShuffleTime;

internal static unsafe class Utils
{
    private static ILogger _logger = null!;
    private static Config _config = null!;
    private static IStartupScanner _startupScanner = null!;

    internal static nint BaseAddress { get; private set; }

    internal static bool Initialise(ILogger logger, Config config, IModLoader modLoader)
    {
        _logger = logger;
        _config = config;

        using var thisProcess = Process.GetCurrentProcess();
        BaseAddress = thisProcess.MainModule!.BaseAddress;

        var startupScannerController = modLoader.GetController<IStartupScanner>();
        if (startupScannerController == null || !startupScannerController.TryGetTarget(out var startupScanner))
        {
            LogError("Unable to get controller for Reloaded SigScan Library.");
            return false;
        }

        _startupScanner = startupScanner;
        return true;
    }

    internal static void UpdateConfig(Config config) => _config = config;

    internal static void Log(string message) => _logger.WriteLine($"[P3R Shuffle Time] {message}");

    internal static void LogDebug(string message)
    {
        if (_config.DebugEnabled)
        {
            _logger.WriteLine($"[P3R Shuffle Time] {message}");
        }
    }

    internal static void LogError(string message, Exception e) => _logger.WriteLine($"[P3R Shuffle Time] {message}: {e.Message}", System.Drawing.Color.Red);

    internal static void LogError(string message) => _logger.WriteLine($"[P3R Shuffle Time] {message}", System.Drawing.Color.Red);

    internal static void SigScan(string pattern, string name, Action<nint> action)
    {
        _startupScanner.AddMainModuleScan(pattern, result =>
        {
            if (!result.Found)
            {
                LogError($"Unable to find {name}, so that part of the mod is off.");
                return;
            }

            try
            {
                action(result.Offset + BaseAddress);
            }
            catch (Exception e)
            {
                LogError($"Failed to set up {name}", e);
            }
        });
    }

    internal static nuint GetGlobalAddress(nint ptrAddress) => (nuint)(*(int*)ptrAddress + ptrAddress + 4);

    /// <summary>Target of the call at <paramref name="site"/>, or 0 if there is no call there</summary>
    internal static nint CallTarget(nint site)
    {
        if (!IsReadable(site, 5) || *(byte*)site != 0xE8)
        {
            return 0;
        }

        return site + 5 + *(int*)(site + 1);
    }

    /// <summary>Follows jump thunks to the function they lead to</summary>
    internal static nint FollowThunks(nint target)
    {
        for (int hop = 0; hop < 4 && target != 0; hop++)
        {
            if (!IsReadable(target, 8))
            {
                return 0;
            }

            byte* p = (byte*)target;
            if (p[0] == 0xE9)
            {
                target += 5 + *(int*)(p + 1);
            }
            else if (p[0] == 0xEB)
            {
                target += 2 + (sbyte)p[1];
            }
            else if (p[0] == 0xFF && p[1] == 0x25)
            {
                nint slot = target + 6 + *(int*)(p + 2);
                target = IsReadable(slot, 8) ? *(nint*)slot : 0;
            }
            else
            {
                break;
            }
        }

        return target;
    }

    /// <summary>True when the whole range is committed readable memory</summary>
    internal static bool IsReadable(nint address, int length)
    {
        nint cursor = address;
        nint end = address + length;
        while (cursor < end)
        {
            if (VirtualQuery(cursor, out MemoryBasicInformation info, (nuint)sizeof(MemoryBasicInformation)) == 0)
            {
                return false;
            }

            const uint Commit = 0x1000, Readable = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80, GuardOrNoAccess = 0x100 | 0x01;
            if (info.State != Commit || (info.Protect & Readable) == 0 || (info.Protect & GuardOrNoAccess) != 0)
            {
                return false;
            }

            cursor = info.BaseAddress + (nint)info.RegionSize;
        }

        return true;
    }

    /// <summary>Writes a patch into game code or data and flushes it from the instruction cache</summary>
    internal static bool SafeWrite(nint address, ReadOnlySpan<byte> bytes)
    {
        if (!VirtualProtect(address, (nuint)bytes.Length, 0x40, out uint oldProtection))
        {
            return false;
        }

        bytes.CopyTo(new Span<byte>((void*)address, bytes.Length));
        VirtualProtect(address, (nuint)bytes.Length, oldProtection, out _);
        FlushInstructionCache(-1, address, (nuint)bytes.Length);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint address, out MemoryBasicInformation info, nuint length);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint oldProtection);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
}
