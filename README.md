# Source Switcher

**Choose which Windows app drives the sample rate of your DAC or playback device.**

Source Switcher lives in the Windows notification area. Choose a playback device and a source app, then follow the app's observed sample rate or set a rate yourself. If you regularly move between a music player, a video player, and other desktop apps, you can set priorities so the source changes with your listening.

[Download](https://github.com/EnzoChizari/source-switcher/releases) · [Quick start](#quick-start) · [Install the Chrome extension](#install-the-chrome-extension) · [How switching works](#how-switching-works) · [Troubleshooting](#troubleshooting) · [Changelog](SOURCE_SWITCHER_CHANGELOG.md)

## Quick start

Use the self-contained release ZIP on a Windows x64 PC. The ZIP includes the .NET desktop runtime.

1. Download the Windows x64 ZIP from [GitHub Releases](https://github.com/EnzoChizari/source-switcher/releases) and extract the entire archive to a folder.
2. Run `SourceSwitcher.exe` and click its icon in the Windows notification area. Check the hidden-icons menu if you don't see it.
3. In **Sources**, choose the DAC or playback device you want Source Switcher to control.
4. Start playback in a source app, then double-click its row in **Sources** to make it the master. The master is the app whose format drives the selected output.
5. Leave the rate on **Auto** to follow the observed sample rate, or choose one of the rates supported by the selected output. Open **Diagnostics** to see the observed format and the controller's status.

## Install the Chrome extension

1. In Source Switcher, open **Settings → Browser integration**. Enable **Browser tab integration** and click **Register browser helper**.
2. In Chrome or Helium, open `chrome://extensions` and turn on **Developer mode**. Click **Load unpacked** and select `browser/chromium` inside the extracted Source Switcher folder.
3. On the tab you want to use, open **Source Switcher Tab Link** from the browser's Extensions menu. Click **Follow this tab**, or enable **Automatically follow the active tab**. Start playback and double-click your browser (which may be listed as `chrome`) in **Sources**.
4. To test the link, choose **Open PCM playback demo** in the extension. Follow the demo tab and play one test at a time.

After an app update, click **Reload** on the extension's card at `chrome://extensions` and reopen the media tab. Register the helper again if you move the app. On a new website, open the extension once to grant temporary access. Unsupported players can use the Windows stream rate when available. [Extension details](browser/chromium/README.md)

For a Kodik player embedded in Animelib, click **Enable embedded Kodik players**, approve the browser prompt, and follow the tab before starting the video. When a player has already started, reload the episode page and follow before pressing play.

## Choose a switching mode

| What you want to do | Where to start |
| --- | --- |
| Let one player control the output | Double-click the player in **Sources** and set the rate to **Auto**. |
| Use a fixed rate for one app | Make the app master and pick a supported rate from the rate menu. |
| Let the master follow playback | Mark preferred apps as favorites or pins, then enable **Auto-switch by source priority**. |
| Give a fullscreen app priority | Enable fullscreen priority separately. |
| Keep the output at a specific bit depth for one app | Enable **Follow source bit depth (experimental)** in **Settings**, then set a manual depth for that master in **Sources**. |
| Change an app's volume without switching sources | Open **Mixer** and use the controls for that app's session. |

## How switching works

**Source priority.** With automatic switching enabled, the order is eligible fullscreen audio when enabled, playing favorites, playing pins, then regular apps. A playing app generally keeps control against peers at the same priority. Ordinary automatic handoffs require a source to remain eligible for half a second. Use **Release** to stop automatic following.

**Format detection.** Source Switcher observes PCM streams submitted to Windows and uses supported formats for the selected shared-mode playback device. If the observation is missing, stale, ambiguous, or unsupported, it can hold the last confirmed format. Automatic rate detection uses Windows audio tracing and may require administrator elevation or appropriate tracing permissions.

**Sample rate and bit depth are separate.** With catalog estimates enabled, Apple Music can raise the output depth in Auto and keeps the higher depth across tracks. Floating-point streams keep the current depth when the player provides no track format. A saved manual override lets you choose an exact depth.

| Source | What Auto depth follows |
| --- | --- |
| Apple Music | A higher depth from the open Lossless details or, when unavailable, an enabled catalog estimate. The output must support it. |
| Chrome and Helium | Integer depth from a supported WAV or native FLAC header in the linked tab. |
| Fooyin with Now Playing export | The active track's reported rate and depth. |
| Games and other apps with a clear integer stream | The PCM precision submitted to Windows. |
| Spotify desktop | The 16- or 24-bit lossless depth shown in the open File quality card. |
| VLC, browser media without depth, and floating-point streams | Keeps the current output depth when track precision is unavailable. |

For Apple Music, enable **Use Apple Music track format (catalog fallback)** and open the **Lossless** or **Hi-Res Lossless** badge in Apple Music. Source Switcher reads supported ALAC labels from the visible panel and associates them with the current title, artist, and album. With **Follow source bit depth (experimental)** enabled, Apple Music can raise the Windows output depth when supported. A label with only a sample rate keeps the current output depth. Automatic handoffs hold the current output format while Apple Music is playing through it.

If an active Apple Music session stays silent while the track advances, Source Switcher can still apply a supported increase in output depth. If silence continues after Windows confirms the new format, the app can request one seek to the current playback position. A manual Apple Music depth uses the current output rate while the player is paused or the track rate is unavailable.

For Spotify, enable **Follow source bit depth (experimental)**, make Spotify the master, and click the **Lossless** badge in Spotify's player to open **File quality** for **This computer**. Keep the card open while listening. The reader supports the English 16- and 24-bit FLAC labels. Auto holds the output depth when the card is closed, the window is minimized, or the labels are unavailable.

### Connect Fooyin track metadata

In Fooyin, open **Settings → Playback → Now Playing**. Enable **File** output to `%APPDATA%\DX5SourceSwitcher\fooyin-format.txt` using the expanded path in the file picker. Enable **Track changes**, **Play and pause**, **Stop**, and **Every second**. Leave append off and use this format:

```text
SSFOOYIN1|%isplaying%|%samplerate%|%bitdepth%|%datetime%
```

Select Fooyin as master and enable **Follow source bit depth (experimental)**. The report is read locally while Fooyin has an active session. [Fooyin format fields](https://docs.fooyin.org/en/latest/scripting/variables.html)

**Mixer.** View session activity and adjust volume or mute for supported sessions on the selected output. Some sessions are read-only when the app cannot safely identify or control them. The meters show Windows signal activity.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| Auto keeps the same rate | Confirm the intended app is master and playing. Check **Diagnostics** for stream evidence and try the refresh control. If audio tracing is unavailable, relaunch with the required permissions. |
| A manual rate is missing | The menu lists rates supported by the selected output at its current depth and channel count. Check that the intended device is selected. |
| The reported format differs from the file | A player can resample or convert audio before submitting it to Windows. Apple Music catalog data, when enabled, is an estimate. |
| Playback briefly goes silent on a switch | Changing the shared format can interrupt an active stream. Recovery depends on the player and device. A fixed manual rate can reduce format changes for that app. |
| The selected DAC is unavailable | Reconnect the device and check its status in **Diagnostics**. Source Switcher retains the selected output while it is absent. |

### Does this guarantee bit-perfect playback?

Source Switcher controls the Windows shared-mode output format. The format shown in Diagnostics describes the stream submitted to Windows, which can differ from the media file. Exclusive mode, ASIO, and native DSD are outside this workflow.

## Build and contribute

Install the .NET 8 SDK on Windows and run the tray project from the repository root.

```powershell
dotnet run --project .\systemwide\WindowsSourceSwitcher.Tray
```

The [contribution guide](CONTRIBUTING.md) covers the test commands and what to include in bug reports. Check the [security policy](SECURITY.md) for private vulnerability reporting and the [changelog](SOURCE_SWITCHER_CHANGELOG.md) for release history.

## Credits and license

We built Source Switcher around our own Windows tray workflow. Windows Lossless Switcher inspired the idea and provided selected components used in this project. See the [third-party notices](THIRD_PARTY_NOTICES.md) for attribution details.

Source Switcher is distributed under the [GNU General Public License v3.0 or later](LICENSE).

**This app is 100% vibe-coded.**
