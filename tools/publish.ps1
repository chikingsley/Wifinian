<#
.SYNOPSIS
Builds, signs and publishes a release of this fork that the installed apps pick up as an update.

.DESCRIPTION
Sets the version, builds and tests, builds the installer (.msi), signs it with the private key
of the publisher, writes latest.json, copies the installer to the Documents folder and, unless
-DryRun is given, commits, tags, pushes and creates the release on GitHub with both assets.

Works with Windows PowerShell 5.1. This file is kept in ASCII.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Version 3.9.1 -Notes "what changed"

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Version 3.9.1 -Notes "what changed" -DryRun
#>
[CmdletBinding()]
param(
	# Version to release, of three numbers (3.9.1)
	[string]$Version,

	# Short text on what changed, shown by the release and kept in latest.json
	[string]$Notes = '',

	# Does everything up to the copy to Documents, then restores the version in the working tree.
	# Nothing is committed, tagged or pushed and GitHub is not contacted.
	[switch]$DryRun,

	# Skips the tests (the Debug build is still made)
	[switch]$SkipTests,

	# Only defines the functions (as dot-sourcing does), for testing them
	[switch]$NoRun
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$Owner = 'chikingsley'
$Repo = 'Wifinian'
$MsiName = 'Wifinian.Installer.msi'
$ManifestName = 'latest.json'
$ApiRoot = "https://api.github.com/repos/$Owner/$Repo"
$UploadRoot = "https://uploads.github.com/repos/$Owner/$Repo"

#region Version

# Determines whether a text is a version of three numbers without leading zeros, within the
# range Windows Installer accepts (major and minor up to 255, build up to 65535).
function Test-ReleaseVersion([string]$Text) {
	if ($Text -notmatch '^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})$') { return $false }
	return (([int]$Matches[1] -le 255) -and ([int]$Matches[2] -le 255) -and ([int]$Matches[3] -le 65535))
}

# Compares two versions of three numbers: -1, 0 or 1 as the first is older, equal or newer.
function Compare-ReleaseVersion([string]$Left, [string]$Right) {
	return ([version]$Left).CompareTo([version]$Right)
}

# Reads a text file keeping note of whether it starts with the byte order mark of UTF-8.
function Read-TextFile([string]$Path) {
	$bytes = [System.IO.File]::ReadAllBytes($Path)
	$hasBom = (($bytes.Length -ge 3) -and ($bytes[0] -eq 0xEF) -and ($bytes[1] -eq 0xBB) -and ($bytes[2] -eq 0xBF))
	$offset = 0
	if ($hasBom) { $offset = 3 }
	$text = (New-Object System.Text.UTF8Encoding($false)).GetString($bytes, $offset, $bytes.Length - $offset)
	return New-Object PSObject -Property @{ Text = $text; HasBom = $hasBom }
}

# Writes a text file as UTF-8, with the byte order mark only if asked. Line ends are kept as given.
function Write-TextFile([string]$Path, [string]$Text, [bool]$WithBom) {
	[System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($WithBom)))
}

# Replaces what a pattern finds in a text and fails unless it finds it exactly as often as expected.
function Set-TextByPattern([string]$Text, [string]$Pattern, [string]$Replacement, [int]$Expected, [string]$What) {
	$regex = New-Object System.Text.RegularExpressions.Regex($Pattern)
	$count = $regex.Matches($Text).Count
	if ($count -ne $Expected) { throw "$What`: expected $Expected place(s) for the version but found $count." }
	return $regex.Replace($Text, $Replacement)
}

# Gets the version (three numbers) from the text of an AssemblyInfo.cs.
function Get-AssemblyInfoVersion([string]$Text) {
	$match = [regex]::Match($Text, '(?m)^\[assembly:\s*AssemblyVersion\("(\d+\.\d+\.\d+)\.\d+"\)\]')
	if (-not $match.Success) { throw 'AssemblyVersion was not found.' }
	return $match.Groups[1].Value
}

# Sets AssemblyVersion and AssemblyFileVersion in the text of an AssemblyInfo.cs.
function Set-AssemblyInfoVersion([string]$Text, [string]$NewVersion) {
	return Set-TextByPattern $Text '(?m)^(\[assembly:\s*Assembly(?:File)?Version\(")\d+\.\d+\.\d+\.\d+("\)\])' ('${1}' + $NewVersion + '.0${2}') 2 'AssemblyInfo.cs'
}

