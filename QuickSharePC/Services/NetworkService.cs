using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using QuickShare.PC.Models;

namespace QuickShare.PC.Services
{
    public class NetworkService
    {
        private static readonly string[] VirtualKeywords = new[]
        {
            "virtual", "vpn", "tap", "tun", "loopback", "host-only",
            "wsl", "hyper-v", "vmware", "vbox", "pseudo", "teredo", "isatap"
        };

        /// <summary>
        /// Gets all active, non-virtual physical LAN network interfaces with IPv4 addresses.
        /// </summary>
        public List<NetworkInterfaceInfo> GetAvailableInterfaces()
        {
            var result = new List<NetworkInterfaceInfo>();

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;

                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    string desc = (ni.Description + " " + ni.Name).ToLower();
                    if (VirtualKeywords.Any(k => desc.Contains(k)))
                        continue;

                    var ipProperties = ni.GetIPProperties();
                    foreach (var ip in ipProperties.UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork) // IPv4
                        {
                            string ipStr = ip.Address.ToString();
                            if (ipStr.StartsWith("127.") || ipStr.StartsWith("169.254."))
                                continue;

                            string typeStr = ni.NetworkInterfaceType switch
                            {
                                NetworkInterfaceType.Wireless80211 => "WLAN (无线局域网)",
                                NetworkInterfaceType.Ethernet => "以太网 (有线局域网)",
                                NetworkInterfaceType.GigabitEthernet => "千兆以太网",
                                _ => "局域网"
                            };

                            result.Add(new NetworkInterfaceInfo
                            {
                                Name = ni.Name,
                                IpAddress = ipStr,
                                InterfaceType = typeStr,
                                Description = ni.Description,
                                IsSelected = true
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting network interfaces: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Gets the primary active LAN IPv4 address for streaming.
        /// Prioritizes Wi-Fi / Ethernet adapters with standard private subnet IP addresses.
        /// </summary>
        public string GetPrimaryLanIpAddress()
        {
            var interfaces = GetAvailableInterfaces();
            if (interfaces.Count == 0)
            {
                return GetFallbackLocalIp();
            }

            // Prioritize standard private LAN IP addresses (192.168.x.x, 10.x.x.x, 172.16-31.x.x)
            var lanNic = interfaces.FirstOrDefault(i => IsPrivateLanIp(i.IpAddress)) ?? interfaces.First();
            return lanNic.IpAddress;
        }

        /// <summary>
        /// Gets the primary LAN network interface info.
        /// </summary>
        public NetworkInterfaceInfo GetPrimaryLanInterface()
        {
            var interfaces = GetAvailableInterfaces();
            if (interfaces.Count > 0)
            {
                return interfaces.FirstOrDefault(i => IsPrivateLanIp(i.IpAddress)) ?? interfaces.First();
            }

            return new NetworkInterfaceInfo
            {
                Name = "本地网络",
                IpAddress = GetFallbackLocalIp(),
                InterfaceType = "局域网",
                Description = "默认网络适配器",
                IsSelected = true
            };
        }

        private static bool IsPrivateLanIp(string ipStr)
        {
            if (IPAddress.TryParse(ipStr, out var ip))
            {
                byte[] bytes = ip.GetAddressBytes();
                if (bytes.Length == 4)
                {
                    // 10.0.0.0 - 10.255.255.255
                    if (bytes[0] == 10) return true;
                    // 172.16.0.0 - 172.31.255.255
                    if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                    // 192.168.0.0 - 192.168.255.255
                    if (bytes[0] == 192 && bytes[1] == 168) return true;
                }
            }
            return false;
        }

        private static string GetFallbackLocalIp()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                // Connect to a public endpoint (doesn't send packets) to resolve the routing local IP
                socket.Connect("223.5.5.5", 65530);
                if (socket.LocalEndPoint is IPEndPoint endPoint)
                {
                    return endPoint.Address.ToString();
                }
            }
            catch
            {
                // Fallback
            }

            try
            {
                string hostName = Dns.GetHostName();
                var hostEntry = Dns.GetHostEntry(hostName);
                foreach (var ip in hostEntry.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    {
                        return ip.ToString();
                    }
                }
            }
            catch
            {
                // Fallback
            }

            return "127.0.0.1";
        }
    }
}
