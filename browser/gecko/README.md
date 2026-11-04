# Firefox / Zen tab decoder integration

This optional extension supplies decoder-reader evidence for **one tab you explicitly follow**. It uses Firefox/Zen's privileged `mozRequestDebugInfo().decoder.reader.audioRate`, not the Windows mixer, browser sink, a catalog, or audio-spectrum guessing. Decoder evidence does not establish the tab's exact Windows audio session, physical DAC routing, bit-perfect playback, or absence of browser processing/resampling.

The native bridge and browser integration must be enabled in DX5 Source Switcher. The host name is `local.dx5_source_switcher`; its manifest must allow only `dx5-native-rate-probe@local.invalid`. Native-host registration is handled by the application/setup instructions, not by this extension. No browser preferences, normal user profile, OS DLLs or drivers are changed by this folder.

## Load and follow

1. In the app's **Controls** page, enable browser integration and choose **Register browser helper**. Keep the complete published app folder in a stable location. This explicitly writes the current user's native-host registration; rerun it if the app folder moves.
2. In Firefox or Zen, open `about:debugging#/runtime/this-firefox`, choose **Load Temporary Add-on**, and select this folder's `manifest.json`.
3. Open an ordinary media tab. Click the extension and choose **Follow this tab**. The explicit click grants `activeTab` access; cross-origin navigation may require choosing Follow again.
4. Select the browser as the rate master in the app. Keep the Windows shared PCM and tab-decoder evidence labels distinct.
5. Use **Stop following** to release the followed-tab policy. No playback controls are sent to websites.

This unsigned developer add-on disappears when that browser instance closes. It follows no tab after a browser/background restart and saves no browser selection, URLs, track titles, browsing history or raw decoder JSON. The only permissions are `nativeMessaging`, `tabs` and `activeTab`. No broad host permission, page-world injection, browser debugger, localhost listener, network fetch or storage API is used by the extension.

## Observation and limits

Only the selected tab's accessible isolated frames are queried. There is at most one active diagnostic promise per document; a timed-out, uncancellable Gecko promise prevents accumulating additional queries until it settles or its document is replaced. Active recovery probes are throttled to 500 ms, with 2-second recovery checks when paused; media events can request fresh evidence sooner. Paused snapshots explicitly report paused state and can retain the previous decoder identity without claiming a fresh playing observation. Missing APIs, changed resources/documents, conflicts, truncation or denied access produce unknown/hold behavior.

The wire explicitly distinguishes capability from temporary observation failure. A missing privileged decoder API reports `decoderSupported: false` with no rate; the app can retain the followed-tab identity and fall back to independent Windows PCM detection. That unavailable capability remains sticky for the current document observer, including pause and player replacement, until a fresh document or explicit Follow creates a new observer. A supported API whose current reader is unavailable or times out reports a temporary unknown and does not substitute the browser sink rate or revive old decoder evidence. This distinction also permits an honest Helium tab link without claiming that Chromium's unavailable Media domain works.

Exactly one playing audio/video element is required across accessible frames, even if several players report the same rate. The census is bounded to 32 media elements per frame and 32 returned frames. Inaccessible frames, other tabs, closed media trees, Web Audio and WebRTC are not covered. Protected-media diagnostics and other browser versions require separate validation. A submitted WASAPI rate can differ from the decoder rate after browser resampling.

Messages answer receiver-issued one-use requests and echo connection/request nonces. A background-lifetime nonce, fresh Follow-selection nonce, document nonces, media epochs and increasing sequences identify context changes. Browser timestamps do not establish freshness. The app independently establishes executable/session/endpoint identity; the extension does not send claimed PIDs, paths, URLs or output-device identifiers. Browser diagnostics are an explicit followed-tab policy, not proof that other browser tabs are silent or that this tab renders through a particular endpoint.

## Known-rate test

The popup's **Open known-rate test** opens generated 5-second stereo PCM WAVs at **44.1, 48, 96 and 192 kHz**, initially at low volume and requiring a playback click. The WAV generator writes the exact PCM header and samples directly without AudioContext conversion. **Play one player at a time** is checked initially; uncheck it to verify that simultaneous players produce a conflict.

The extension page can read the Gecko API directly. For full selected-tab/native-bridge validation, serve this folder from a test server bound to `127.0.0.1`, open its `fixture.html` as an ordinary page, then choose **Follow this tab** in the extension. The ordinary page's own diagnostics button may lack extension privileges; the isolated extension probe is the production evidence path. Use a disposable browser profile for automated live validation.

Run pure JavaScript and mocked-browser checks with:

```powershell
node --test extension.test.mjs
```

These tests cover decoder/sink separation, multi-player conflicts, pause/resource/navigation races, uncancellable timeout behavior, context nonces, request-only messages, stopped/closed tabs, permission failure, privacy boundaries and known PCM headers. They do not replace native browser/pipe/elevation tests. This folder remains an optional developer integration until that validation is recorded.
