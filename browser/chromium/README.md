# Source Switcher Tab Link for Helium

1. Run the packaged Source Switcher build that includes Helium. In **Settings → Helium**, select **Register browser helper** and enable **Helium tab integration**. Register again if you move the app.
2. In Helium, open `chrome://extensions`, enable **Developer mode**, and choose **Load unpacked**. Select the `browser/chromium` folder inside the extracted Source Switcher folder. Reload if the extension is already loaded from that folder. If the old DX5 Helium Tab Link points at a different folder, remove that entry before loading this one.
3. Open **Source Switcher Tab Link** and click **Follow this tab** or check **Automatically follow the active tab**. Start playback, then select Helium (sometimes listed as `chrome`) as master in **Sources**. Following before you start gives the extension a chance to see streaming initialization.

Automatic following moves the link as you switch tabs or focus another Helium window. Unchecking keeps the current tab linked. **Stop following**, restarting Helium, or disconnecting the helper ends automatic following. Open the toolbar popup on a new site to grant that tab temporary media inspection access.

**File header** reads available WAV, native FLAC, MP3, MP4/AAC and WebM audio metadata from bounded HTTP(S) responses. Cross-origin resources require the server to allow the read. **Stream header** reads supported MP4 or WebM initialization data appended to a followed player's Media Source buffer. The extension temporarily observes the followed document's media APIs and removes its hooks after the follow lease expires. Media URLs, credentials, and audio bytes stay out of the native bridge. A previously detected tab holds its target on pause. Multiple playing elements can make the rate ambiguous.

Header rates describe the observed media. The browser can resample before sending PCM to Windows. Already initialized players, workers, inaccessible frames, Web Audio, encrypted media and unsupported formats can fall back to the Windows application stream rate when available. To try again when the player initialized before you followed it, start another item from that same linked page. The bundled PCM demo reports the headers of its own generated WAVs when followed. Reopen the demo tab after reloading the extension.

Version 0.3 requires the corresponding Source Switcher native helper for stream-header evidence. Update the app and reload the extension together. Version 0.3.1 retries a transient direct header read once while the same media remains active.

Run `node --test *.test.mjs` here for the extension tests. Build the Windows bundle from the repo with:

```powershell
pwsh -File .\tools\Publish-Systemwide.ps1 -FriendDistribution -IncludeHelium
```
