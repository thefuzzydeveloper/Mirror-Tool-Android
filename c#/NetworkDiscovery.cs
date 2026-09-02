using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace WiFiAutoStreamSync;

public static class NetworkDiscovery
{
    public static List<string> GetActiveIPv4Subnets()
    {
        var ips = new HashSet<string>();
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up ||
                iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var addr in iface.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    string ip = addr.Address.ToString();
                    if (!ip.StartsWith("127.") && !ip.StartsWith("169.254"))
                        ips.Add(ip);
                }
            }
        }
        return [.. ips];
    }

    public static async Task<List<string>> ScanSubnetDevicesAsync(string manualIpCsv, HashSet<string> excludeIps, CancellationToken ct)
    {
        var candidates = new HashSet<string>();

        if (!string.IsNullOrWhiteSpace(manualIpCsv))
        {
            foreach (var ip in manualIpCsv.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!excludeIps.Contains(ip))
                    candidates.Add(ip);
            }
        }

        foreach (var localIp in GetActiveIPv4Subnets())
        {
            var parts = localIp.Split('.');
            if (parts.Length == 4)
            {
                string prefix = $"{parts[0]}.{parts[1]}.{parts[2]}";
                for (int i = 1; i < 255; i++)
                {
                    string ip = $"{prefix}.{i}";
                    if (!excludeIps.Contains(ip))
                        candidates.Add(ip);
                }
            }
        }

        var discovered = new List<string>();
        using var throttle = new SemaphoreSlim(40);
        var tasks = candidates.Select(async ip =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                if (await ProbePortAsync(ip, ct))
                {
                    lock (discovered)
                        discovered.Add(ip);
                }
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);
        return discovered;
    }

    private static async Task<bool> ProbePortAsync(string ip, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(700);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await client.ConnectAsync(ip, Protocol.TcpDataPort, linked.Token);
            var stream = client.GetStream();

            byte[] ping = [Protocol.MagicHeader[0], Protocol.MagicHeader[1], Protocol.CmdPing];
            await stream.WriteAsync(ping, linked.Token);

            byte[] resp = new byte[1];
            int read = await stream.ReadAsync(resp, linked.Token);
            return read == 1 && resp[0] == 0x00;
        }
        catch
        {
            return false;
        }
    }
}