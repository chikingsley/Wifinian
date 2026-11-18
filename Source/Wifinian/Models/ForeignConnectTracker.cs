using System;
using System.Collections.Generic;

using ManagedNativeWifi;

namespace Wifinian.Models;

/// <summary>
/// Record of connections of wireless interfaces started outside this app
/// </summary>
/// <remarks>
/// A connection started by the user (Windows Wi-Fi menu, netsh) or by the system must not be
/// taken over by automatic connection of this app. This class reads no clock and is not thread
/// safe: the caller gives the time and serializes the calls.
/// </remarks>
internal class ForeignConnectTracker
{
	/// <summary>
	/// Period in which a connection started outside this app is regarded as in progress unless
	/// its end is notified
	/// </summary>
	public static readonly TimeSpan Period = TimeSpan.FromSeconds(40);

	private readonly Dictionary<Guid, int> _ownConnects = new(); // Numbers of connections by this app in progress
	private readonly Dictionary<Guid, DateTime> _foreignConnects = new(); // UTC

	/// <summary>
	/// Records that this app starts a connection in an interface.
	/// </summary>
	public void BeginOwn(Guid interfaceId)
	{
		_ownConnects.TryGetValue(interfaceId, out int count);
		_ownConnects[interfaceId] = count + 1;
	}

	/// <summary>
	/// Records that a connection started by this app in an interface has ended.
	/// </summary>
	public void EndOwn(Guid interfaceId)
	{
		if (_ownConnects.TryGetValue(interfaceId, out int count) && (count > 1))
			_ownConnects[interfaceId] = count - 1;
		else
			_ownConnects.Remove(interfaceId);
	}

	/// <summary>
	/// Reflects a notification of connection.
	/// </summary>
	/// <param name="interfaceId">Interface ID</param>
	/// <param name="changedState">Notified state</param>
	/// <param name="utcNow">Current time (UTC)</param>
	/// <remarks>
	/// <see cref="ConnectionChangedState.Failed"/> is the failure of one attempt
	/// (wlan_notification_acm_connection_attempt_fail). Windows goes on with further attempts
	/// within the same connection, between which the interface looks disconnected, and so
	/// the connection is still in progress. Its end, successful or not, is always notified by
	/// <see cref="ConnectionChangedState.Completed"/>.
	/// </remarks>
	public void Track(Guid interfaceId, ConnectionChangedState changedState, DateTime utcNow)
	{
		switch (changedState)
		{
			case ConnectionChangedState.Started:
				if (!_ownConnects.ContainsKey(interfaceId))
					_foreignConnects[interfaceId] = utcNow;
				break;

			case ConnectionChangedState.Completed:
				_foreignConnects.Remove(interfaceId);
				break;
		}
	}

	/// <summary>
	/// Determines whether a connection started outside this app is in progress in an interface.
	/// </summary>
	/// <param name="interfaceId">Interface ID</param>
	/// <param name="utcNow">Current time (UTC)</param>
	public bool IsConnecting(Guid interfaceId, DateTime utcNow)
	{
		if (!_foreignConnects.TryGetValue(interfaceId, out DateTime time))
			return false;

		if ((utcNow - time).Duration() < Period)
			return true;

		_foreignConnects.Remove(interfaceId);
		return false;
	}
}
