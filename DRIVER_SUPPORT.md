# Device-specific driver support

## Black Lion Audio Revolution 2x2

Install the official Black Lion Audio Windows driver before using Source Switcher.
The tested installation is driver **5.72.0.26579**, with vendor API **5.13**, in
`C:\Program Files\Black Lion Audio\USB Audio Device Driver\x64`.
The driver is available from [Black Lion Audio](https://blacklionaudio.com/en/products/revolution-2x2).

Select **Analog Out 1/2 (Revolution 2x2)** in Sources. The driver advertises
44.1, 48, 88.2, 96, 176.4 and 192 kHz. On the tested installation its shared
output is 24-bit PCM in a 32-bit container. Source Switcher preserves that actual
format; enabling experimental automatic bit depth does not create unsupported
16- or 32-bit output options. Windows' 32-bit float mix format is separate.

The vendor mixer controls the hardware clock and exposes only the current rate
through ordinary Windows exclusive-format probing. Source Switcher therefore
reads the installed vendor API for the rate list and changes the clock through
that API. It checks hardware-rate readback and waits for a fresh Windows mixer
at the target rate. No ASIO render stream is opened. The driver panel can remain
open; automation does not click it or depend on its UI.

The API is vendor-specific, not a portable Windows contract. The implemented ABI
was inspected and measured on the version above; unknown API versions fail with
a diagnostic instead of calling an assumed ABI. The vendor DLL is loaded for a
query/change and released afterward. It is neither bundled nor modified. Other
install locations and other Black Lion interface models are not currently covered.

The driver can recreate Windows endpoints during a rate change. Recovery matches
both the physical device container and the exact logical output name; it never
chooses an arbitrary device with a similar name. Zero or ambiguous matches keep
the selection pending. If Windows removes the old endpoint identity entirely,
select the output again. Renamed Windows endpoints may need their original
Revolution name restored for this adapter to recognize them.

All analog and virtual outputs and recording paths use the same hardware clock.
Changes can interrupt those paths. Turn off automatic switching during recording
or ASIO/exclusive playback; those clients are outside the shared-mode controller.
The driver itself may reject a requested change or an external-clock constraint.
Source Switcher does not change clock-source, buffer-size, routing or gain settings.
Some applications need playback restarted when Windows recreates their output.

### Validation on the attached interface

- All six advertised rates, plus a return to 48 kHz, passed hardware and Windows
  device/mix readback. Measured changes took approximately 1.98–2.28 seconds,
  including driver endpoint recreation; this is not a sub-second guarantee.
- Low-level generated shared-mode audio played at each rate with a nonzero Windows
  output peak. Meter evidence does not prove physical speaker/headphone audibility.
- Full controller tests with 48 and 96 kHz streams passed detection, clock switching,
  stream reopen, unchanged master identity and persistence of the recreated endpoint.
  The complete detect/apply/recovery observations were approximately 3.1 seconds.
- Regression tests cover preserved native precision/container/mask, rate filtering
  and refusal to rebind across physical devices, logical outputs or ambiguous matches.
- Active recording, external S/PDIF clock and ASIO/exclusive coexistence were not
  exercised. They are not claimed as verified automatic-switching scenarios.
