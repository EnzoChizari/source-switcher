# Source Switcher Tab Link for Chrome and Helium

1. Run the packaged Source Switcher build. In **Settings → Browser integration**, select **Register browser helper** and enable **Browser tab integration**. Register again if you move the app.
2. In Chrome or Helium, open `chrome://extensions`, enable **Developer mode**, and choose **Load unpacked**. Select the `browser/chromium` folder inside the extracted Source Switcher folder. Reload if the extension is already loaded from that folder. If the old DX5 Helium Tab Link points at a different folder, remove that entry before loading this one.
3. Open **Source Switcher Tab Link**. Choose **Improve detection on this site**, approve the browser prompt, and reload the player once. Or enable **Early detection on all websites** to include embedded players on other sites automatically. These permissions are optional.
4. Click **Follow this tab** or check **Automatically follow the active tab**. Start playback, then double-click your browser as master in **Sources**.

**Embedded players, including Animelib/Kodik.** A player can be hosted on a different site from the page around it. **Early detection on all websites** covers permitted HTTP(S) embedded players. With individual site access, the embedded player's own site also needs permission. The extension probes at most 16 eligible child documents in the followed tab; if more are present, it withholds a rate. Cached/prerendered and ungranted frames are excluded. Existing optional Kodik grants remain usable.

Automatic following moves the link as you switch tabs or focus another Chrome/Helium window. Unchecking keeps the current tab linked. **Stop following**, restarting the browser, or disconnecting the helper ends automatic following. Opening the toolbar popup grants temporary access to that tab; optional site access survives browser restarts and avoids asking again on every site.

**File header** reads available WAV, native FLAC, MP3, MP4/AAC and WebM audio metadata from bounded HTTP(S) responses. Cross-origin resources still require the server to allow the read; site access does not bypass CORS or authentication. **Stream header** reads supported MP4 or WebM initialization data appended to a player's Media Source buffer, including split initialization up to 64 KiB. On optionally granted sites, observation starts at document load, so a later Follow can read metadata already captured. It retains bounded format metadata without a repeating background timer. Other tabs never send evidence to Source Switcher unless followed.

Without persistent access, the followed document uses temporary hooks that expire after its follow lease. Revoke access on the extension details page, or uncheck all-websites access; reload open pages to remove hooks that were already injected. Chrome does not undo existing page code when its registration is removed. Media URLs, credentials, and audio bytes stay out of the native bridge. A previously detected tab holds its target on pause. Multiple playing elements, an unknown active player alongside a known player, or a frame limit can make the rate ambiguous.

Header rates describe the observed media. The browser can resample before sending PCM to Windows. Web Audio, WebRTC, DRM, worker-owned Media Source, inaccessible resources, or unsupported containers can remain unknown. This is not a universal decoder inspector. When media format is unavailable, the app can fall back to the Windows application stream rate. After upgrading or granting access, reload a player that was already open. The bundled PCM demo reports the headers of its own generated WAVs when followed. Reopen the demo tab after reloading the extension.

Version 0.5 requires Chrome/Helium 119 or newer. It reports 16-, 24-, and 32-bit integer WAV or native FLAC file precision when the app requests it. A floating-point WAV or lossy stream supplies a rate when readable; it does not claim a native integer depth. A transient direct header read is retried once while the same media remains active. Update the app and reload the extension together.

The registration and permission behavior follows [Chrome's scripting API](https://developer.chrome.com/docs/extensions/reference/api/scripting). The extension does not request debugger access; the [extension debugger domain list](https://developer.chrome.com/docs/extensions/reference/api/debugger#restricted-domains) excludes CDP Media.

Run `node --test *.test.mjs` here for the extension tests. Build the Windows bundle from the repo with:

```powershell
pwsh -File .\tools\Publish-Systemwide.ps1 -FriendDistribution -IncludeHelium
```
