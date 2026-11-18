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
 * `speed` table - one row per speed test: `time, interface_id, profile, bssid, band, channel, signal, link_rate, gateway_ms, latency_ms, jitter_ms, down_mbps, up_mbps, down_bytes, up_bytes, duration_ms, trigger, result, valid`
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
 2. It measures latency to, downloads from and uploads to `speed.cloudflare.com`.

Both hosts can be changed in settings.xml (see below). If another connection (e.g. Ethernet) carries the traffic instead of Wi-Fi, the result is recorded as not valid and is never used.

Automatic tests run 20 seconds after a network is connected (unless it was tested within the last 30 minutes), then once an hour while it stays connected, and only for networks with __Auto switch__ ticked and __Avoid__ unticked. An automatic test is put off while the link is carrying more than about 1 Mbps. __Test speed now__ in the menu tests the connected network at any time.

Upload speed is that of the bytes actually sent: it is measured for at least 3 seconds after a second of warm-up, through sockets with small fixed send buffers so that bytes merely handed to the system are not counted.

Data use: an automatic test uses up to about 60 MB (20 MB down and, depending on the upload speed, up to 40 MB up), __Test speed now__ up to about 130 MB. Automatic tests stop for the day once 500 MB has been used. The metered connection setting of Windows is not consulted, so untick __Measure speed automatically__ on metered or tethered connections.

### How speed-aware Engage decides

Tick __Engage by measured speed__ in the menu. It only has effect while __Engage__ is on, and it only moves between networks that have both __Auto connect__ and __Auto switch__ ticked (and __Avoid__ unticked), the same rule as the original Engage. Connecting or disconnecting by hand turns Engage off, as before.

 * The score of a network is its typical measured download speed (recent tests weigh more, tests older than 14 days are ignored), reduced when its current signal is weaker than it was during those tests. A network found to have no internet scores 0.
 * It stays where it is if the connected network scores 50 Mbps or more.
 * It switches only to a network that is at least 1.5 times and at least 10 Mbps faster, only after 10 minutes on the current network, and only when two consecutive checks at least 30 seconds apart (about a minute) agree. It waits up to 15 minutes for a pause in traffic, and makes at most 4 automatic switches an hour.
 * Before leaving, it retests the current network if its latest result is more than two hours old. After switching, it tests the new network; if that has no internet, or twice measures clearly worse than the network it left, it switches back once and leaves that network alone for 2 hours.
 * With no measurements at all it falls back to the original priority order. A network that has never been tested is tried once a day when the current one is below 50 Mbps. A connected network with no internet or a signal below 30 % is left for the best alternative.

Engage (in priority order as well as by speed) never takes the adapter back from a connection made outside this app: while the adapter is associating or authenticating with a network chosen in the Wi-Fi menu of Windows or by `netsh`, and for 10 seconds after a disconnection this app did not cause, it does not connect to anything. Once that network is connected, the ordinary rules above (time on the network, retest, margin) apply to leaving it.

Every decision is written with its reason to the events table and shown in the Decisions tab of the History window. While __Engage by measured speed__ is off but __Record history__ is on, the same logic runs without acting and records "would switch" rows, so it can be judged before it is turned on.

### Avoid

The __Avoid__ checkbox of a network makes this app never connect to that network automatically (Engage skips it, in priority order as well as by speed), never test it automatically and never test it when idle. It is kept in the settings of this app by profile name. It does not change the Windows profile: Windows itself may still connect to the network if its Auto connect is on, you can still connect by hand, and __Test all networks__ still includes it.

### Test all networks and testing when idle

Speed is only known for networks that have been connected, so:

 * __Test all networks__ in the menu connects to every saved network in range in turn, tests each and returns to the network it started from. Wi-Fi drops for about 30 seconds per network. A network is given up to 40 seconds to connect and to get an address; if it cannot be tested, a row with the result "Could not test" (never used as a speed) is added to the measurements. Choosing the same menu item again (__Cancel test__) stops it and returns to the original network.
 * __Test other networks when idle__ (off by default) does the same for one network at a time: at most once every 6 hours, only while speed-aware Engage is on, after 10 minutes without keyboard or mouse input and with no traffic on the link, and only for a network not tested in the last 3 days. The connection drops for about 30 seconds each time.

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
|SpeedTestDailyBudget|500|50-5000|MB a day automatic tests may use|
|SwitchDwellTime|10|2-60|Minutes to stay on a network before switching for speed|
|SwitchMargin|50|20-200|Percent another network must be faster (50 means 1.5 times)|
|SufficientSpeed|50|5-1000|Mbps at or above which the connected network is never left for a faster one|
|SpeedSignalThreshold|30|20-50|Signal (%) below which a tested network is left; a tested network is switched to only at 10 points above this|
|SwitchMaxDeferral|15|0-120|Minutes to wait for a pause in traffic before switching anyway (0 for no wait)|
|ProbeInterval|6|1-48|Hours between tests of other networks when idle|
|ConnectivityCheckUrl|`http://www.msftconnecttest.com/connecttest.txt`| |Must return the text "Microsoft Connect Test"|
|SpeedTestDownloadUrl|`https://speed.cloudflare.com/__down?bytes=25000000`| |Any large file over HTTP(S)|
|SpeedTestUploadUrl|`https://speed.cloudflare.com/__up`| |Must accept HTTP POST|

A network without measurements still needs the signal set by the number on __Engage__ (50-90 %) to be connected to.

### Building this fork

The projects are SDK-style and build with the .NET SDK alone (Visual Studio is not required). Build the solution filter, not `Wifinian.sln`, which also contains the installer project:

```
dotnet build Source\Wifinian.slnf -c Release
dotnet test Source\Wifinian.slnf -c Release --no-build
Source\Wifinian\bin\Release\Wifinian.exe
```

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
