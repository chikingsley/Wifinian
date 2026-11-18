English|[Japanese](README_ja.md)
-|-

# Wifinian

More control over Wi-Fi connections!

Wifinian is a Windows desktop tool to enable user to actively control Wi-Fi connections.

<img src="Images/Screenshot_main.png" alt="Screenshot" width="512.5"><br>
(DPI: 200%)

Functions:
 * Connect to or disconnect from a wireless network
 * Rename a wireless profile
 * Change automatic connection (Auto Connect) or automatic switch (Auto Switch) settings of a wireless profile
 * __Rush__ - Perform rescan of wireless networks in short intervals (The number indicates interval (sec).)
 * __Engage__ - Execute automatic connection depending on signal strength, order and automatic switch setting of wireless profiles (The number indicates threshold of signal strength (%).)
 * __Organize__ - Change the order (priority) of wireless profiles, delete a wireless profile

![Screenshot](Images/Screenshot_organize.png)<br>
(DPI: 100%)

## Fork additions: speed history and speed-aware Engage

This fork records how each saved network performs and can let __Engage__ choose the network by measured speed instead of by profile order alone. All of it can be turned off from the menu (right click on the tray icon); with __Record history__, __Measure speed automatically__ and __Engage by measured speed__ all unticked, nothing is recorded, no test is run and Engage picks networks as the original does.

### What is recorded and where

History is kept in one local SQLite database, `%LOCALAPPDATA%\Wifinian\data\history.db`. Rows older than 90 days are deleted. Nothing is sent anywhere.

 * `signal` table - signal of every saved network in range, at most about once a minute while it changes and at least every 5 minutes: `time, interface_id, profile, connected, signal, rssi, band, channel, protocol, bssid, link_rate`
 * `speed` table - one row per speed test: `time, interface_id, profile, bssid, band, channel, signal, link_rate, gateway_ms, latency_ms, jitter_ms, down_mbps, up_mbps, down_bytes, up_bytes, duration_ms, trigger, result, valid, detail` (`detail` tells what went wrong in a failed test: the HTTP status the test server answered with or the error of the connection)
 * `events` table - every automatic switch and the reason for it, decisions to stay, and what speed-aware Engage would have done while it is off: `time, interface_id, kind, from_profile, to_profile, from_score, to_score, reason`

`time` is UTC as ISO 8601 text (`2026-11-17T23:37:29Z`), so it sorts and compares as text; each table is indexed by `time` and by profile and `time`. The database can be read with any SQLite tool while the app is running (it uses write-ahead logging, hence the `history.db-wal` and `history.db-shm` files beside it while the app runs), for example the query below. Rows are written in the background, so the app does not wait while another program holds the database; if a program keeps it locked for writing, up to 5,000 rows wait in memory and are written once it is released.

```sql
SELECT profile, count(*) AS tests, round(avg(down_mbps), 1) AS down, round(avg(latency_ms), 1) AS latency
FROM speed WHERE valid = 1 AND time >= '2026-11-01' GROUP BY profile;
```

The schema version is kept in `PRAGMA user_version`. When a network is renamed in this app, its rows in `signal` and `speed` are moved to the new name; `events` are left as they were recorded.

Old rows are deleted once a day. When that frees a quarter or more of the file (for example after `HistoryRetentionDays` is lowered), the database is rebuilt so that the file shrinks; otherwise the freed space is reused by new rows. If the file turns out not to be a valid database, it is renamed to `history-corrupt-yyyyMMdd-HHmmss.db` (never deleted) and a new database is started.

The database is in the `data` subfolder so that it survives upgrading and uninstalling: the installer removes the files directly in `%LOCALAPPDATA%\Wifinian` (such as the settings) but not its subfolders. Delete the folder by hand to remove the history. A `history.db` which an earlier build of this fork left directly in `%LOCALAPPDATA%\Wifinian` is moved to `data` when the app starts.

__History...__ in the menu opens a window with a summary per network, charts of signal and download speed (24 hours, 7 days or 30 days), the list of measurements and the list of decisions. __Open data folder__ opens the folder of the database. __Export CSV__ writes the shown range to three CSV files (`export-yyyyMMdd-HHmmss-signal.csv`, `-speed.csv` and `-events.csv`, UTF-8) in `%LOCALAPPDATA%\Wifinian\history` and opens that folder. The latest result is also shown at the right of each network's name in the main window (download / upload in Mbps, latency, age).

