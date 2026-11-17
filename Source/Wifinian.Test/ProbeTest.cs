using System;
using System.Collections.Generic;
using System.Linq;
using ManagedNativeWifi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Wifinian.Models;
using Wifinian.Models.Measure;
using Wifinian.Models.Wlan;

namespace Wifinian.Test;

[TestClass]
public class ProbeTest
{
	private static readonly Guid _interfaceId = Guid.NewGuid();
	private static readonly DateTime _now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

	#region Targets

	[TestMethod]
	public void TestProbeTargetsOrder()
	{
		var office = Profile("Office", 60, isConnected: true);
		var ext = Profile("EXT", 90);
		var ext2 = Profile("EXT2", 75);
		var away = Profile("Away", 0);
		var off = Profile("Off", 80, isRadioOn: false);

		var targets = AppController.GetProbeTargets(new[] { away, ext2, office, off, ext });

		// The connected one comes first and the others follow in descending order of signal.
		CollectionAssert.AreEqual(new[] { office, ext, ext2 }, targets);
	}

	[TestMethod]
	public void TestProbeTargetsIncludeAvoidedAndManual()
	{
		var office = Profile("Office", 60, isConnected: true);
		var guest = Profile("Guest", 50, isAutoSwitchEnabled: false);
		var avoided = Profile("EXT2", 40);
		avoided.IsAvoided = true;

		var targets = AppController.GetProbeTargets(new[] { office, guest, avoided });

		CollectionAssert.AreEqual(new[] { office, guest, avoided }, targets);
	}

	[TestMethod]
	public void TestProbeTargetsNone()
	{
		Assert.AreEqual(0, AppController.GetProbeTargets(Array.Empty<ProfileItem>()).Length);
		Assert.AreEqual(0, AppController.GetProbeTargets(new[] { Profile("Away", 0) }).Length);

		// With no profile connected, the profiles in range are still tested.
		var ext = Profile("EXT", 30);
		var office = Profile("Office", 70);
		CollectionAssert.AreEqual(new[] { office, ext }, AppController.GetProbeTargets(new[] { ext, office }));
	}

	#endregion

	#region Idle candidate

	[TestMethod]
	public void TestIdleCandidateNeverTestedFirst()
	{
		var office = Profile("Office", 70, isConnected: true);
		var ext = Profile("EXT", 80);
		var ext2 = Profile("EXT2", 60);

		var times = new Dictionary<string, DateTime>
		{
			["Office"] = _now.AddMinutes(-10),
			["EXT"] = _now.AddDays(-5)
		};

		// EXT2 has never been measured.
		Assert.AreSame(ext2, Select(new[] { office, ext, ext2 }, times));

		// The one measured least recently comes next.
		times["EXT2"] = _now.AddDays(-4);
		Assert.AreSame(ext, Select(new[] { office, ext, ext2 }, times));

		// Among those never measured, the one of the strongest signal is selected.
		times.Remove("EXT");
		times.Remove("EXT2");
		Assert.AreSame(ext, Select(new[] { office, ext2, ext }, times));
	}

	[TestMethod]
	public void TestIdleCandidateStalePeriod()
	{
		var office = Profile("Office", 70, isConnected: true);
		var ext = Profile("EXT", 80);

		var times = new Dictionary<string, DateTime>
		{
			["EXT"] = _now - AppController.ProbeStalePeriod + TimeSpan.FromMinutes(1)
		};
		Assert.IsNull(Select(new[] { office, ext }, times));

		times["EXT"] = _now - AppController.ProbeStalePeriod - TimeSpan.FromMinutes(1);
		Assert.AreSame(ext, Select(new[] { office, ext }, times));
	}

	[TestMethod]
	public void TestIdleCandidateExcluded()
	{
		var times = new Dictionary<string, DateTime>();

		// No profile is connected.
		Assert.IsNull(Select(new[] { Profile("Office", 70), Profile("EXT", 80) }, times));

		// The connected profile is not automatic switch enabled.
		Assert.IsNull(Select(new[] { Profile("Guest", 70, isConnected: true, isAutoSwitchEnabled: false), Profile("EXT", 80) }, times));

		var office = Profile("Office", 70, isConnected: true);

		// The connected profile itself is never a candidate.
		Assert.IsNull(Select(new[] { office }, times));

		// Not automatic switch enabled
		Assert.IsNull(Select(new[] { office, Profile("Guest", 80, isAutoSwitchEnabled: false) }, times));

		// Avoided
		var avoided = Profile("EXT2", 80);
		avoided.IsAvoided = true;
		Assert.IsNull(Select(new[] { office, avoided }, times));

		// Signal below the threshold
		Assert.IsNull(Select(new[] { office, Profile("EXT", 39) }, times));
		Assert.IsNotNull(Select(new[] { office, Profile("EXT", 40) }, times));

		// Radio off
		Assert.IsNull(Select(new[] { office, Profile("EXT", 80, isRadioOn: false) }, times));
	}

	private static ProfileItem Select(IEnumerable<ProfileItem> profiles, IReadOnlyDictionary<string, DateTime> times) =>
		AppController.SelectIdleCandidate(
			profiles,
			40,
			x => times.TryGetValue(x.Name, out DateTime time) ? time : default,
			_now);

	#endregion

	#region Idle time

	[TestMethod]
	public void TestIdleElapsedTime()
	{
		Assert.AreEqual(TimeSpan.FromSeconds(5), IdleDetector.GetElapsedTime(15000U, 10000U));
		Assert.AreEqual(TimeSpan.Zero, IdleDetector.GetElapsedTime(10000U, 10000U));

		// The tick count wraps around.
		Assert.AreEqual(TimeSpan.FromMilliseconds(300), IdleDetector.GetElapsedTime(199U, uint.MaxValue - 100U));
	}

	[TestMethod]
	public void TestIdleTimeNotNegative()
	{
		var idleTime = IdleDetector.GetIdleTime();
		Assert.IsTrue(idleTime >= TimeSpan.Zero);
		Assert.IsTrue(idleTime < TimeSpan.FromDays(50));
	}

	#endregion

	#region Settings

	[TestMethod]
	public void TestSettingsProbe()
	{
		var settings = Settings.Current;

		var probesWhenIdle = settings.ProbesWhenIdle;
		var probeInterval = settings.ProbeInterval;
		try
		{
			Assert.IsFalse(probesWhenIdle);
			Assert.AreEqual(6, probeInterval);

			settings.ProbeInterval = 0;
			Assert.AreEqual(1, settings.ProbeInterval);
			settings.ProbeInterval = 100;
			Assert.AreEqual(48, settings.ProbeInterval);
			settings.ProbeInterval = 12;
			Assert.AreEqual(12, settings.ProbeInterval);
		}
		finally
		{
			settings.ProbesWhenIdle = probesWhenIdle;
			settings.ProbeInterval = probeInterval;
		}
	}

	#endregion

	private static ProfileItem Profile(string name, int signal, bool isConnected = false, bool isAutoSwitchEnabled = true, bool isRadioOn = true) => new(
		name: name,
		interfaceId: _interfaceId,
		interfaceName: null,
		interfaceDescription: null,
		authentication: default(AuthenticationMethod),
		encryption: default(EncryptionType),
		isAutoConnectEnabled: true,
		isAutoSwitchEnabled: isAutoSwitchEnabled,
		position: 0,
		isRadioOn: isRadioOn,
		isConnected: isConnected,
		protocol: null,
		signal: signal,
		band: 2.4F,
		channel: 1);
}