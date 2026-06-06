using Microsoft.Win32;

namespace VComTunnel.Core;

public interface IComPortInventory
{
    IReadOnlyList<string> GetRegisteredPortNames();
    IReadOnlyList<Com0comPairInfo> GetCom0comPairs();
}

public sealed class WindowsComPortInventory : IComPortInventory
{
    private const string SerialCommKey = @"HARDWARE\DEVICEMAP\SERIALCOMM";

    public IReadOnlyList<string> GetRegisteredPortNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
#pragma warning disable CA1416
            using var key = Registry.LocalMachine.OpenSubKey(SerialCommKey);
            if (key is null)
            {
                return [];
            }

            return key.GetValueNames()
                .Select(name => key.GetValue(name)?.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
#pragma warning restore CA1416
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<Com0comPairInfo> GetCom0comPairs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
#pragma warning disable CA1416
            using var key = Registry.LocalMachine.OpenSubKey(SerialCommKey);
            if (key is null)
            {
                return [];
            }

            var pairs = new Dictionary<int, PairBuilder>();
            foreach (var valueName in key.GetValueNames())
            {
                if (!TryParseCom0comDevice(valueName, out var side, out var pairNumber))
                {
                    continue;
                }

                var portName = key.GetValue(valueName)?.ToString();
                if (string.IsNullOrWhiteSpace(portName))
                {
                    continue;
                }

                if (!pairs.TryGetValue(pairNumber, out var pair))
                {
                    pair = new PairBuilder(pairNumber);
                    pairs[pairNumber] = pair;
                }

                if (side == '1')
                {
                    pair.PortA = portName;
                    pair.DeviceA = valueName;
                }
                else
                {
                    pair.PortB = portName;
                    pair.DeviceB = valueName;
                }
            }
#pragma warning restore CA1416

            return pairs.Values
                .OrderBy(pair => pair.PairNumber)
                .Select(pair => new Com0comPairInfo(
                    pair.PairNumber,
                    pair.PortA,
                    pair.PortB,
                    pair.DeviceA,
                    pair.DeviceB,
                    !string.IsNullOrWhiteSpace(pair.PortA) && !string.IsNullOrWhiteSpace(pair.PortB)))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool TryParseCom0comDevice(string deviceName, out char side, out int pairNumber)
    {
        side = '\0';
        pairNumber = -1;

        const string prefix = @"\Device\com0com";
        if (!deviceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || deviceName.Length <= prefix.Length + 1)
        {
            return false;
        }

        side = deviceName[prefix.Length];
        if (side is not ('1' or '2'))
        {
            return false;
        }

        return int.TryParse(deviceName[(prefix.Length + 1)..], out pairNumber);
    }

    private sealed class PairBuilder
    {
        public PairBuilder(int pairNumber)
        {
            PairNumber = pairNumber;
        }

        public int PairNumber { get; }
        public string? PortA { get; set; }
        public string? PortB { get; set; }
        public string? DeviceA { get; set; }
        public string? DeviceB { get; set; }
    }
}
