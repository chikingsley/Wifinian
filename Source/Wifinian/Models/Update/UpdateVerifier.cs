using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Wifinian.Models.Update;

/// <summary>
/// Checks which an update must pass before it is installed
/// </summary>
/// <remarks>The methods are pure and never throw an exception.</remarks>
internal static class UpdateVerifier
{
	/// <summary>
	/// Public key (RSA 3072) of the releases of this fork
	/// </summary>
	/// <remarks>The private key is kept by the publisher and is never in the repository.</remarks>
	internal const string PublicKey = "<RSAKeyValue><Modulus>rfu++2PL+GcsPDMCc7ND6CkEY9LOt0OsgVsoF832tnPjEIMLioZDnCBQbux89ndjiUcAUD4AWkDj3hc5377X7T3xgyUFT3FYqKsgQqHjhJVxy90bMkkkewTAaabZ15q1EUVQRZ6k+UgF9kPbwyoM9yGN6Ao5Xt7xKyvuy8f7GWxsRzpu7tFneDC9YooSdI9V14ifxwogFM7C59txrRCiAxcRKxtZXwoH34+iEkbMmGCd7bjPa2CQz6H4vnf7ktT4Tx6bIeHQZbaTG7RJsndOuzw2JI75OazqjOJCFSvmgiSm8pWLr0hXhgLGeOyTHhA0W1hCpy20juM8JTVYMT383ra9rj7TT9l7lHBS2WrQMgJe79Fv/ttUiNZUhyCsZ/COjnedEdiErT2uIyUMqUtNW6ZiOlitmW22rbligGVkWviV68L/RIQAZhCmAGtJ/HNC6cTVyRz2U52h/FCL/Fj8+0t44F18kwHi9TAZExRqNZpBqIV0maMhlN+hY2H6utBl</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

	/// <summary>
	/// URL of the manifest of the latest release
	/// </summary>
	internal const string ManifestUrl = "https://github.com/chikingsley/Wifinian/releases/latest/download/latest.json";

	private const string AllowedHost = "github.com";
	private const string AllowedPathStart = "/chikingsley/Wifinian/releases/download/";

	#region Version

	/// <summary>
	/// Attempts to parse a version of a release (three numbers, e.g. 3.9.1).
	/// </summary>
	/// <param name="source">Source string</param>
	/// <param name="version">Version of three components if successfully parsed</param>
	public static bool TryParseVersion(string source, out Version version)
	{
		version = null;

		if (string.IsNullOrEmpty(source))
			return false;

		var parts = source.Split('.');
		if (parts.Length != 3)
			return false;

		var numbers = new int[3];
		for (int i = 0; i < parts.Length; i++)
		{
			// Signs, spaces and other forms which int.Parse accepts are not accepted.
			if ((parts[i].Length is 0 or > 6) || !parts[i].All(x => x is >= '0' and <= '9'))
				return false;

			numbers[i] = int.Parse(parts[i]);
		}

		version = new Version(numbers[0], numbers[1], numbers[2]);
		return true;
	}

	/// <summary>
	/// Determines whether the version of a release is newer than the running version.
	/// </summary>
	/// <param name="candidate">Version of a release</param>
	/// <param name="running">Running version</param>
	/// <returns>True if newer. False if the same, older or malformed.</returns>
	/// <remarks>
	/// Only the first three components are compared. The fourth of the running version
	/// (3.9.0.0) would otherwise make it newer than the same version of three (3.9.0).
	/// </remarks>
	public static bool IsNewer(string candidate, Version running)
	{
		if (running is null)
			return false;

		if (!TryParseVersion(candidate, out Version version))
			return false;

		return version > Normalize(running);
	}

	/// <summary>
	/// Reduces a version to its first three components.
	/// </summary>
	public static Version Normalize(Version version) =>
		new(version.Major, version.Minor, Math.Max(0, version.Build));

	#endregion

	#region URL

	/// <summary>
	/// Determines whether a URL of an installer is one of the release assets of this fork.
	/// </summary>
	/// <param name="url">URL in the manifest</param>
	/// <returns>True if allowed</returns>
	public static bool IsAllowedUrl(string url)
	{
		if (string.IsNullOrEmpty(url))
			return false;

		// Anything by which a path could be read differently from how it is written (dot
		// segments, escaped characters, backslashes), a query and a fragment are not accepted
		// as written, before the URL is parsed. Neither are characters other than printable
		// ASCII, which may look like or be converted to other characters.
		if (url.Contains("..") || (0 <= url.IndexOfAny(['%', '\\', '?', '#', '@'])) || url.Any(x => x is <= ' ' or > '~'))
			return false;

		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
			return false;

		if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
			return false;

		if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
			return false;

		if (!string.Equals(uri.Host, AllowedHost, StringComparison.OrdinalIgnoreCase))
			return false;

		if (!uri.AbsolutePath.StartsWith(AllowedPathStart, StringComparison.Ordinal))
			return false;

		// The path as parsed must be the path as written.
		return url.EndsWith(uri.AbsolutePath, StringComparison.Ordinal)
			&& (uri.AbsolutePath.Length > AllowedPathStart.Length);
	}

	#endregion

	#region Hash

	/// <summary>
	/// Computes SHA-256 of data.
	/// </summary>
	public static byte[] ComputeHash(byte[] data)
	{
		using var sha = SHA256.Create();
		return sha.ComputeHash(data ?? Array.Empty<byte>());
	}