Earlier builds of this fork wrote daily CSV files (`signal-yyyyMMdd.csv`, `speed-yyyyMMdd.csv`, `events-yyyyMMdd.csv`) in `%LOCALAPPDATA%\Wifinian\history`. They are imported into the database once, in the background shortly after the app starts, and the folder is then renamed to `history-csv-imported`. It is not deleted and is no longer used, so it can be removed by hand.

The database is accessed through [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) with the native SQLite library of SQLitePCLRaw, which is placed under `runtimes\win-x86`, `win-x64` and `win-arm64` beside the executable and chosen by the architecture of the process.

### Speed test

A test measures the network the PC is connected to, through the Wi-Fi adapter only:

 1. It requests `http://www.msftconnecttest.com/connecttest.txt` to tell working internet from no internet or a sign-in (captive portal) page.
 2. It measures latency to, downloads from and uploads to `speed.cloudflare.com`, or downloads from another server while that one fails or limits requests (see __Test servers and their limits__ below).

The hosts can be changed in settings.xml (see below). If another connection (e.g. Ethernet) carries the traffic instead of Wi-Fi, the result is recorded as not valid and is never used.

Every test is timed, not sized: after a second of warm-up it downloads for 4 seconds and then uploads for 4 seconds (8 seconds each for __Test speed now__), whatever the speed of the link. No amount of data ends a test early. (Earlier builds stopped at 20 MB, which on a 100 Mbps link left only about a second to measure and made results jump around.) The only limits left are safety ones: 2 GB per direction, 10 seconds to get the first byte.

If the download gets no data at all although internet access and the connection to the test server were confirmed a moment before, it is tried once more 3 seconds later. A test that still fails is recorded with the result "Test server failed" and with what went wrong (for example `HTTP 429 Too Many Requests` or the error of the connection): the History window shows it in the Result column (in full in its tooltip), the `detail` column of the `speed` table has it, and `operation.log` gets a line. Every request of a test opens its own connection and closes it afterwards, so nothing is carried over from the network connected before.

Automatic tests run 20 seconds after a network is connected (unless it was tested within the last 30 minutes), then once an hour while it stays connected, and only for networks with __Auto switch__ ticked and __Avoid__ unticked. An automatic test is put off while the link is carrying more than about 1 Mbps. __Test speed now__ in the menu tests the connected network at any time.

Upload speed is that of the bytes actually sent: it is measured through sockets with small fixed send buffers so that bytes merely handed to the system are not counted, and is left blank if it could not be measured for at least 3 seconds. Upload speeds recorded by earlier builds, which counted bytes too early and showed several times the real speed, are blanked in the database the first time this build opens it; the rest of those rows is kept.

Data use is proportional to the speed of the link because every test lasts the same time. On a link of 100 Mbps in both directions an automatic test (and each network of __Test all networks__) uses about 130 MB (about 63 MB down in 5 seconds and about 65 MB up in 5.2 seconds, warm-up included) and __Test speed now__ about 230 MB; on a 20 Mbps link it is a fifth of that. (Measured on an office link: an automatic test at 79 Mbps down / 60 Mbps up used 51 MB + 36 MB in 11 seconds; __Test speed now__ at 92 / 84 Mbps used 104 MB + 97 MB in 19 seconds.) With the default of a test every hour that is about 3 GB a day at 100 Mbps. There is no daily limit by default (`SpeedTestDailyLimit` is 0). To limit it, set `SpeedTestDailyLimit` to a number of MB: automatic tests then stop for the day (UTC) once that much has been used. While the limit stops them, a line saying so is written to `operation.log` once a day. (Earlier builds called this setting `SpeedTestDailyBudget` and saved a default of 1000 or 500 to `settings.xml`; that leftover line is ignored now.) The metered connection setting of Windows is not consulted, so untick __Measure speed automatically__ (or set a limit, or lengthen `SpeedTestInterval`) on metered or tethered connections. The download URL asks for a 25 MB file per connection, which lasts a whole automatic test up to about 160 Mbps (__Test speed now__: about 90 Mbps); on a faster link the file is requested again, over a new connection which is made about 1.5 seconds before the file ends so that the transfer goes on without a gap. A request which fails during the download is made again after 0.3 seconds; if a connection fails three times in a row the download is not measured and the test is recorded as failed with the reason (the default server limits how much it serves to one address: after a morning of repeated tests it answered HTTP 429 with `Retry-After` of an hour, which the detail of the failed test shows).