# Sets the version of the Package element in the text of Product.wxs.
function Set-WxsVersion([string]$Text, [string]$NewVersion) {
	return Set-TextByPattern $Text '(?s)(<Package\b[^>]*?\sVersion=")\d+\.\d+\.\d+(")' ('${1}' + $NewVersion + '${2}') 1 'Product.wxs'
}

# Gets the public key built into the app from the text of UpdateVerifier.cs.
function Get-EmbeddedPublicKey([string]$Text) {
	$match = [regex]::Match($Text, 'const\s+string\s+PublicKey\s*=\s*"(<RSAKeyValue>[^"]+</RSAKeyValue>)"')
	if (-not $match.Success) { throw 'The public key was not found in UpdateVerifier.cs.' }
	return $match.Groups[1].Value
}

# Gets the version of the product (ProductVersion) from an installer (.msi), which is opened
# as a database for reading only. The app refuses an update unless this is the version in
# latest.json.
function Get-MsiProductVersion([string]$Path) {
	$flags = [System.Reflection.BindingFlags]
	$installer = New-Object -ComObject WindowsInstaller.Installer
	$database = $null
	$view = $null
	$record = $null
	try {
		$database = $installer.GetType().InvokeMember('OpenDatabase', $flags::InvokeMethod, $null, $installer, @($Path, 0))
		$view = $database.GetType().InvokeMember('OpenView', $flags::InvokeMethod, $null, $database, @("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'ProductVersion'"))
		$view.GetType().InvokeMember('Execute', $flags::InvokeMethod, $null, $view, $null) | Out-Null
		$record = $view.GetType().InvokeMember('Fetch', $flags::InvokeMethod, $null, $view, $null)
		if ($null -eq $record) { throw "ProductVersion was not found in $Path." }
		return [string]$record.GetType().InvokeMember('StringData', $flags::GetProperty, $null, $record, @(1))
	}
	finally {
		# The file stays open until the objects are released.
		foreach ($object in @($record, $view, $database, $installer)) {
			if ($null -ne $object) { [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($object) | Out-Null }
		}
	}
}

#endregion

#region Hash, signature and manifest

# Computes SHA-256 of a file.
function Get-FileSha256([string]$Path) {
	$sha = [System.Security.Cryptography.SHA256]::Create()
	$stream = [System.IO.File]::OpenRead($Path)
	try { return ,$sha.ComputeHash($stream) }
	finally { $stream.Dispose(); $sha.Dispose() }
}

function ConvertTo-HexText([byte[]]$Bytes) {
	return ([System.BitConverter]::ToString($Bytes) -replace '-', '').ToLowerInvariant()
}

# Signs SHA-256 of data (RSA, PKCS#1 v1.5) with a private key in the XML format of .NET and
# returns the signature in Base64. The key is used in memory only.
function New-HashSignature([byte[]]$Hash, [string]$PrivateKeyXml) {
	$rsa = New-Object System.Security.Cryptography.RSACng
	try {
		$rsa.FromXmlString($PrivateKeyXml)
		$signature = $rsa.SignHash($Hash, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
		return [System.Convert]::ToBase64String($signature)
	}
	finally { $rsa.Dispose() }
}

# Determines whether a signature is valid, in the way the app does (UpdateVerifier.IsSignatureValid).
function Test-HashSignature([byte[]]$Hash, [string]$Signature, [string]$PublicKeyXml) {
	if (($null -eq $Hash) -or ($Hash.Length -ne 32) -or [string]::IsNullOrWhiteSpace($Signature) -or [string]::IsNullOrWhiteSpace($PublicKeyXml)) { return $false }
	try {
		$signatureBytes = [System.Convert]::FromBase64String($Signature)
		$rsa = New-Object System.Security.Cryptography.RSACng
		try {
			$rsa.FromXmlString($PublicKeyXml)
			return $rsa.VerifyHash($Hash, $signatureBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
		}
		finally { $rsa.Dispose() }
	}
	catch { return $false }
}

# Gets the URL of an asset of a release.
function Get-AssetUrl([string]$ReleaseVersion, [string]$Name) {
	return "https://github.com/$Owner/$Repo/releases/download/v$ReleaseVersion/$Name"
}

# Composes the text of latest.json.
function New-ManifestText([string]$ReleaseVersion, [string]$Url, [string]$Sha256, [string]$Signature, [string]$ReleaseNotes) {
	$manifest = New-Object System.Collections.Specialized.OrderedDictionary
	$manifest.Add('version', $ReleaseVersion)
	$manifest.Add('url', $Url)
	$manifest.Add('sha256', $Sha256)
	$manifest.Add('signature', $Signature)
	$manifest.Add('notes', $ReleaseNotes)
	return (ConvertTo-Json -InputObject $manifest -Compress)
}

# Writes latest.json (UTF-8 without the byte order mark) and reads it back to make sure that
# it says what was meant.
function Write-Manifest([string]$Path, [string]$ReleaseVersion, [string]$Url, [string]$Sha256, [string]$Signature, [string]$ReleaseNotes) {
	$text = New-ManifestText $ReleaseVersion $Url $Sha256 $Signature $ReleaseNotes
	Write-TextFile $Path $text $false

	$read = Read-TextFile $Path
	if ($read.HasBom) { throw 'latest.json was written with a byte order mark.' }
	$back = ConvertFrom-Json -InputObject $read.Text
	if (($back.version -cne $ReleaseVersion) -or ($back.url -cne $Url) -or ($back.sha256 -cne $Sha256) -or ($back.signature -cne $Signature) -or ($back.notes -cne $ReleaseNotes)) {
		throw 'latest.json does not read back as written.'
	}
}

#endregion

#region Commands

# Runs a program and fails if its exit code is not 0. Its output goes to the console.
function Invoke-Tool([string]$What, [string]$File, [string[]]$Arguments) {
	Write-Host ">> $What" -ForegroundColor Cyan
	$previous = $ErrorActionPreference
	$ErrorActionPreference = 'Continue'
	try { & $File @Arguments | Out-Host }
	finally { $ErrorActionPreference = $previous }
	if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

# Runs git and returns its output as lines. Fails if the exit code is not 0 unless told otherwise.
function Invoke-Git([string[]]$Arguments, [switch]$AllowFailure) {
	$previous = $ErrorActionPreference
	$ErrorActionPreference = 'Continue'
	try { $output = @(& git -C $script:RepoRoot @Arguments) }
	finally { $ErrorActionPreference = $previous }
	if (($LASTEXITCODE -ne 0) -and (-not $AllowFailure)) { throw "git $($Arguments -join ' ') failed (exit code $LASTEXITCODE)." }
	return ,$output
}

# Gets the commit a tag points at, or null if there is no such tag in this repository.
function Get-TagCommit([string]$Tag) {
	$output = Invoke-Git @('rev-parse', '-q', '--verify', "refs/tags/$Tag^{commit}") -AllowFailure
	if (($LASTEXITCODE -ne 0) -or ($output.Count -eq 0)) { return $null }
	return $output[0].Trim()
}

#endregion

#region GitHub

# Gets the token for github.com from the credential helper of git. The token is returned to the
# caller only and is never written anywhere.
function Get-GitHubToken {
	$info = New-Object System.Diagnostics.ProcessStartInfo
	$info.FileName = 'git'
	$info.Arguments = 'credential fill'
	$info.UseShellExecute = $false
	$info.RedirectStandardInput = $true
	$info.RedirectStandardOutput = $true
	$info.CreateNoWindow = $false
	$info.EnvironmentVariables['GIT_TERMINAL_PROMPT'] = '0'

	$process = [System.Diagnostics.Process]::Start($info)
	try {
		# Line feeds only: git refuses values which contain a carriage return.
		$process.StandardInput.Write("protocol=https`nhost=github.com`n`n")
		$process.StandardInput.Close()
		$output = $process.StandardOutput.ReadToEnd()
		$process.WaitForExit()
		if ($process.ExitCode -ne 0) { throw "git credential fill failed (exit code $($process.ExitCode)). Sign in to github.com with git first." }
	}
	finally { $process.Dispose() }

	foreach ($line in ($output -split "`r?`n")) {
		if ($line.StartsWith('password=')) {
			$token = $line.Substring('password='.Length)
			if ($token.Length -gt 0) { return $token }
		}
	}
	throw 'git credential fill gave no password for github.com. Sign in to github.com with git first.'
}

# Calls the REST API of GitHub. A failure is reported by the status and the answer of GitHub,
# with the token taken out in case it ever appeared.
function Invoke-GitHub([string]$Method, [string]$Uri, [string]$Token, $Body = $null, [string]$InFile = $null, [string]$ContentType = $null) {
	$headers = @{
		'Authorization' = "Bearer $Token"
		'Accept' = 'application/vnd.github+json'
		'X-GitHub-Api-Version' = '2022-11-28'
	}
	$arguments = @{ Method = $Method; Uri = $Uri; Headers = $headers; UserAgent = 'Wifinian-Publish'; UseBasicParsing = $true }
	if ($InFile) {
		$arguments.InFile = $InFile
		$arguments.ContentType = $ContentType
	}
	elseif ($null -ne $Body) {
		$arguments.Body = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Body -Compress))
		$arguments.ContentType = 'application/json; charset=utf-8'
	}

	$failure = $null
	try { return Invoke-RestMethod @arguments }
	catch {
		$failure = "$($_.Exception.Message)"
		if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $failure += " $($_.ErrorDetails.Message)" }
	}
	throw ("GitHub $Method $Uri failed: $failure").Replace($Token, '***')
}

# Finds the release of a tag, drafts included (a draft is not found by its tag otherwise).
function Find-Release([string]$Tag, [string]$Token) {
	$response = Invoke-GitHub 'GET' "$ApiRoot/releases?per_page=100" $Token
	return Select-Release $response $Tag
}

# Picks the release of a tag from the answer to a request for the list of releases.
# Windows PowerShell 5.1 gives a list read from JSON as one object (the whole array), which
# would here be taken for one release that has every tag and the assets of every release.
# The answer is therefore taken apart into the releases first.
function Select-Release($Response, [string]$Tag) {
	$releases = @($Response | ForEach-Object { $_ })
	foreach ($release in $releases) {
		if (($null -ne $release) -and (Test-SingleRelease $release) -and ($release.tag_name -ceq $Tag)) { return $release }
	}
	return $null
}

# Determines whether an object is one release (one id) and not a list of releases.
function Test-SingleRelease($Release) {
	if (($null -eq $Release) -or ($Release -is [array])) { return $false }
	if ($null -eq $Release.PSObject.Properties['id']) { return $false }
	$id = $Release.id
	return (($null -ne $id) -and ($id -isnot [array]) -and ("$id" -match '^\d+$'))
}

# Creates the release of a tag or takes the existing one, replaces the two assets and makes the
# release the latest. Returns the release as it is in the end.
function Publish-Release([string]$ReleaseVersion, [string]$ReleaseNotes, [string]$MsiPath, [string]$ManifestPath, [string]$Token) {
	$tag = "v$ReleaseVersion"
	$release = Find-Release $tag $Token
	if ($null -eq $release) {
		# A draft first, so that the release is never the latest one without its assets.
		Write-Host ">> Creating the release $tag (draft until the assets are there)" -ForegroundColor Cyan
		$release = Invoke-GitHub 'POST' "$ApiRoot/releases" $Token @{ tag_name = $tag; name = "Wifinian $ReleaseVersion"; body = $ReleaseNotes; draft = $true; prerelease = $false }
	}
	else {
		Write-Host ">> The release $tag exists: replacing its assets" -ForegroundColor Cyan
	}

	# Nothing is deleted or uploaded unless this is one release and the one of this tag.
	if ((-not (Test-SingleRelease $release)) -or ($release.tag_name -cne $tag)) {
		throw "The release $tag was not identified as a single release. No asset was deleted or uploaded."
	}

	# The manifest is removed first and added last, so that it never names an installer which
	# is not there or is not the one it was made for.
	foreach ($name in @($ManifestName, $MsiName)) {
		foreach ($asset in @($release.assets)) {
			if (($null -ne $asset) -and ($asset.name -ceq $name)) {
				Invoke-GitHub 'DELETE' "$ApiRoot/releases/assets/$($asset.id)" $Token | Out-Null
			}
		}
	}
	Write-Host ">> Uploading $MsiName" -ForegroundColor Cyan
	Invoke-GitHub 'POST' "$UploadRoot/releases/$($release.id)/assets?name=$MsiName" $Token $null $MsiPath 'application/octet-stream' | Out-Null
	Write-Host ">> Uploading $ManifestName" -ForegroundColor Cyan
	Invoke-GitHub 'POST' "$UploadRoot/releases/$($release.id)/assets?name=$ManifestName" $Token $null $ManifestPath 'application/json' | Out-Null

	Write-Host ">> Making the release the latest" -ForegroundColor Cyan
	return Invoke-GitHub 'PATCH' "$ApiRoot/releases/$($release.id)" $Token @{ tag_name = $tag; name = "Wifinian $ReleaseVersion"; body = $ReleaseNotes; draft = $false; prerelease = $false; make_latest = 'true' }
}

#endregion

# Dot-sourcing or -NoRun only defines the functions above.
if ($NoRun -or ($MyInvocation.InvocationName -eq '.')) { return }

#region Main

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$assemblyInfoPaths = @(
	(Join-Path $script:RepoRoot 'Source\Wifinian\Properties\AssemblyInfo.cs'),
	(Join-Path $script:RepoRoot 'Source\Wifinian.Test\Properties\AssemblyInfo.cs'))
$wxsPath = Join-Path $script:RepoRoot 'Source\Installer\Product.wxs'
$verifierPath = Join-Path $script:RepoRoot 'Source\Wifinian\Models\Update\UpdateVerifier.cs'
$solutionPath = Join-Path $script:RepoRoot 'Source\Wifinian.slnf'
$installerProjectPath = Join-Path $script:RepoRoot 'Source\Installer\Installer.wixproj'
$installerFolder = Join-Path $script:RepoRoot 'Source\Installer\bin\x86\Release'
$msiPath = Join-Path $installerFolder $MsiName
$manifestPath = Join-Path $installerFolder $ManifestName
$packagedExePath = Join-Path $script:RepoRoot 'Source\Wifinian\bin\x86\Release\Wifinian.exe'
$privateKeyPath = Join-Path $env:USERPROFILE '.wifinian\update-private-key.xml'
$documentsPath = Join-Path $env:USERPROFILE "Documents\$MsiName"
$tag = "v$Version"

$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }

