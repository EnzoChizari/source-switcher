# Source Switcher changelog

## v1.4.4

- Apple Music prefers the driver's extensible PCM format for 16-bit output. This preserves the actual 16-bit depth when the legacy format makes Apple Music silent.
- If Apple Music is already silent on a legacy 16-bit output, Source Switcher can change the format description and keep the requested 16-bit depth. Other apps keep their existing format selection.
- Sources show known bit depths before the sample rate, with the same evidence labels.

## v1.4.3

- Apple Music can recover a silent 16-bit output by trying a higher supported depth while the track is still advancing. If silence persists after the change, Source Switcher can ask Apple Music once to reload its current playback position.
- Auto can lower Apple Music's output depth to a supported track depth and restore the previous format if playback stays silent. Once 16-bit fails on an output, Apple Music Auto avoids lowering back to it for the rest of the run. The track depth and Windows output depth stay separate.
- A manual Apple Music bit depth can apply at the current output rate while the player is paused or track format evidence is unavailable.

## v1.4.2

- Automatic handoffs hold the selected output format while Apple Music is still playing through that output.
- Apple Music's open Lossless details can supply the current track's reported ALAC rate and depth. Catalog values remain labeled as estimates when the panel is unavailable.
- If the panel gives an ALAC rate without a bit depth, the output keeps its current depth.

## v1.4.1

- Spotify Auto reads 16- or 24-bit lossless from the desktop player's open File quality card. Without that card, Auto keeps the current depth while following the observed Windows rate.

## v1.4.0

- Apple Music Auto can raise the output bit depth from a catalog estimate and keeps that depth across lower-depth tracks to avoid repeat interruptions. Manual depth overrides remain available.
- Apple Music keeps its controls enabled while waiting for a live signal and shows a steady status during retries.
- Linked Chrome and Helium tabs can supply the bit depth of supported integer WAV and native FLAC files. Fooyin can use its Now Playing track export. Spotify, VLC, Fooyin, and browsers keep depth when their only evidence is converted Windows PCM.
- Source Switcher Tab Link supports opt-in Kodik frame inspection, retries one transient file-header read, and distinguishes tab audio activity from available format evidence.
- Added extension installation, reload steps, and Fooyin setup to the main README.
- Checks for updates at startup and every 30 minutes while running. Installation still starts from the update dialog.
- Shows a small amber exclamation badge on the tray icon when an update is available.

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
