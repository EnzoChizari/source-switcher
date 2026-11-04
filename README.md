# Source Switcher

**A Windows tray app that lets you choose which app drives your audio output format.**

Pick a DAC or another Windows playback device, then choose a source app manually or let Source Switcher follow your priorities. It can adjust the selected device's shared-mode sample rate based on what the active app sends to Windows.

## What you can do

- **Choose a source.** Set a music player or another app as the master source, or use favorites and pins to prioritize apps as playback moves between them.
- **Set the output format.** Follow the source's sample rate automatically or save a manual rate for an app. Automatic bit-depth selection is experimental, and you can set a manual depth override.
- **Mix apps in one place.** View active audio sessions and adjust volume or mute for supported sessions on the selected output.
- **See what's happening.** Use Diagnostics to check the observed stream format and see when Source Switcher holds the current output format.

## Get started

1. Download the Windows x64 ZIP from [Releases](https://github.com/EnzoChizari/source-switcher/releases).
2. Extract the entire ZIP and run `WindowsSourceSwitcher.Tray.exe`.
3. Open **Sources** from the tray icon, select your playback device, and double-click an app to make it the master source.
4. Leave the rate on **Auto** or choose a supported manual rate. If automatic detection is unavailable, try running the app as administrator so it can access Windows audio tracing.

Windows apps can resample audio before submitting it, so the observed format may differ from the original track. Changing the shared output format can briefly interrupt playback.

See the [changelog](SOURCE_SWITCHER_CHANGELOG.md) for recent updates.

## Credits

We built Source Switcher around our own Windows tray workflow. Windows Lossless Switcher helped inspire the idea, and Source Switcher also uses selected components from that project. See the [third-party notices](THIRD_PARTY_NOTICES.md) for attribution details.

## License

Source Switcher is distributed under the [GNU General Public License v3.0 or later](LICENSE).

**This app is 100% vibe-coded.**