# 1. Checks
if (-not (Test-ReleaseVersion $Version)) { throw "-Version must be three numbers like 3.9.1 (given: '$Version')." }

$branch = (Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD'))[0].Trim()
if ($branch -cne 'master') { throw "The branch is '$branch'. Releases are made from master." }

$changes = Invoke-Git @('status', '--porcelain')
if ($changes.Count -gt 0) { throw "The working tree is not clean. Commit or put aside first:`n$($changes -join "`n")" }

$currentVersion = Get-AssemblyInfoVersion (Read-TextFile $assemblyInfoPaths[0]).Text
$head = (Invoke-Git @('rev-parse', 'HEAD'))[0].Trim()
$tagCommit = Get-TagCommit $tag
$comparison = Compare-ReleaseVersion $Version $currentVersion
if ($comparison -lt 0) { throw "Version $Version is older than the current $currentVersion." }
if (($comparison -eq 0) -and ($null -ne $tagCommit) -and ($tagCommit -cne $head)) {
	throw "Version $Version is the current one and the tag $tag exists already, on another commit. Give a newer version."
}
if (($comparison -gt 0) -and ($null -ne $tagCommit)) { throw "The tag $tag exists already." }
# The same version with its tag on this very commit is a publishing which was interrupted
# after the push: it is taken up again and the assets of the release are replaced.
$isResumed = (($comparison -eq 0) -and ($null -ne $tagCommit))

if (-not (Test-Path -LiteralPath $privateKeyPath -PathType Leaf)) { throw "The private key is not at $privateKeyPath." }
$publicKey = Get-EmbeddedPublicKey (Read-TextFile $verifierPath).Text

Write-Host "Releasing $Version (current: $currentVersion)" -ForegroundColor Green
if ($DryRun) { Write-Host 'Dry run: nothing is committed, tagged, pushed or sent to GitHub.' -ForegroundColor Yellow }

# The files which hold the version, as they are now, to put them back if this run does not
# reach the commit.
$originals = @{}
foreach ($path in ($assemblyInfoPaths + $wxsPath)) { $originals[$path] = [System.IO.File]::ReadAllBytes($path) }
$isCommitted = $false
$started = Get-Date

try {
	# 2. Version, builds, tests and the installer
	foreach ($path in $assemblyInfoPaths) {
		$file = Read-TextFile $path
		Write-TextFile $path (Set-AssemblyInfoVersion $file.Text $Version) $file.HasBom
	}
	$file = Read-TextFile $wxsPath
	Write-TextFile $wxsPath (Set-WxsVersion $file.Text $Version) $file.HasBom

	Invoke-Tool 'Debug build' $dotnet @('build', $solutionPath, '-c', 'Debug')
	if (-not $SkipTests) {
		Invoke-Tool 'Tests' $dotnet @('test', $solutionPath, '-c', 'Debug', '--no-build')
	}
	Invoke-Tool 'Release build' $dotnet @('build', $solutionPath, '-c', 'Release')
	Invoke-Tool 'Installer build' $dotnet @('build', $installerProjectPath, '-c', 'Release', '-p:Platform=x86')

	if (-not (Test-Path -LiteralPath $msiPath -PathType Leaf)) { throw "The installer was not built at $msiPath." }
	if ((Get-Item -LiteralPath $msiPath).LastWriteTime -lt $started) { throw "The installer at $msiPath is older than this run: it was not rebuilt." }
	$packagedVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($packagedExePath).FileVersion
	if ($packagedVersion -cne "$Version.0") { throw "The packaged Wifinian.exe is of version $packagedVersion, not $Version.0." }
	$productVersion = Get-MsiProductVersion $msiPath
	if ($productVersion -cne $Version) { throw "The installer is of product version $productVersion, not $Version. The app would refuse it." }

	# 3. Hash, signature, check of the signature, latest.json
	$hash = Get-FileSha256 $msiPath
	$sha256 = ConvertTo-HexText $hash
	$signature = New-HashSignature $hash ([System.IO.File]::ReadAllText($privateKeyPath))
	if (-not (Test-HashSignature $hash $signature $publicKey)) {
		throw 'The signature is not valid for the public key built into the app (UpdateVerifier.cs). The key files and the app do not belong together; nothing was published.'
	}
	Write-Host "SHA-256: $sha256"
	Write-Host 'The signature is valid for the public key built into the app.'

	$msiUrl = Get-AssetUrl $Version $MsiName
	$manifestUrl = Get-AssetUrl $Version $ManifestName
	Write-Manifest $manifestPath $Version $msiUrl $sha256 $signature $Notes

	# 4. Copy for the publisher
	Copy-Item -LiteralPath $msiPath -Destination $documentsPath -Force
	if ((ConvertTo-HexText (Get-FileSha256 $documentsPath)) -cne $sha256) { throw "The copy at $documentsPath is not the same as the installer." }
	Write-Host "Installer: $documentsPath"
	Write-Host "Manifest:  $manifestPath"

	if ($DryRun) {
		Write-Host 'Dry run done. The version in the working tree is put back.' -ForegroundColor Yellow
		return
	}

	# 5. Commit, tag, push, release
	# The token is asked for before anything is committed, so that a missing sign-in stops here.
	$token = Get-GitHubToken

	$changes = Invoke-Git @('status', '--porcelain')
	if ($changes.Count -gt 0) {
		$paths = @($assemblyInfoPaths + $wxsPath)
		Invoke-Git (@('add', '--') + $paths) | Out-Null
		Invoke-Git @('commit', '-m', "Release $Version") | Out-Host
	}
	$isCommitted = $true

	if (-not $isResumed) {
		Invoke-Git @('tag', '-a', $tag, '-m', "Release $Version") | Out-Null
	}
	Invoke-Tool "Push of master and $tag" 'git' @('-C', $script:RepoRoot, 'push', '--atomic', 'origin', 'master', "refs/tags/$tag")

	$release = Publish-Release $Version $Notes $msiPath $manifestPath $token
	$token = $null

	# 6. Result
	$names = @($release.assets | ForEach-Object { $_.name })
	if (($names -cnotcontains $MsiName) -or ($names -cnotcontains $ManifestName)) { throw "The release $tag does not show both assets. Check $($release.html_url)." }
	if ($release.draft) { throw "The release $tag is still a draft. Check $($release.html_url)." }

	Write-Host ''
	Write-Host "Released $Version" -ForegroundColor Green
	Write-Host "Release:   $($release.html_url)"
	Write-Host "Installer: $msiUrl"
	Write-Host "Manifest:  $manifestUrl"
	Write-Host "The apps ask https://github.com/$Owner/$Repo/releases/latest/download/$ManifestName"
}
finally {
	$token = $null
	if (-not $isCommitted) {
		foreach ($path in $originals.Keys) { [System.IO.File]::WriteAllBytes($path, $originals[$path]) }
	}
}

#endregion
