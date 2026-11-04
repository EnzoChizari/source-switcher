# Source Switcher changelog

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
