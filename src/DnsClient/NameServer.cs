﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DnsClient
{
    /// <summary>
    /// Represents a name server instance used by <see cref="ILookupClient"/>.
    /// Also, comes with some static methods to resolve name servers from the local network configuration.
    /// </summary>
    public class NameServer
    {
        /// <summary>
        /// The default DNS server port.
        /// </summary>
        public const int DefaultPort = 53;

        /// <summary>
        /// The public google DNS IPv4 endpoint.
        /// </summary>
        public static readonly IPEndPoint GooglePublicDns = new IPEndPoint(IPAddress.Parse("8.8.4.4"), DefaultPort);

        /// <summary>
        /// The second public google DNS IPv6 endpoint.
        /// </summary>
        public static readonly IPEndPoint GooglePublicDns2 = new IPEndPoint(IPAddress.Parse("8.8.8.8"), DefaultPort);

        /// <summary>
        /// The public google DNS IPv6 endpoint.
        /// </summary>
        public static readonly IPEndPoint GooglePublicDnsIPv6 = new IPEndPoint(IPAddress.Parse("2001:4860:4860::8844"), DefaultPort);

        /// <summary>
        /// The second public google DNS IPv6 endpoint.
        /// </summary>
        public static readonly IPEndPoint GooglePublicDns2IPv6 = new IPEndPoint(IPAddress.Parse("2001:4860:4860::8888"), DefaultPort);

        internal const string EtcResolvConfFile = "/etc/resolv.conf";

        /// <summary>
        /// Initializes a new instance of the <see cref="NameServer"/> class.
        /// </summary>
        /// <param name="endpoint">The endpoint.</param>
        public NameServer(IPAddress endpoint)
            : this(new IPEndPoint(endpoint, DefaultPort))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="NameServer"/> class.
        /// </summary>
        /// <param name="endpoint">The endpoint.</param>
        /// <exception cref="System.ArgumentNullException">If <paramref name="endpoint"/> is null.</exception>
        public NameServer(IPEndPoint endpoint)
        {
            Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        }

        /// <summary>
        /// Gets a value indicating whether this <see cref="NameServer"/> is enabled.
        /// <para>
        /// The instance might get disabled if <see cref="ILookupClient"/> encounters problems to connect to it.
        /// </para>
        /// </summary>
        /// <value>
        ///   <c>true</c> if enabled; otherwise, <c>false</c>.
        /// </value>
        public bool Enabled { get; internal set; } = true;

        /// <summary>
        /// Gets the endpoint of this instance.
        /// </summary>
        /// <value>
        /// The endpoint.
        /// </value>
        public IPEndPoint Endpoint { get; }

        /// <summary>
        /// Gets the size of the supported UDP payload.
        /// <para>
        /// This value might get updated by <see cref="ILookupClient"/> by reading the options records returned by a query.
        /// </para>
        /// </summary>
        /// <value>
        /// The size of the supported UDP payload.
        /// </value>
        public int? SupportedUdpPayloadSize { get; internal set; }

        /// <summary>
        /// Returns a <see cref="System.String" /> that represents this instance.
        /// </summary>
        /// <returns>
        /// A <see cref="System.String" /> that represents this instance.
        /// </returns>
        public override string ToString()
        {
            return $"{Endpoint} (Udp: {SupportedUdpPayloadSize ?? 512})";
        }

        /// <summary>
        /// Gets a list of name servers by iterating over the available network interfaces.
        /// <para>
        /// If <paramref name="fallbackToGooglePublicDns" /> is enabled, this method will return the google public dns endpoints if no
        /// local DNS server was found.
        /// </para>
        /// </summary>
        /// <param name="skipIPv6SiteLocal">If set to <c>true</c> local IPv6 sites are skiped.</param>
        /// <param name="fallbackToGooglePublicDns">If set to <c>true</c> the public Google DNS servers are returned if no other servers could be found.</param>
        /// <returns>
        /// The list of name servers.
        /// </returns>
        public static ICollection<IPEndPoint> ResolveNameServers(bool skipIPv6SiteLocal = true, bool fallbackToGooglePublicDns = true)
        {
            var endpoints = ResolveNameServersInternal(skipIPv6SiteLocal);
            if (endpoints.Count == 0)
            {
                return new[]
                {
                    GooglePublicDnsIPv6,
                    GooglePublicDns2IPv6,
                    GooglePublicDns,
                    GooglePublicDns2,
                };
            }

            return endpoints;
        }

        private static ICollection<IPEndPoint> ResolveNameServersInternal(bool skipIPv6SiteLocal)
        {
#if PORTABLE
            Exception frameworkEx = null;
#endif
            try
            {
                return QueryNetworkInterfaces(skipIPv6SiteLocal);
            }
#if !PORTABLE
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error resolving name servers: {ex.Message} HResult: {ex.HResult}.", ex);
            }
#endif
#if PORTABLE
            catch (Exception ex)
            {
                // lets try native
                frameworkEx = ex;
            }

            try
            {
                return GetDnsEndpointsNative();
            }
            catch (Exception ex)
            {
                // log etc?
                throw new AggregateException("Could not resolve name servers via .NET Framework nor native. See inner exceptions for details.", frameworkEx, ex);
            }
#endif
        }

#if PORTABLE

        private static IPEndPoint[] GetDnsEndpointsNative()
        {
            IPAddress[] addresses = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var fixedInfo = Windows.IpHlpApi.FixedNetworkInformation.GetFixedInformation();

                addresses = fixedInfo.DnsAddresses.ToArray();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // TODO: Remove if fixed in dotnet core runtim 1.2.x?
                addresses = Linux.StringParsingHelpers.ParseDnsAddressesFromResolvConfFile(EtcResolvConfFile).ToArray();
            }

            return addresses?.Select(p => new IPEndPoint(p, DefaultPort)).ToArray();
        }

#endif

        private static IPEndPoint[] QueryNetworkInterfaces(bool skipIPv6SiteLocal)
        {
            var result = new HashSet<IPEndPoint>();

            var adapters = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface networkInterface in
                adapters
                    .Where(p => p.OperationalStatus == OperationalStatus.Up
                    && p.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                foreach (IPAddress dnsAddress in networkInterface
                    .GetIPProperties()
                    .DnsAddresses
                    .Where(i =>
                        i.AddressFamily == AddressFamily.InterNetwork
                        || i.AddressFamily == AddressFamily.InterNetworkV6))
                {
                    if (dnsAddress.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        if (skipIPv6SiteLocal && dnsAddress.IsIPv6SiteLocal)
                        {
                            continue;
                        }
                    }

                    result.Add(new IPEndPoint(dnsAddress, DefaultPort));
                }
            }

            return result.ToArray();
        }

        internal NameServer Clone()
        {
            return new NameServer(Endpoint)
            {
                Enabled = Enabled,
                SupportedUdpPayloadSize = SupportedUdpPayloadSize
            };
        }
    }
}