	/// <summary>
	/// Computes SHA-256 of a stream from its current position to its end.
	/// </summary>
	public static byte[] ComputeHash(Stream stream)
	{
		using var sha = SHA256.Create();
		return sha.ComputeHash(stream);
	}

	/// <summary>
	/// Converts a hash to lowercase hexadecimal.
	/// </summary>
	public static string ToHex(byte[] hash)
	{
		if (hash is null)
			return string.Empty;

		var buffer = new StringBuilder(hash.Length * 2);
		foreach (var b in hash)
			buffer.Append(b.ToString("x2"));

		return buffer.ToString();
	}

	/// <summary>
	/// Determines whether a hash is the one given in the manifest.
	/// </summary>
	/// <param name="expected">SHA-256 in hexadecimal in the manifest</param>
	/// <param name="hash">SHA-256 computed from the file</param>
	public static bool IsHashMatched(string expected, byte[] hash)
	{
		if ((hash is not { Length: 32 }) || (expected is not { Length: 64 }))
			return false;

		return string.Equals(expected, ToHex(hash), StringComparison.OrdinalIgnoreCase);
	}

	#endregion

	#region Signature

	/// <summary>
	/// Determines whether a signature over data is valid with the public key of this fork.
	/// </summary>
	/// <param name="hash">SHA-256 of the data</param>
	/// <param name="signature">Signature in Base64</param>
	public static bool IsSignatureValid(byte[] hash, string signature) =>
		IsSignatureValid(hash, signature, PublicKey);

	/// <summary>
	/// Determines whether a signature (RSA, PKCS#1 v1.5, SHA-256) over data is valid.
	/// </summary>
	/// <param name="hash">SHA-256 of the data</param>
	/// <param name="signature">Signature in Base64</param>
	/// <param name="publicKey">Public key in the XML format of .NET</param>
	/// <returns>True if valid. False if not valid or if any of the arguments is malformed.</returns>
	public static bool IsSignatureValid(byte[] hash, string signature, string publicKey)
	{
		if ((hash is not { Length: 32 }) || string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(publicKey))
			return false;

		try
		{
			var signatureBytes = Convert.FromBase64String(signature);

			using var rsa = new RSACng();
			rsa.FromXmlString(publicKey);

			return rsa.VerifyHash(hash, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		}
		catch (Exception)
		{
			// Base64 or key which cannot be read
			return false;
		}
	}

	#endregion

	/// <summary>
	/// Determines whether a manifest offers an update which may be downloaded.
	/// </summary>
	/// <param name="manifest">Manifest</param>
	/// <param name="running">Running version</param>
	/// <param name="isNewer">Whether the version of the manifest is newer than the running version</param>
	/// <param name="reason">Short reason if the manifest is refused</param>
	/// <returns>True if the manifest is acceptable (whether newer or not)</returns>
	public static bool CheckManifest(UpdateManifest manifest, Version running, out bool isNewer, out string reason)
	{
		isNewer = false;

		if (manifest is null)
		{
			reason = "no manifest";
			return false;
		}
		if (!TryParseVersion(manifest.Version, out _))
		{
			reason = "malformed version";
			return false;
		}

		isNewer = IsNewer(manifest.Version, running);
		if (!isNewer)
		{
			reason = null;
			return true;
		}

		if (!IsAllowedUrl(manifest.Url))
		{
			reason = "URL not allowed";
			return false;
		}
		if ((manifest.Sha256.Length != 64) || !manifest.Sha256.All(Uri.IsHexDigit))
		{
			reason = "malformed hash";
			return false;
		}

		reason = null;
		return true;
	}

	/// <summary>
	/// Determines whether a downloaded installer is the one the manifest describes and is signed
	/// by the publisher of this fork.
	/// </summary>
	/// <param name="manifest">Manifest</param>
	/// <param name="hash">SHA-256 of the downloaded file</param>
	/// <param name="reason">Short reason if the file is refused</param>
	public static bool CheckFile(UpdateManifest manifest, byte[] hash, out string reason) =>
		CheckFile(manifest, hash, PublicKey, out reason);

	internal static bool CheckFile(UpdateManifest manifest, byte[] hash, string publicKey, out string reason)
	{
		if (!IsHashMatched(manifest?.Sha256, hash))
		{
			reason = "hash mismatch";
			return false;
		}
		if (!IsSignatureValid(hash, manifest.Signature, publicKey))
		{
			reason = "invalid signature";
			return false;
		}

		reason = null;
		return true;
	}

	/// <summary>
	/// Determines whether a verified installer installs the version which the manifest names.
	/// </summary>
	/// <param name="manifest">Manifest</param>
	/// <param name="productVersion">Version of the product read from the installer, null if it could not be read</param>
	/// <param name="running">Running version</param>
	/// <param name="reason">Short reason if the installer is refused</param>
	/// <remarks>
	/// The signature covers the installer but not the manifest. Without this check, the installer
	/// of an old release could be offered again under a higher version in the manifest.
	/// </remarks>
	public static bool CheckProduct(UpdateManifest manifest, string productVersion, Version running, out string reason)
	{
		if (!TryParseVersion(productVersion, out Version product))
		{
			reason = "installer version unreadable";
			return false;
		}
		if (!TryParseVersion(manifest?.Version, out Version named) || (product != named))
		{
			reason = "version mismatch";
			return false;
		}
		if (!IsNewer(productVersion, running))
		{
			reason = "version not newer";
			return false;
		}

		reason = null;
		return true;
	}
}