### How speed-aware Engage decides

Tick __Engage by measured speed__ in the menu. It only has effect while __Engage__ is on, and it only moves between networks that have both __Auto connect__ and __Auto switch__ ticked (and __Avoid__ unticked), the same rule as the original Engage. Connecting or disconnecting by hand turns Engage off, as before.

 * The score of a network is its typical measured download speed (recent tests weigh more, tests older than 14 days are ignored), reduced when its current signal is weaker than it was during those tests. A network found to have no internet scores 0.
 * It stays where it is if the connected network scores 50 Mbps or more.
 * It switches only to a network that is at least 1.5 times and at least 10 Mbps faster, only after 10 minutes on the current network, and only when two consecutive checks at least 30 seconds apart (about a minute) agree. It waits up to 15 minutes for a pause in traffic, and makes at most 4 automatic switches an hour.
 * Before leaving, it retests the current network if its latest result is more than two hours old. After switching, it tests the new network 20 seconds later; if that has no internet, or measures below 80 % of the network it left and does so again 60 seconds later, it switches back once and leaves that network alone for 2 hours. Each step is listed under Decisions in the History window: the switch, the low result and the retest, and then either the switch back or that the network is kept. A network whose newest test, within the last 24 hours, was not faster by the margin is not switched to even if its typical speed still looks faster, so the same switch and switch back are not repeated after the 2 hours or after this app is restarted.
 * With no measurements at all it falls back to the original priority order. A network that has never been tested is tried once a day when the current one is below 50 Mbps. A connected network with no internet or a signal below 30 % is left for the best alternative.

Engage (in priority order as well as by speed) never takes the adapter back from a connection made outside this app: while the adapter is associating or authenticating with a network chosen in the Wi-Fi menu of Windows or by `netsh`, and for 10 seconds after a disconnection this app did not cause, it does not connect to anything. Once that network is connected, the ordinary rules above (time on the network, retest, margin) apply to leaving it.

Every decision is written with its reason to the events table and shown in the Decisions tab of the History window. While __Engage by measured speed__ is off but __Record history__ is on, the same logic runs without acting and records "would switch" rows, so it can be judged before it is turned on.

### Avoid

The __Avoid__ checkbox of a network makes this app never connect to that network automatically (Engage skips it, in priority order as well as by speed), never test it automatically and never test it when idle. It is kept in the settings of this app by profile name. It does not change the Windows profile: Windows itself may still connect to the network if its Auto connect is on, you can still connect by hand, and __Test all networks__ still includes it.

### Test all networks and testing when idle

Speed is only known for networks that have been connected, so:

 * __Test all networks__ in the menu connects to every saved network in range in turn, tests each and returns to the network it started from. Wi-Fi drops for about 30 seconds per network. A network is given up to 40 seconds to connect and to get an address; if it cannot be tested, a row with the result "Could not test" (never used as a speed) is added to the measurements. Choosing the same menu item again (__Cancel test__) stops it and returns to the original network. The Decisions list gets a line when the test starts ("Testing 3 networks") and one when it ends ("Tested 3 networks, back on X", with the networks that could not be tested, or that it was stopped or interrupted).
 * If this app is closed (Close in the menu, sign-out, shutdown) while a test has the adapter on another network, it asks Windows to reconnect the network it started from before it exits, waits a few seconds (6 at most) and records that in the Decisions list and in `operation.log`. Windows completes the connection even after the app has gone. A process killed from outside cannot do this.
 * __Test other networks when idle__ (off by default) does the same for one network at a time: at most once every 6 hours, only while speed-aware Engage is on, after 10 minutes without keyboard or mouse input and with no traffic on the link, and only for a network not tested in the last 18 hours. The connection drops for about 30 seconds each time. When the PC is idle and the test still does not run, `operation.log` tells the lasting reasons (off, too soon, link in use, nothing to test, not connected), each once per idle time; work in progress that ends within seconds is not noted.

### Test servers and their limits

Download uses the first of these servers that works, in this order:

|Server|File|What it is|
|-|-|-|
|`speed.cloudflare.com`|`/__down?bytes=25000000`|Cloudflare's speed test (also the only server for upload, `/__up`)|
|`cachefly.cachefly.net`|`/100mb.test`|Test file CacheFly publishes on its content delivery network|
|`proof.ovh.net`|`/files/1Gb.dat`|Test file OVHcloud publishes for testing its network (in Europe)|

A URL set as `SpeedTestDownloadUrl` goes first and these three follow it. A server is listed once: if that URL is on one of these servers, it replaces the URL above for that server.

These servers are free and public, and they throttle heavy use. Cloudflare limits how much it serves to one address: after a day of repeated tests (hourly tests, __Test all networks__, tests after switching) it answered every request with HTTP 429 and `Retry-After: 3600` for an hour. Every test uses data in proportion to the speed of the link (see above), so a fast link tested often reaches such a limit sooner. This is how the app deals with it:

 * A server that answers 429, or 403/503 with a `Retry-After` header, is not asked again on that network until the time it gave is over (30 minutes if it gave none, never less than a minute or more than 6 hours). The limit belongs to the public address the requests came from, which differs between networks, so another network asks the server itself once and leaves it alone too if it is refused there. Not even the latency check connects to it meanwhile. The list is kept in `%LOCALAPPDATA%\Wifinian\data\servers.txt`, so restarting the app does not forget it; delete that file (with the app closed) to forget it by hand.
 * The test in which that happens goes on to the next server at once, and so do the tests after it. The same happens when a server cannot be reached or gives no data (the first server asked is tried once more after 3 seconds before it is given up, the others are not). A test fails with "Test server failed" only if no server at all gave a speed.
 * The `detail` of the speed row names the server when it was not the first one, for example `Server: cachefly.cachefly.net (speed.cloudflare.com limits requests until 14:05)`. The other servers are single files, one of them in Europe, so on a fast link the server rather than the network can limit the speed, and latency is to the server used. Such a speed is therefore treated as a lower bound: it is shown as the last result, but the typical speed and latency of a network (the figures networks are compared by) use only tests through the first server as long as the network has any, and after an automatic switch it can confirm that the new network is fast enough but never causes a switch back.
 * Upload has no second server: no public server is known that accepts this kind of upload from anyone over HTTPS. While the upload server is blocked the test is still recorded as Ok with the upload left blank, the same as when upload alone fails; networks are compared by download speed only, so nothing changes for switching.
 * `operation.log` gets one line when a server is blocked (with what it answered and until when) and one when it is used again, not one per test.
 * Every test records the network the adapter is actually on: the profile, BSSID, band, channel, signal and link rate are asked of Windows at the moment the test starts, not taken from the list this app shows (which can lag behind a switch). They are asked again when the test ends. If the adapter is then on another SSID or disconnected, the row is recorded as aborted with `Network changed during the test: ...` in its detail and counts for neither network; a move to another access point of the same SSID keeps the result and is noted as `Roamed during the test: ...`. The test after an automatic switch runs only on the network switched to: if the adapter is not on it yet, the test waits (as long as the check of the switch is due, 5 minutes) and `operation.log` says so.
 * __Test all networks__ or __Test speed now__ which cannot run (the profiles are not loaded yet, another test or an update is still running, nothing is in range) writes a line to `operation.log` saying why.
 * A test in which the servers failed is never taken as a finding on the network, because internet access was confirmed just before: it does not count in the typical speed, never marks a network as without internet, never causes a switch back, and the check after an automatic switch is simply repeated a minute later. The exception is a test shaped by servers known to limit requests (none could be asked, or another server was used because of it): repeating it would give the same until their times are over, so it is not repeated, the switch stays unchecked, and a test in which no server could be asked gets no line in `operation.log`.
 * A further server is asked only within 20 seconds of the start of the transfers, and a server that does not accept the first two connections is given up at once. On a network that passes the connectivity check but lets nothing else through, a test therefore ends about as soon as it did with a single server instead of waiting for the timeouts of all three.

### Updates

About 2 minutes after start and then once a day, the app asks `github.com` whether a newer version of this fork has been released (`https://github.com/chikingsley/Wifinian/releases/latest/download/latest.json`). Nothing is sent but the request itself (the address of this PC, as with any request, and the name `Wifinian-Updater`): no identifier, no version, no data on networks. The check is not made during a speed test or a test of networks; if it fails (e.g. no internet access) it is tried again an hour later and the failure is written to `operation.log` once a day at most. To turn the automatic check off, close the app and set `ChecksUpdates` to `false` in `settings.xml` (see below); the menu still checks when asked.

