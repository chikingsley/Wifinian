using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Wifinian.Models.Update;

namespace Wifinian.Test;

[TestClass]
public class UpdateVerifierTest
{
	// A key made for each run of these tests. The key of the releases is never used here.
	private static RSACng _rsa;
	private static string _publicKey;

	private static readonly byte[] _data = Encoding.UTF8.GetBytes("This stands for the bytes of an installer.");

	[ClassInitialize]
	public static void Initialize(TestContext context)
	{
		_rsa = new RSACng(2048);
		_publicKey = _rsa.ToXmlString(false);
	}

	[ClassCleanup]
	public static void Cleanup()
	{
		_rsa?.Dispose();
	}

	private static string Sign(byte[] data) =>
		Convert.ToBase64String(_rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

	#region Version

	[TestMethod]
	public void TestVersionNewer()
	{
		var running = new Version(3, 9, 0, 0);

		Assert.IsTrue(UpdateVerifier.IsNewer("3.9.1", running));
		Assert.IsTrue(UpdateVerifier.IsNewer("3.10.0", running)); // Numbers, not text, are compared.
		Assert.IsTrue(UpdateVerifier.IsNewer("4.0.0", running));
		Assert.IsTrue(UpdateVerifier.IsNewer("3.10.0", new Version(3, 9, 9, 0)));

		Assert.IsFalse(UpdateVerifier.IsNewer("3.9.0", new Version(3, 10, 0, 0)));
		Assert.IsFalse(UpdateVerifier.IsNewer("3.8.9", running));
		Assert.IsFalse(UpdateVerifier.IsNewer("2.99.99", running));
	}

	[TestMethod]
	public void TestVersionEqual()
	{
		// The fourth component of the running version does not count.
		Assert.IsFalse(UpdateVerifier.IsNewer("3.9.0", new Version(3, 9, 0, 0)));
		Assert.IsFalse(UpdateVerifier.IsNewer("3.9.0", new Version(3, 9, 0, 5)));
		Assert.IsFalse(UpdateVerifier.IsNewer("3.9.0", new Version(3, 9, 0)));
		Assert.IsFalse(UpdateVerifier.IsNewer("3.9.0", new Version(3, 9)));
		Assert.IsTrue(UpdateVerifier.IsNewer("3.9.1", new Version(3, 9)));
	}

	[TestMethod]
	public void TestVersionMalformed()
	{
		var running = new Version(3, 9, 0, 0);

		foreach (var source in new[] { null, "", " ", "4", "4.0", "4.0.0.0", "v4.0.0", "4.0.0-beta", "4.0.x", "4..0", ".4.0.0", "4.0.0.",
			"+4.0.0", "-4.0.0", " 4.0.0", "4.0.0 ", "4.0. 0", "4,0,0", "99999999999.0.0", "4.0.0\n", "４.0.0" })
		{
			Assert.IsFalse(UpdateVerifier.TryParseVersion(source, out _), $"Parsed: [{source}]");
			Assert.IsFalse(UpdateVerifier.IsNewer(source, running), $"Newer: [{source}]");
		}

		Assert.IsFalse(UpdateVerifier.IsNewer("4.0.0", null));

		Assert.IsTrue(UpdateVerifier.TryParseVersion("3.10.2", out Version version));
		Assert.AreEqual(new Version(3, 10, 2), version);
	}

	#endregion

	#region URL

	[TestMethod]
	public void TestUrlAllowed()
	{
		Assert.IsTrue(UpdateVerifier.IsAllowedUrl("https://github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi"));
		Assert.IsTrue(UpdateVerifier.IsAllowedUrl("https://GitHub.com/chikingsley/Wifinian/releases/download/v3.10.0/Wifinian.Installer.msi"));
	}

	[TestMethod]
	public void TestUrlRefused()
	{
		foreach (var url in new[]
		{
			null,
			"",
			"Wifinian.Installer.msi",
			"/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",

			// Scheme
			"http://github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"ftp://github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"file:///C:/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"file://github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",

			// Host
			"https://example.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://github.com.evil.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://evilgithub.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://www.github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://raw.githubusercontent.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://github.com./chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://github.com:8443/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://githuｂ.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",

			// User information
			"https://github.com@evil.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://user@github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://user:password@github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://evil.com/@github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",

			// Other repository or other place of this repository
			"https://github.com/emoacht/Wifinian/releases/download/3.8.0-Installer/WifinianInstaller380.zip",
			"https://github.com/chikingsley/Other/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://github.com/chikingsley/Wifinian/archive/refs/heads/master.zip",
			"https://github.com/chikingsley/Wifinian/releases/latest/download/Wifinian.Installer.msi",
			"https://github.com/chikingsley/wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://github.com/evil/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://github.com/chikingsley/Wifinian/releases/download/",
			"https://github.com/chikingsley/Wifinian/releases/download",

			// Tricks on the path
			"https://github.com/chikingsley/Wifinian/releases/download/../../evil",
			"https://github.com/chikingsley/Wifinian/releases/download/../../../../evil/repo/releases/download/v1/x.msi",
			"https://github.com/chikingsley/Wifinian/releases/download/v3.9.1/../../../../../evil/x.msi",
			"https://github.com/chikingsley/Wifinian/releases/download/%2e%2e/%2e%2e/evil",
			"https://github.com/chikingsley/Wifinian/releases/download/.%2e/evil",
			"https://github.com/chikingsley/Wifinian/releases/download/..%2f..%2fevil",
			"https://github.com/chikingsley/Wifinian/releases/download/..\\..\\evil",
			"https://github.com\\chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://github.com/evil/../chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://github.com//chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi?x=1",
			"https://github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi#x",
			"https://evil.com/?https://github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://evil.com/#https://github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://evil.com/https://github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			" https://github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi",
			"https://github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian Installer.msi",
			"https://github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi\r\nHost: evil.com",
		})
		{
			Assert.IsFalse(UpdateVerifier.IsAllowedUrl(url), $"Allowed: [{url}]");
		}
	}

	#endregion

	#region Hash

	[TestMethod]
	public void TestHash()
	{
		// SHA-256 of "abc" (FIPS 180-2)
		const string abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

		var hash = UpdateVerifier.ComputeHash(Encoding.ASCII.GetBytes("abc"));
		Assert.AreEqual(abc, UpdateVerifier.ToHex(hash));

		using (var ms = new MemoryStream(Encoding.ASCII.GetBytes("abc")))
			CollectionAssert.AreEqual(hash, UpdateVerifier.ComputeHash(ms));

		Assert.IsTrue(UpdateVerifier.IsHashMatched(abc, hash));
		Assert.IsTrue(UpdateVerifier.IsHashMatched(abc.ToUpperInvariant(), hash));
	}

	[TestMethod]
	public void TestHashMismatch()
	{
		const string abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

		var hash = UpdateVerifier.ComputeHash(Encoding.ASCII.GetBytes("abd"));

		Assert.IsFalse(UpdateVerifier.IsHashMatched(abc, hash));
		Assert.IsFalse(UpdateVerifier.IsHashMatched(null, hash));
		Assert.IsFalse(UpdateVerifier.IsHashMatched("", hash));
		Assert.IsFalse(UpdateVerifier.IsHashMatched(abc, null));
		Assert.IsFalse(UpdateVerifier.IsHashMatched(abc, Array.Empty<byte>()));

		// A part of the hash is not the hash.
		var own = UpdateVerifier.ToHex(hash);
		Assert.IsTrue(UpdateVerifier.IsHashMatched(own, hash));
		Assert.IsFalse(UpdateVerifier.IsHashMatched(own.Substring(0, 63), hash));
		Assert.IsFalse(UpdateVerifier.IsHashMatched(own + "0", hash));
		Assert.IsFalse(UpdateVerifier.IsHashMatched(" " + own.Substring(1), hash));
	}

	#endregion

	#region Signature

	[TestMethod]
	public void TestSignatureValid()
	{
		var hash = UpdateVerifier.ComputeHash(_data);

		Assert.IsTrue(UpdateVerifier.IsSignatureValid(hash, Sign(_data), _publicKey));
	}

	[TestMethod]
	public void TestSignatureInvalid()
	{
		var hash = UpdateVerifier.ComputeHash(_data);
		var signature = Sign(_data);

		// Other data
		var other = (byte[])_data.Clone();
		other[0] ^= 1;
		Assert.IsFalse(UpdateVerifier.IsSignatureValid(UpdateVerifier.ComputeHash(other), signature, _publicKey));

		// Signature over other data
		Assert.IsFalse(UpdateVerifier.IsSignatureValid(hash, Sign(other), _publicKey));

		// Signature changed by a bit
		var bytes = Convert.FromBase64String(signature);
		bytes[bytes.Length - 1] ^= 1;
		Assert.IsFalse(UpdateVerifier.IsSignatureValid(hash, Convert.ToBase64String(bytes), _publicKey));

		// Other key
		using (var rsa = new RSACng(2048))
		{
			var foreign = Convert.ToBase64String(rsa.SignData(_data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
			Assert.IsFalse(UpdateVerifier.IsSignatureValid(hash, foreign, _publicKey));
		}

		// Other padding
		var pss = Convert.ToBase64String(_rsa.SignData(_data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
		Assert.IsFalse(UpdateVerifier.IsSignatureValid(hash, pss, _publicKey));

		// The key of the releases did not make the signature of this test.
		Assert.IsFalse(UpdateVerifier.IsSignatureValid(hash, signature));
	}

	[TestMethod]
	public void TestSignatureGarbage()
	{
		var hash = UpdateVerifier.ComputeHash(_data);

		foreach (var signature in new[] { null, "", " ", "not base64 !!!", "AAAA", "====", Convert.ToBase64String(new byte[256]), Convert.ToBase64String(new byte[10000]), Sign(_data) + "A" })
			Assert.IsFalse(UpdateVerifier.IsSignatureValid(hash, signature, _publicKey), $"Valid: [{signature}]");

		// Hash of wrong length and key which cannot be read
		Assert.IsFalse(UpdateVerifier.IsSignatureValid(null, Sign(_data), _publicKey));
		Assert.IsFalse(UpdateVerifier.IsSignatureValid(new byte[20], Sign(_data), _publicKey));
		Assert.IsFalse(UpdateVerifier.IsSignatureValid(hash, Sign(_data), null));
		Assert.IsFalse(UpdateVerifier.IsSignatureValid(hash, Sign(_data), "<RSAKeyValue>"));
	}

	[TestMethod]
	public void TestPublicKey()
	{
		// The embedded key can be read and is RSA 3072.
		using var rsa = new RSACng();
		rsa.FromXmlString(UpdateVerifier.PublicKey);
		Assert.AreEqual(3072, rsa.KeySize);
	}

	#endregion

	#region Manifest

	private const string ValidUrl = "https://github.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi";

	private static string Compose(string version = "3.9.1", string url = ValidUrl, string sha256 = null, string signature = "AAAA", string notes = "Short text")
	{
		static string Field(string name, string value) => (value is null) ? null : $"\"{name}\":\"{value}\"";

		sha256 ??= new string('a', 64);

		return "{" + string.Join(",", new[]
		{
			Field("version", version),
			Field("url", url),
			Field("sha256", sha256),
			Field("signature", signature),
			Field("notes", notes)
		}.Where(x => x is not null)) + "}";
	}

	[TestMethod]
	public void TestManifestParse()
	{
		Assert.IsTrue(UpdateManifest.TryParse(Compose(), out UpdateManifest manifest, out string reason));
		Assert.IsNull(reason);
		Assert.AreEqual("3.9.1", manifest.Version);
		Assert.AreEqual(ValidUrl, manifest.Url);
		Assert.AreEqual(new string('a', 64), manifest.Sha256);
		Assert.AreEqual("AAAA", manifest.Signature);
		Assert.AreEqual("Short text", manifest.Notes);

		// Notes are optional, unknown fields are ignored and a byte order mark is harmless.
		Assert.IsTrue(UpdateManifest.TryParse("\uFEFF" + Compose(notes: null).Replace("{", "{\"other\":[1,{\"a\":null}],"), out manifest, out _));
		Assert.IsNull(manifest.Notes);
		Assert.AreEqual("3.9.1", manifest.Version);
	}

	[TestMethod]
	public void TestManifestMissing()
	{
		void AssertRefused(string json, string expectedReason)
		{
			Assert.IsFalse(UpdateManifest.TryParse(json, out UpdateManifest manifest, out string reason), $"Parsed: {json}");
			Assert.IsNull(manifest);
			Assert.AreEqual(expectedReason, reason);
		}

		AssertRefused(ComposeWithout("version"), "no version in manifest");
		AssertRefused(ComposeWithout("url"), "no url in manifest");
		AssertRefused(ComposeWithout("sha256"), "no sha256 in manifest");
		AssertRefused(ComposeWithout("signature"), "no signature in manifest");

		// Empty or null values are as good as missing.
		AssertRefused(Compose(version: ""), "no version in manifest");
		AssertRefused(Compose(url: " "), "no url in manifest");
		AssertRefused(Compose(signature: ""), "no signature in manifest");
		AssertRefused(Compose().Replace("\"version\":\"3.9.1\"", "\"version\":null"), "no version in manifest");

		AssertRefused(null, "empty manifest");
		AssertRefused("", "empty manifest");
		AssertRefused("{}", "no version in manifest");
		AssertRefused("[]", "no version in manifest"); // The serializer reads this as an object without fields.
		AssertRefused("null", "malformed manifest");
		AssertRefused("<html>Not Found</html>", "malformed manifest");
		AssertRefused("{\"version\":\"3.9.1\"", "no url in manifest"); // Cut short: the serializer takes what it got.
		AssertRefused("{\"version\":{\"a\":1},\"url\":\"x\"}", "malformed manifest");
	}

	private static string ComposeWithout(string name)
	{
		var json = name switch
		{
			"version" => Compose().Replace("\"version\":\"3.9.1\",", ""),
			"url" => Compose().Replace($"\"url\":\"{ValidUrl}\",", ""),
			"sha256" => Compose().Replace($"\"sha256\":\"{new string('a', 64)}\",", ""),
			"signature" => Compose().Replace("\"signature\":\"AAAA\",", ""),
			_ => throw new ArgumentException(name)
		};
		Assert.IsFalse(json.Contains($"\"{name}\""));
		return json;
	}

	[TestMethod]
	public void TestManifestCheck()
	{
		var running = new Version(3, 9, 0, 0);

		UpdateManifest Parse(string json)
		{
			Assert.IsTrue(UpdateManifest.TryParse(json, out UpdateManifest manifest, out _));
			return manifest;
		}

		// Newer
		Assert.IsTrue(UpdateVerifier.CheckManifest(Parse(Compose()), running, out bool isNewer, out string reason));
		Assert.IsTrue(isNewer);
		Assert.IsNull(reason);

		// The same or older is accepted as nothing to install.
		Assert.IsTrue(UpdateVerifier.CheckManifest(Parse(Compose(version: "3.9.0")), running, out isNewer, out _));
		Assert.IsFalse(isNewer);
		Assert.IsTrue(UpdateVerifier.CheckManifest(Parse(Compose(version: "3.8.0")), running, out isNewer, out _));
		Assert.IsFalse(isNewer);

		// Refused
		Assert.IsFalse(UpdateVerifier.CheckManifest(null, running, out isNewer, out _));
		Assert.IsFalse(isNewer);

		Assert.IsFalse(UpdateVerifier.CheckManifest(Parse(Compose(version: "v3.9.1")), running, out isNewer, out reason));
		Assert.IsFalse(isNewer);
		Assert.AreEqual("malformed version", reason);

		Assert.IsFalse(UpdateVerifier.CheckManifest(Parse(Compose(url: "https://github.com.evil.com/chikingsley/Wifinian/releases/download/v3.9.1/Wifinian.Installer.msi")), running, out _, out reason));
		Assert.AreEqual("URL not allowed", reason);

		Assert.IsFalse(UpdateVerifier.CheckManifest(Parse(Compose(sha256: "abc")), running, out _, out reason));
		Assert.AreEqual("malformed hash", reason);
		Assert.IsFalse(UpdateVerifier.CheckManifest(Parse(Compose(sha256: new string('g', 64))), running, out _, out reason));
		Assert.AreEqual("malformed hash", reason);
	}

	[TestMethod]
	public void TestFileCheck()
	{
		var hash = UpdateVerifier.ComputeHash(_data);

		UpdateManifest Parse(string sha256, string signature)
		{
			Assert.IsTrue(UpdateManifest.TryParse(Compose(sha256: sha256, signature: signature), out UpdateManifest manifest, out _));
			return manifest;
		}

		// The file which the manifest describes, signed
		Assert.IsTrue(UpdateVerifier.CheckFile(Parse(UpdateVerifier.ToHex(hash), Sign(_data)), hash, _publicKey, out string reason));
		Assert.IsNull(reason);

		// Another file than the manifest describes, although its signature is valid
		var other = Encoding.UTF8.GetBytes("Other bytes");
		Assert.IsFalse(UpdateVerifier.CheckFile(Parse(UpdateVerifier.ToHex(hash), Sign(other)), UpdateVerifier.ComputeHash(other), _publicKey, out reason));
		Assert.AreEqual("hash mismatch", reason);

		// The hash matches but the signature is of other data, garbage, or by another key.
		Assert.IsFalse(UpdateVerifier.CheckFile(Parse(UpdateVerifier.ToHex(hash), Sign(other)), hash, _publicKey, out reason));
		Assert.AreEqual("invalid signature", reason);
		Assert.IsFalse(UpdateVerifier.CheckFile(Parse(UpdateVerifier.ToHex(hash), "garbage"), hash, _publicKey, out reason));
		Assert.AreEqual("invalid signature", reason);
		Assert.IsFalse(UpdateVerifier.CheckFile(Parse(UpdateVerifier.ToHex(hash), Sign(_data)), hash, out reason));
		Assert.AreEqual("invalid signature", reason);

		Assert.IsFalse(UpdateVerifier.CheckFile(null, hash, _publicKey, out reason));
		Assert.AreEqual("hash mismatch", reason);
	}

	#endregion

	#region Schedule and helper

	[TestMethod]
	public void TestCheckDue()
	{
		var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

		// First check after start
		Assert.IsTrue(AppController.IsUpgradeCheckDue(true, false, false, default, false, now));

		// Never when turned off, while a check or an installation is running, or during a test
		Assert.IsFalse(AppController.IsUpgradeCheckDue(false, false, false, default, false, now));
		Assert.IsFalse(AppController.IsUpgradeCheckDue(true, true, false, default, false, now));
		Assert.IsFalse(AppController.IsUpgradeCheckDue(true, false, true, default, false, now));

		// Once a day
		Assert.IsFalse(AppController.IsUpgradeCheckDue(true, false, false, now.AddHours(-23.9), false, now));
		Assert.IsTrue(AppController.IsUpgradeCheckDue(true, false, false, now.AddHours(-24), false, now));

		// After a failure, an hour later
		Assert.IsFalse(AppController.IsUpgradeCheckDue(true, false, false, now.AddMinutes(-59), true, now));
		Assert.IsTrue(AppController.IsUpgradeCheckDue(true, false, false, now.AddMinutes(-60), true, now));
	}

	[TestMethod]
	public void TestHelper()
	{
		var script = UpdateService.ComposeHelper(1234, "Wifinian.exe",
			@"C:\Users\a%b\AppData\Local\Wifinian\update\Wifinian.Installer.msi",
			@"C:\Users\a%b\AppData\Local\Wifinian\update\result.txt",
			@"HKCU\Software\Wifinian\Install",
			@"C:\Users\a%b\AppData\Local\Programs\Wifinian\Wifinian.exe",
			@"C:\Program Files (x86)\Copy\Wifinian.exe");

		var lines = script.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries);

		const string startRemembered = "start \"\" \"%folder%%image%\"";
		const string startInstalled = "start \"\" \"C:\\Users\\a%%b\\AppData\\Local\\Programs\\Wifinian\\Wifinian.exe\"";
		const string startRunning = "start \"\" \"C:\\Program Files (x86)\\Copy\\Wifinian.exe\"";

		// The process is waited for before the installer is run, and the application is started after it.
		var waitIndex = Array.FindIndex(lines, x => x.Contains("\"PID eq 1234\"") && x.Contains("\"IMAGENAME eq Wifinian.exe\""));
		var installIndex = Array.FindIndex(lines, x => x.Contains("msiexec.exe /i \"C:\\Users\\a%%b\\AppData\\Local\\Wifinian\\update\\Wifinian.Installer.msi\" /passive /norestart"));
		var startIndex = Array.FindIndex(lines, x => x.StartsWith("start \"\" \""));

		Assert.IsTrue(0 <= waitIndex);
		Assert.IsTrue(waitIndex < installIndex);
		Assert.IsTrue(installIndex < startIndex);
		Assert.AreEqual(1, lines.Count(x => x.Contains("msiexec")));

		// The exit code of the installer is kept before any other command changes it.
		Assert.AreEqual("set code=%errorlevel%", lines[installIndex + 1]);
		Assert.AreEqual(">\"C:\\Users\\a%%b\\AppData\\Local\\Wifinian\\update\\result.txt\" echo %code%", lines[installIndex + 2]);

		// The installed folder is asked of the registry after the installer has run.
		var queryIndex = Array.FindIndex(lines, x => x.Contains("reg.exe query \"HKCU\\Software\\Wifinian\\Install\" /v InstallDir"));
		Assert.IsTrue(installIndex < queryIndex);
		Assert.IsTrue(queryIndex < startIndex);

		// When the installer failed, the executable of this process is started first.
		Assert.AreEqual(startRunning, lines[startIndex]);

		// When it succeeded, the installed executable comes before the one of this process, so
		// that a copy in another folder does not start its old self again.
		var installedIndex = Array.IndexOf(lines, ":installed");
		Assert.IsTrue(startIndex < installedIndex);
		Assert.IsTrue(Array.IndexOf(lines, "if \"%code%\"==\"0\" goto installed") < startIndex);

		var starts = lines.Skip(installedIndex).Where(x => x.StartsWith("start \"\" \"")).ToArray();
		CollectionAssert.AreEqual(new[] { startRemembered, startInstalled, startRunning }, starts);

		// Each start is only made if the file exists and ends the script.
		for (int i = 0; i < lines.Length; i++)
		{
			if (!lines[i].StartsWith("start \"\" \""))
				continue;

			Assert.IsTrue(lines[i - 1].StartsWith("if not exist " + lines[i].Substring("start \"\" ".Length) + " goto next"));
			Assert.AreEqual("goto end", lines[i + 1]);
		}

		// Every label which is jumped to exists once.
		foreach (var label in lines.Select(x => x.Split(' ')).Where(x => (x.Length >= 2) && (x[x.Length - 2] == "goto")).Select(x => x.Last()).Distinct())
			Assert.AreEqual(1, lines.Count(x => x == ":" + label), $"Label: {label}");

		// Nothing in the script asks for elevation or a silent installation.
		Assert.IsFalse(script.Contains("runas"));
		Assert.IsFalse(script.Contains("/qn"));
	}

	#endregion

	#region Installer package

	[DllImport("Msi.dll", CharSet = CharSet.Unicode)]
	private static extern uint MsiOpenDatabase(string szDatabasePath, IntPtr szPersist, out uint phDatabase);

	[DllImport("Msi.dll", CharSet = CharSet.Unicode)]
	private static extern uint MsiDatabaseOpenView(uint hDatabase, string szQuery, out uint phView);

	[DllImport("Msi.dll")]
	private static extern uint MsiViewExecute(uint hView, uint hRecord);

	[DllImport("Msi.dll")]
	private static extern uint MsiDatabaseCommit(uint hDatabase);

	[DllImport("Msi.dll")]
	private static extern uint MsiCloseHandle(uint hAny);

	private static readonly IntPtr MSIDBOPEN_CREATE = new(3);

	// Makes a package which holds nothing but the table of properties. It cannot install anything.
	private static void CreatePackage(string filePath, params string[] queries)
	{
		Assert.AreEqual(0U, MsiOpenDatabase(filePath, MSIDBOPEN_CREATE, out uint database));
		try
		{
			foreach (var query in queries)
			{
				Assert.AreEqual(0U, MsiDatabaseOpenView(database, query, out uint view), query);
				try
				{
					Assert.AreEqual(0U, MsiViewExecute(view, 0), query);
				}
				finally
				{
					MsiCloseHandle(view);
				}
			}
			Assert.AreEqual(0U, MsiDatabaseCommit(database));
		}
		finally
		{
			MsiCloseHandle(database);
		}
	}

	[TestMethod]
	public void TestPackageVersion()
	{
		const string createTable = "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR NOT NULL LOCALIZABLE PRIMARY KEY `Property`)";

		var folderPath = Path.Combine(Path.GetTempPath(), "WifinianTest-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folderPath);
		try
		{
			// The version is read from the package, among other properties.
			var filePath = Path.Combine(folderPath, "version.msi");
			CreatePackage(filePath, createTable,
				"INSERT INTO `Property` (`Property`, `Value`) VALUES ('ProductName', 'Test')",
				"INSERT INTO `Property` (`Property`, `Value`) VALUES ('ProductVersion', '3.9.1')",
				"INSERT INTO `Property` (`Property`, `Value`) VALUES ('ProductVersionOther', '9.9.9')");
			Assert.AreEqual("3.9.1", InstallerPackage.GetProductVersion(filePath));

			// The package is not kept open.
			File.Delete(filePath);
			Assert.IsFalse(File.Exists(filePath));

			// A package without the version or without the table
			filePath = Path.Combine(folderPath, "name.msi");
			CreatePackage(filePath, createTable,
				"INSERT INTO `Property` (`Property`, `Value`) VALUES ('ProductName', 'Test')");
			Assert.IsNull(InstallerPackage.GetProductVersion(filePath));

			filePath = Path.Combine(folderPath, "empty.msi");
			CreatePackage(filePath);
			Assert.IsNull(InstallerPackage.GetProductVersion(filePath));

			// A file which is not a package, no file, no path
			filePath = Path.Combine(folderPath, "text.msi");
			File.WriteAllText(filePath, "This is not an installer.");
			Assert.IsNull(InstallerPackage.GetProductVersion(filePath));
			Assert.IsNull(InstallerPackage.GetProductVersion(Path.Combine(folderPath, "none.msi")));
			Assert.IsNull(InstallerPackage.GetProductVersion(null));
			Assert.IsNull(InstallerPackage.GetProductVersion(""));
		}
		finally
		{
			Directory.Delete(folderPath, true);
		}
	}

	[TestMethod]
	public void TestProductCheck()
	{
		var running = new Version(3, 9, 0, 0);

		UpdateManifest Parse(string version)
		{
			Assert.IsTrue(UpdateManifest.TryParse(Compose(version: version), out UpdateManifest manifest, out _));
			return manifest;
		}

		// The installer is of the version which the manifest names and that is newer.
		Assert.IsTrue(UpdateVerifier.CheckProduct(Parse("3.9.1"), "3.9.1", running, out string reason));
		Assert.IsNull(reason);

		// A signed installer of an older release under a higher version in the manifest
		Assert.IsFalse(UpdateVerifier.CheckProduct(Parse("99.0.0"), "3.9.1", running, out reason));
		Assert.AreEqual("version mismatch", reason);
		Assert.IsFalse(UpdateVerifier.CheckProduct(Parse("99.0.0"), "3.9.0", running, out reason));
		Assert.AreEqual("version mismatch", reason);
		Assert.IsFalse(UpdateVerifier.CheckProduct(Parse("3.9.1"), "3.9.2", running, out reason));
		Assert.AreEqual("version mismatch", reason);

		// The installer of the running or an older version, named as it is
		Assert.IsFalse(UpdateVerifier.CheckProduct(Parse("3.9.0"), "3.9.0", running, out reason));
		Assert.AreEqual("version not newer", reason);
		Assert.IsFalse(UpdateVerifier.CheckProduct(Parse("3.8.0"), "3.8.0", running, out reason));
		Assert.AreEqual("version not newer", reason);

		// The version of the installer could not be read or is not of three numbers.
		foreach (var productVersion in new[] { null, "", "3.9", "3.9.1.0", "v3.9.1", " 3.9.1" })
		{
			Assert.IsFalse(UpdateVerifier.CheckProduct(Parse("3.9.1"), productVersion, running, out reason), $"Accepted: [{productVersion}]");
			Assert.AreEqual("installer version unreadable", reason);
		}

		Assert.IsFalse(UpdateVerifier.CheckProduct(null, "3.9.1", running, out reason));
		Assert.AreEqual("version mismatch", reason);
	}

	#endregion
}
