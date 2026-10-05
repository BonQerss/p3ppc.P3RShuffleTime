using p3ppc.P3RShuffleTime.Configuration;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using System.Diagnostics;
using System.Text;

namespace p3ppc.P3RShuffleTime;

internal static class Utils
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
        if (startupScannerController == null || !startupScannerController.TryGetTarget(out var startupScanner) || startupScanner == null)
        {
            LogError("Reloaded SigScan is not ready, so the mod stopped.");
            return false;
        }

        _startupScanner = startupScanner;
        return true;
    }

    internal static void UpdateConfig(Config config) => _config = config;

    internal static void LogDebug(string message)
    {
        if (_config.DebugEnabled)
        {
            _logger.WriteLine($"[P3R Shuffle Time] {message}");
        }
    }

    internal static void LogError(string message, Exception exception) => _logger.WriteLine($"[P3R Shuffle Time] ERROR: {message}: {exception.Message}", System.Drawing.Color.Red);

    internal static void LogError(string message) => _logger.WriteLine($"[P3R Shuffle Time] ERROR: {message}", System.Drawing.Color.Red);

    internal static void SigScan(string pattern, string name, Action<nint> action)
    {
        _startupScanner.AddMainModuleScan(pattern, result =>
        {
            if (!result.Found)
            {
                LogError($"Unable to find {name}; feature disabled.");
                return;
            }

            try
            {
                action(result.Offset + BaseAddress);
            }
            catch (Exception ex)
            {
                LogError($"Failed to initialise {name}", ex);
            }
        });
    }

    public static string PushXmm(int xmmNum) => $"sub rsp, 16\nmovdqu dqword [rsp], xmm{xmmNum}\n";

    public static string PushXmm()
    {
        StringBuilder sb = new();

        for (int i = 0; i < 16; i++)
        {
            sb.Append(PushXmm(i));
        }

        return sb.ToString();
    }

    public static string PopXmm(int xmmNum) => $"movdqu xmm{xmmNum}, dqword [rsp]\nadd rsp, 16\n";

    public static string PopXmm()
    {
        StringBuilder sb = new();

        for (int i = 15; i >= 0; i--)
        {
            sb.Append(PopXmm(i));
        }

        return sb.ToString();
    }

    internal static unsafe nuint GetGlobalAddress(nint ptrAddress)
    {
        return (nuint)(*(int*)ptrAddress + ptrAddress + 4);
    }
}