The automatic check only tells: the item __Check for updates__ of the menu then reads __Install 3.9.1 and restart__ and a line is written to `operation.log`. Nothing is downloaded or installed until that item is chosen. Then the installer (.msi) is downloaded to `%LOCALAPPDATA%\Wifinian\update` and is used only if all of these hold; otherwise the item reads __Update failed:__ with the reason, `operation.log` has the details and nothing is installed:

 * the version is newer than the running one,
 * the installer is a release asset of this fork (`https://github.com/chikingsley/Wifinian/releases/download/...`),
 * its SHA-256 is the one given by the release,
 * its signature (RSA 3072, SHA-256) is valid for the public key built into the app. The private key is kept by the publisher and is not in the repository, so a file from anyone else is refused,
 * the version written in the installer itself is the one the release names. The signature covers the installer, not the version the release names, so without this an installer of an older release could be offered again under a higher version.

The app then closes in the way __Close__ does (a running test of networks restores the connection first; the reason in `operation.log` is "Closing to install update ..."), the installer runs showing only its progress, and the app is started again from the installed folder (if the installer failed, it puts the previous version back and that one is started). An app that was run from another folder, such as a copy or the output of a build, is thus replaced by the installed one; that copy stays as it is. The next start records the exit code of the installer in `operation.log`.

### Logs

`operation.log` in `%LOCALAPPDATA%\Wifinian` records every start and every orderly shutdown of this app with its reason (Close in the menu, the main window closed, the Windows session ending) and a second instance that was started and ended at once. A process killed from outside (Task Manager, `taskkill`) cannot write anything, so the next start records that the previous run left no record of its end. Unhandled exceptions are always written to `exception.log` in the same folder before the process ends.

### Settings without UI

These values are only in `%LOCALAPPDATA%\Wifinian\settings.xml`. Close the app before editing; values out of range are brought into range.

|Setting|Default|Range|Meaning|
|-|-|-|-|
|SampleInterval|60|30-300|Seconds between checks of signal and of what to do|
|ScanInterval|5|0-1440|Minutes between background scans (0 for never); a scan is skipped while the link is in use|
|HistoryRetentionDays|90|7-365|Days history is kept|
|SpeedTestInterval|60|15-240|Minutes between periodic tests of the connected network|
|SpeedTestDailyLimit|0|0-100000|MB a day automatic tests may use (0: no limit)|
|SwitchDwellTime|10|2-60|Minutes to stay on a network before switching for speed|
|SwitchMargin|50|20-200|Percent another network must be faster (50 means 1.5 times)|
|SufficientSpeed|50|5-1000|Mbps at or above which the connected network is never left for a faster one|
|SpeedSignalThreshold|30|20-50|Signal (%) below which a tested network is left; a tested network is switched to only at 10 points above this|
|SwitchMaxDeferral|15|0-120|Minutes to wait for a pause in traffic before switching anyway (0 for no wait)|
|ProbeInterval|6|1-48|Hours between tests of other networks when idle|
|ChecksUpdates|true|true, false|Whether to ask github.com once a day for a new version of this fork (see Updates)|
|ConnectivityCheckUrl|`http://www.msftconnecttest.com/connecttest.txt`| |Must return the text "Microsoft Connect Test"|
|SpeedTestDownloadUrl|`https://speed.cloudflare.com/__down?bytes=25000000`| |Any large file over HTTP(S); used first, the built-in servers follow|
|SpeedTestUploadUrl|`https://speed.cloudflare.com/__up`| |Must accept HTTP POST; used first, the built-in server follows if this one is blocked|

A network without measurements still needs the signal set by the number on __Engage__ (50-90 %) to be connected to.

### Building this fork

The projects are SDK-style and build with the .NET SDK alone (Visual Studio is not required). Build the solution filter, not `Wifinian.sln`, which also contains the installer project:

```
dotnet build Source\Wifinian.slnf -c Release
dotnet test Source\Wifinian.slnf -c Release --no-build
Source\Wifinian\bin\Release\Wifinian.exe
```

### Publishing an update

A release which the installed apps pick up (see Updates) is made by one script, from a clean `master`:

```
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Version 3.9.1 -Notes "what changed"
```

