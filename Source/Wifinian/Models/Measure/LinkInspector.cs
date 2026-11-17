using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Wifinian.Models.Measure;

/// <summary>
/// Inspector of IP link of a network interface
/// </summary>
/// <remarks>
/// No method of this class throws an exception except for cancellation of waiting.
/// </remarks>
internal static class LinkInspector
{
	/// <summary>
	/// Finds the network interface of a specified interface ID.
	/// </summary>
	/// <param name="interfaceId">Interface ID</param>
	/// <returns>Network interface if found, null otherwise</returns>
	public static NetworkInterface Find(Guid interfaceId)
	{
		try
		{
			return NetworkInterface.GetAllNetworkInterfaces()
				.FirstOrDefault(x => Guid.TryParse(x.Id, out Guid id) && (id == interfaceId));
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to find a network interface." + Environment.NewLine
				+ ex);
			return null;
		}
	}

	/// <summary>
	/// Gets the IPv4 address of a network interface excluding link-local one.
	/// </summary>
	public static IPAddress GetLocalAddress(NetworkInterface networkInterface)
	{
		try
		{
			return networkInterface?.GetIPProperties().UnicastAddresses
				.Select(x => x.Address)
				.FirstOrDefault(x => (x.AddressFamily == AddressFamily.InterNetwork)
					&& !IPAddress.IsLoopback(x)
					&& !x.ToString().StartsWith("169.254.", StringComparison.Ordinal));
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to get a local address." + Environment.NewLine
				+ ex);
			return null;
		}
	}

	/// <summary>
	/// Gets the global IPv6 address of a network interface.
	/// </summary>
	public static IPAddress GetGlobalAddressV6(NetworkInterface networkInterface)
	{
		try
		{
			return networkInterface?.GetIPProperties().UnicastAddresses
				.Where(x => (x.Address.AddressFamily == AddressFamily.InterNetworkV6)
					&& !IPAddress.IsLoopback(x.Address)
					&& !x.Address.IsIPv6LinkLocal
					&& !x.Address.IsIPv6SiteLocal
					&& !x.Address.IsIPv6Multicast
					&& !x.Address.IsIPv6Teredo
					&& !IsUniqueLocal(x.Address)
					&& (x.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred))
				// A temporary address is what the system uses for outgoing connections.
				.OrderByDescending(x => x.SuffixOrigin == SuffixOrigin.Random)
				.Select(x => x.Address)
				.FirstOrDefault();
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to get a global IPv6 address." + Environment.NewLine
				+ ex);
			return null;
		}

		static bool IsUniqueLocal(IPAddress address) =>
			(address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7
	}

	/// <summary>
	/// Gets the IPv4 address of the gateway of a network interface.
	/// </summary>
	public static IPAddress GetGateway(NetworkInterface networkInterface)
	{
		try
		{
			return networkInterface?.GetIPProperties().GatewayAddresses
				.Select(x => x.Address)
				.FirstOrDefault(x => (x is not null)
					&& (x.AddressFamily == AddressFamily.InterNetwork)
					&& !x.Equals(IPAddress.Any));
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to get a gateway address." + Environment.NewLine
				+ ex);
			return null;
		}
	}

	/// <summary>
	/// Determines whether a network interface is up and has a local address and a gateway.
	/// </summary>
	public static bool IsReady(Guid interfaceId)
	{
		try
		{
			var networkInterface = Find(interfaceId);
			return (networkInterface is { OperationalStatus: OperationalStatus.Up })
				&& (GetLocalAddress(networkInterface) is not null)
				&& (GetGateway(networkInterface) is not null);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to check a network interface." + Environment.NewLine
				+ ex);
			return false;
		}
	}

	/// <summary>
	/// Waits until a network interface gets ready.
	/// </summary>
	/// <returns>True if ready within the timeout</returns>
	public static async Task<bool> WaitForAddressAsync(Guid interfaceId, TimeSpan timeout, CancellationToken cancellationToken)
	{
		var stopwatch = Stopwatch.StartNew();

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (IsReady(interfaceId))
				return true;

			if (stopwatch.Elapsed >= timeout)
				return false;

			await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Gets the bytes received and sent by a network interface.
	/// </summary>
	public static (long received, long sent) GetBytes(Guid interfaceId) => GetBytes(Find(interfaceId));

	/// <summary>
	/// Gets the bytes received and sent by a network interface.
	/// </summary>
	/// <remarks>
	/// Although the name of the source, the values are those of the interface and so include
	/// IPv6 traffic.
	/// </remarks>
	public static (long received, long sent) GetBytes(NetworkInterface networkInterface)
	{
		try
		{
			if (networkInterface is null)
				return (0L, 0L);

			var statistics = networkInterface.GetIPv4Statistics();
			return (statistics.BytesReceived, statistics.BytesSent);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to get bytes of a network interface." + Environment.NewLine
				+ ex);
			return (0L, 0L);
		}
	}

	/// <summary>
	/// Gets the rate (Mbps) of traffic, receiving and sending in total, of a network interface.
	/// </summary>
	public static async Task<double> GetRateMbpsAsync(Guid interfaceId, TimeSpan window)
	{
		try
		{
			var networkInterface = Find(interfaceId);
			if (networkInterface is null)
				return 0D;

			var stopwatch = Stopwatch.StartNew();
			var (oldReceived, oldSent) = GetBytes(networkInterface);

			await Task.Delay(window).ConfigureAwait(false);

			var (newReceived, newSent) = GetBytes(networkInterface);
			var seconds = stopwatch.Elapsed.TotalSeconds;
			if (seconds <= 0D)
				return 0D;

			var bytes = Math.Max(0L, newReceived - oldReceived) + Math.Max(0L, newSent - oldSent);
			return bytes * 8D / seconds / 1e6;
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to get rate of a network interface." + Environment.NewLine
				+ ex);
			return 0D;
		}
	}
}