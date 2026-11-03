using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ReFlow.Services;

/// <summary>
/// Усиленное определение страны: HTTP-запрос, у которого исходящий адрес — адрес настоящего адаптера
/// (Ethernet / Wi-Fi), а не виртуального адаптера VPN. Windows тогда отправляет пакеты по маршруту
/// физического адаптера, мимо туннеля. С VPN, у которых включена защита от утечек (kill switch),
/// запрос намеренно блокируется — тогда реальную страну узнать нельзя.
/// </summary>
internal static class VpnBypassProbe
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Признаки виртуальных адаптеров VPN в имени/описании (TAP-адаптеры выдают себя за Ethernet).</summary>
    private static readonly string[] VpnAdapterMarkers =
    {
        "vpn", "tap-", "tap adapter", "wireguard", "wintun", "openvpn", "tunnel", "nordlynx", "proton",
        "outline", "amnezia", "zerotier", "tailscale", "hamachi", "fortinet", "anyconnect", "globalprotect",
        "pangp", "softether", "sstp", "l2tp", "pptp", "ikev2"
    };

    private static readonly NetworkInterfaceType[] PhysicalTypes =
    {
        NetworkInterfaceType.Ethernet,
        NetworkInterfaceType.Wireless80211,
        NetworkInterfaceType.GigabitEthernet,
        NetworkInterfaceType.FastEthernetT,
        NetworkInterfaceType.FastEthernetFx,
        NetworkInterfaceType.Ethernet3Megabit
    };

    /// <summary>IPv4-адреса включённых физических адаптеров со шлюзом по умолчанию.</summary>
    public static List<IPAddress> GetPhysicalIPv4Addresses()
    {
        var result = new List<IPAddress>();
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return result;
        }

        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !PhysicalTypes.Contains(nic.NetworkInterfaceType))
                continue;
            var label = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (VpnAdapterMarkers.Any(label.Contains))
                continue;

            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            if (!hasGateway)
                continue;

            result.AddRange(props.UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => u.Address));
        }

        return result;
    }

    /// <summary>
    /// HttpClient, все соединения которого идут с адреса <paramref name="localAddress"/>.
    /// Системный прокси не используется: «VPN» в режиме прокси тоже обходится.
    /// </summary>
    public static HttpClient CreateBoundClient(IPAddress localAddress)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = RequestTimeout,
            ConnectCallback = async (ctx, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, AddressFamily.InterNetwork, ct)
                    .ConfigureAwait(false);
                if (addresses.Length == 0)
                    throw new HttpRequestException("No IPv4 address for " + ctx.DnsEndPoint.Host);

                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    socket.Bind(new IPEndPoint(localAddress, 0));
                    await socket.ConnectAsync(new IPEndPoint(addresses[0], ctx.DnsEndPoint.Port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler) { Timeout = RequestTimeout };
    }
}