It needs the private key at `%USERPROFILE%\.wifinian\update-private-key.xml` (never in the repository; without it no update can be made which the apps accept, so keep a copy in a safe place) and git signed in to `github.com` (the token is taken from `git credential fill` when needed and is not written anywhere). In order, stopping at the first failure:

 1. Checks: branch `master`, nothing uncommitted, the version is of three numbers and newer than the current one, the private key is there. The current version is accepted as long as it has no tag yet (the first release), or if its tag is on the current commit (a publishing which was interrupted is taken up again).
 2. Sets the version in `Source\Wifinian\Properties\AssemblyInfo.cs`, `Source\Wifinian.Test\Properties\AssemblyInfo.cs` and `Source\Installer\Product.wxs`, builds Debug, runs the tests (`-SkipTests` leaves them out), builds Release and the installer.
 3. Computes SHA-256 of the installer, signs it, checks the signature against the public key in `UpdateVerifier.cs` in the way the app does, and writes `latest.json` next to the installer (`Source\Installer\bin\x86\Release`).
 4. Copies the installer to `%USERPROFILE%\Documents\Wifinian.Installer.msi`.
 5. Commits the version ("Release 3.9.1"), tags `v3.9.1`, pushes `master` and the tag, creates the release on GitHub with `Wifinian.Installer.msi` and `latest.json` and makes it the latest. The release is a draft until both files are there. If the release exists already, its two files are replaced.
 6. Tells the addresses of the release and of the two files.

With `-DryRun` the script ends after step 4 and puts the version in the working tree back: nothing is committed, tagged or pushed and GitHub is not contacted. The version is put back as well whenever a run fails before the commit.

## Requirements

 * Windows 7 or newer
 * .NET Framework 4.8
 * On Windows 11 (24H2) or newer, in Privacy & security > Location settings, access to location information needs to be turned on.
  <img src="Images/Privacy_location_en.png" alt="Location" width="600">
 
## Download

 * Microsoft Store (Windows 10 (1607) or newer):<br>
   [Wifinian](https://www.microsoft.com/store/apps/9pngfqps4flh)<br>
   <a href='//www.microsoft.com/store/apps/9pngfqps4flh?cid=storebadge&ocid=badge'><img src='https://developer.microsoft.com/store/badges/images/English_get-it-from-MS.png' alt='Wifinian' width='142px' height='52px'/></a>

 * Winget (a.k.a. [Windows Package Manager](https://docs.microsoft.com/en-us/windows/package-manager), App Installer):
   ```
   winget install Wifinian
   ```

 * Other:<br>
:floppy_disk: [Installer](https://github.com/emoacht/Wifinian/releases/download/3.8.0-Installer/WifinianInstaller380.zip)

## Install/Uninstall

If you wish to place executable files on your own, you can extract them from installer file (.msi) by the following command:

```
msiexec /a [source msi file path] targetdir=[destination folder path (absolute path)] /qn
```

In such case, please note the following:

 - The settings file will be created at: `[system drive]\Users\[user name]\AppData\Local\Wifinian\`
 - When you check [Start on sign in], a registry value will be added to: `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`

## Remarks

 - To rename a wireless profile, press and hold its name.
 - Rescan of wireless networks by OS itself is triggered by some actions and if no such actions take place, seems to be performed once per one minute.
 - Automatic connection by Engage function applies only to wireless profiles whose automatic switch are enabled. If a profile whose automatic switch is not enabled has been already connected, automatic connection will not be executed.
 - When you connect to a wireless network by OS's wireless menu, the order of wireless profiles may be automatically changed.
 - A wireless profile is associated with a specific wireless adapter and if the adapter is not in place, such profile will not be shown.

## History

:scroll: [History](HISTORY.md)

## Libraries

 - [Reactive Extensions][1]
 - [Reactive Property][2]
 - [XamlBehaviors for WPF][3]
 - [Managed Native Wifi][4]
 - [Monitorian/ScreenFrame][5]
 - [Monitorian/StartupAgency][5]

[1]: https://github.com/Reactive-Extensions/Rx.NET
[2]: https://github.com/runceel/ReactiveProperty
[3]: https://github.com/microsoft/XamlBehaviorsWpf
[4]: https://github.com/emoacht/ManagedNativeWifi
[5]: https://github.com/emoacht/Monitorian

## License

 - MIT License

## Developer

 - emoacht (emotom[atmark]pobox.com)
