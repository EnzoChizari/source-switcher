# Source Switcher changelog

## v1.5.0

- Added Black Lion Audio Revolution 2x2 support using its installed Windows driver API. Discover and select the driver's 44.1, 48, 88.2, 96, 176.4 and 192 kHz rates instead of being limited to the current WDM rate.
- Verify the hardware clock and Windows mixer after a change. Recover the same physical device and logical output when the driver recreates Windows endpoints. Ambiguous or different devices are never selected by name alone.
- Preserve the driver's supported PCM precision (24-bit on the tested 5.72 driver). Automatic bit depth cannot add unsupported 16- or 32-bit output modes. All Revolution outputs share one hardware clock; changes can briefly interrupt playback and recording. ASIO/exclusive applications remain outside shared-mode control.
- Requires the official Black Lion Audio driver in its standard installation folder; the vendor DLL is loaded only for driver queries/changes and is not bundled or modified. Tested with driver 5.72, API 5.13.

## v1.4.0

- Improved Apple Music format identification and switching recovery, including compatible 16-bit output. Lossless details can be read automatically while Apple Music is in front. Catalog fallback remains an estimate.
- Spotify can read 16- or 24-bit lossless from its open File quality card. Fooyin track exports and linked integer WAV/native FLAC headers can supply source depth. Unknown or converted float precision holds the current depth; automatic depth remains experimental.
- Chrome/Helium extension 0.5.1 adds optional early detection on one site or all websites, including permitted embedded players. Supported stream headers can be captured before Follow. Protected, inaccessible and unsupported media can remain unknown.
- Reduced background work and retained memory. The balanced runtime memory policy preserves detector deadlines and mixer cadence. Local controller checks were around 128–150 MiB RAM and 0.15% average CPU during browser playback; startup and switching can have higher peaks.
- Recover abandoned audio traces after crashes, add graceful shutdown for maintenance, and avoid unnecessary player UI-component loading. Browser grants are cached with permission-change invalidation and fresh frame checks.
- Sources display known bit depth before sample rate. Stability fixes retain master selection through brief Apple Music stream restarts and improve pause/handoff handling.
- Update checks run at startup and every 30 minutes, with an amber tray/Settings badge and a changelog before installation. Public release updates no longer require GitHub sign-in.
- Windows x64 ZIP includes the desktop runtime, browser helper/extension, license notices and corresponding source. Reload the extension after updating. Windows/shared-mode conversion and player-specific limitations still apply.

## v1.3.2

- Marked automatic bit-depth selection as experimental in Settings and Sources. Manual depth overrides remain available.

## v1.3.1

- Windows x64 release ZIP filenames include the version, such as SourceSwitcher-v1.3.1-win-x64.zip.
- This release also includes a compatibility copy so earlier builds can update in the app.
- The update screen shows the GitHub sign-in command when a credential is missing.
- Settings reserves room for automatic-source status so the Rate detection card stays steady.
- Float streams from Fooyin, VLC, and other players still supply the sample rate. Auto keeps the current bit depth until there is usable evidence or a manual override.
- Spotify's submitted stream depth remains visible in Diagnostics. Auto holds the current depth because Spotify can convert the original track before submitting it.

## v1.3.0

### Source controls

- Click a source once to select it for Favorite and Pin. Double-click it to make it master.
- Save a master bit-depth override for an individual app while Follow source bit depth is enabled.
- Spotify's 32-bit Windows stream no longer sets the output depth in Auto. A known 16- or 24-bit track can use a manual depth.

### Updates

- Redesigned the update window with version information, formatted scrolling notes, and a fixed amber Update button.

## v1.2.5

- Reduced the Apple Music format-switch preflight and measured signal return separately from timeline confirmation.
- Kept Rate detection checkbox colors and card layout steady during audio changes.

## v1.2.4

- Settings and the update changelog use dark scrollbars, including while the changelog is open over Settings.

## v1.2.3

- With both Apple Music catalog estimates and Follow source bit depth enabled, the controller uses the same track's estimated rate and depth together.
- Follow source bit depth now handles 32-bit float streams such as those submitted by Fooyin.
- Diagnostics distinguish Apple Music's estimated depth from the Windows stream's submitted precision.

## v1.2.2

- The target-rate dropdown shows only rates supported by the selected output at its current bit depth and channel count.
- Unavailable saved overrides are explained below the dropdown and can be replaced by Auto or a supported rate.

## v1.2.1

- Review the changelog before downloading an update. Choose Close to come back later or Update to install it.
- GitHub releases now include notes for each version.

## v1.2.0

- Enable Follow source bit depth in Settings to match observed 16-, 24-, or 32-bit integer PCM.
- See your selected output's highest probed sample rate at each bit depth in Settings.
- An amber exclamation mark beside Settings marks an available update.

## v1.1.2

- Renamed Controls to Settings.

## v1.1.1

- Automated testing and private Windows x64 release publishing.
