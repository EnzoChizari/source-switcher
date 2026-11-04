# Source Switcher Tab Link for Chrome and Helium

1. Run the packaged Source Switcher build. In **Settings → Browser integration**, select **Register browser helper** and enable **Browser tab integration**. Register again if you move the app.
2. In Chrome or Helium, open `chrome://extensions`, enable **Developer mode**, and choose **Load unpacked**. Select the `browser/chromium` folder inside the extracted Source Switcher folder. Reload if the extension is already loaded from that folder. If the old DX5 Helium Tab Link points at a different folder, remove that entry before loading this one.
3. Open **Source Switcher Tab Link** and click **Follow this tab** or check **Automatically follow the active tab**. Start playback, then select your browser as master in **Sources**. Following before you start gives the extension a chance to see streaming initialization.

**Animelib and Kodik.** In the extension popup, choose **Enable embedded Kodik players** and grant the optional browser permissions. The extension can inspect supported Kodik frames on the followed tab. Start playback after following so the extension can see the player's initialization. Revoke access from the extension's details page.

Automatic following moves the link as you switch tabs or focus another Helium window. Unchecking keeps the current tab linked. **Stop following**, restarting Helium, or disconnecting the helper ends automatic following. Open the toolbar popup on a new site to grant that tab temporary media inspection access.

**File header** reads available WAV, native FLAC, MP3, MP4/AAC and WebM audio metadata from bounded HTTP(S) responses. Cross-origin resources require the server to allow the read. **Stream header** reads supported MP4 or WebM initialization data appended to a followed player's Media Source buffer. The extension temporarily observes the followed document's media APIs and removes its hooks after the follow lease expires. Media URLs, credentials, and audio bytes stay out of the native bridge. A previously detected tab holds its target on pause. Multiple playing elements can make the rate ambiguous.

Header rates describe the observed media. The browser can resample before sending PCM to Windows. The popup uses the browser's tab audio activity for music, video, and Web Audio. When exact media format is unavailable, the app can fall back to the Windows application stream rate. To try again when the player initialized before you followed it, start another item from that same linked page. The bundled PCM demo reports the headers of its own generated WAVs when followed. Reopen the demo tab after reloading the extension.

Version 0.4 reports 16-, 24-, and 32-bit integer WAV or native FLAC file precision when the app requests it. A floating-point WAV or lossy stream supplies a rate when readable. A transient direct header read is retried once while the same media remains active. Update the app and reload the extension together to use the new depth evidence.

Run `node --test *.test.mjs` here for the extension tests. Build the Windows bundle from the repo with:

```powershell
pwsh -File .\tools\Publish-Systemwide.ps1 -FriendDistribution -IncludeHelium
```
