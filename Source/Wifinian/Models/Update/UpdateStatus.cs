using System;

namespace Wifinian.Models.Update;

/// <summary>
/// Phase of looking for and installing an update
/// </summary>
internal enum UpdatePhase
{
	/// <summary>
	/// Nothing is known yet.
	/// </summary>
	None = 0,

	/// <summary>
	/// The manifest of the latest release is being asked for.
	/// </summary>
	Checking,

	/// <summary>
	/// The running version is the latest.
	/// </summary>
	Latest,

	/// <summary>
	/// A newer version is known and can be installed.
	/// </summary>
	Available,

	/// <summary>
	/// The installer of a newer version is being downloaded.
	/// </summary>
	Downloading,

	/// <summary>
	/// The last check or installation failed.
	/// </summary>
	Failed
}

/// <summary>
/// State of looking for and installing an update, which the menu shows
/// </summary>
internal class UpdateStatus
{
	public UpdatePhase Phase { get; }

	/// <summary>
	/// Version concerned (the running version if it is the latest, otherwise the newer version)
	/// </summary>
	public string Version { get; }

	/// <summary>
	/// Short reason of failure
	/// </summary>
	public string Reason { get; }

	private UpdateStatus(UpdatePhase phase, string version = null, string reason = null)
	{
		this.Phase = phase;
		this.Version = version;
		this.Reason = reason;
	}

	public static UpdateStatus None { get; } = new(UpdatePhase.None);
	public static UpdateStatus Checking { get; } = new(UpdatePhase.Checking);
	public static UpdateStatus Latest(Version running) => new(UpdatePhase.Latest, UpdateVerifier.Normalize(running).ToString(3));
	public static UpdateStatus Available(string version) => new(UpdatePhase.Available, version);
	public static UpdateStatus Downloading(string version) => new(UpdatePhase.Downloading, version);
	public static UpdateStatus Failed(string reason) => new(UpdatePhase.Failed, reason: reason);

	/// <summary>
	/// Whether a check or an installation is running
	/// </summary>
	public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading;
}
