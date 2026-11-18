using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Wifinian.Models.Update;

/// <summary>
/// Description of the latest release (latest.json of the release)
/// </summary>
[DataContract]
internal class UpdateManifest
{
	/// <summary>
	/// Version of the release (e.g. 3.9.1)
	/// </summary>
	[DataMember(Name = "version")]
	public string Version { get; set; }

	/// <summary>
	/// URL of the installer
	/// </summary>
	[DataMember(Name = "url")]
	public string Url { get; set; }

	/// <summary>
	/// SHA-256 of the installer in hexadecimal
	/// </summary>
	[DataMember(Name = "sha256")]
	public string Sha256 { get; set; }

	/// <summary>
	/// Signature (RSA, PKCS#1 v1.5, SHA-256) over the bytes of the installer in Base64
	/// </summary>
	[DataMember(Name = "signature")]
	public string Signature { get; set; }

	/// <summary>
	/// Short text on the release (optional)
	/// </summary>
	[DataMember(Name = "notes")]
	public string Notes { get; set; }

	/// <summary>
	/// Attempts to parse the content of a manifest.
	/// </summary>
	/// <param name="json">Content in JSON</param>
	/// <param name="manifest">Manifest if successfully parsed</param>
	/// <param name="reason">Short reason if not</param>
	/// <returns>True if the content is JSON and has all the required fields</returns>
	/// <remarks>
	/// Only the presence of the fields is checked here. Their values are checked by
	/// <see cref="UpdateVerifier"/>. This method never throws an exception.
	/// </remarks>
	public static bool TryParse(string json, out UpdateManifest manifest, out string reason)
	{
		manifest = null;

		if (string.IsNullOrWhiteSpace(json))
		{
			reason = "empty manifest";
			return false;
		}

		UpdateManifest buffer;
		try
		{
			// A byte order mark is not expected but is harmless.
			using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json.TrimStart('﻿')));
			var serializer = new DataContractJsonSerializer(typeof(UpdateManifest));
			buffer = serializer.ReadObject(ms) as UpdateManifest;
		}
		catch (Exception)
		{
			reason = "malformed manifest";
			return false;
		}

		if (buffer is null)
		{
			reason = "malformed manifest";
			return false;
		}

		if (string.IsNullOrWhiteSpace(buffer.Version))
		{
			reason = "no version in manifest";
			return false;
		}
		if (string.IsNullOrWhiteSpace(buffer.Url))
		{
			reason = "no url in manifest";
			return false;
		}
		if (string.IsNullOrWhiteSpace(buffer.Sha256))
		{
			reason = "no sha256 in manifest";
			return false;
		}
		if (string.IsNullOrWhiteSpace(buffer.Signature))
		{
			reason = "no signature in manifest";
			return false;
		}

		manifest = buffer;
		reason = null;
		return true;
	}
}
