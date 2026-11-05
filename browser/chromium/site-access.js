/* Optional persistent grants; never inspect an ungranted site's frames. */
globalThis.DX5SiteAccess = (() => {
  const id = "source-switcher-early-media-v1";
  const files = ["stream-formats.js", "media-header.js", "mse-observer.js", "early-media.js"];
  let queue = Promise.resolve(), error = "";
  const patterns = permissions => (permissions?.origins ?? []).filter(value => /^https?:\/\//.test(value)).sort();
  function matches(pattern, value) {
    try {
      const url = new URL(value), parsed = /^(https?):\/\/([^/]+)\/\*$/.exec(pattern);
      if (!parsed || url.protocol !== parsed[1] + ":") return false;
      const host = parsed[2];
      return host === "*" || (host.startsWith("*.")
        ? url.hostname === host.slice(2) || url.hostname.endsWith("." + host.slice(2)) : url.hostname === host);
    } catch { return false; }
  }
  async function grants() {
    return chrome.permissions?.getAll ? await chrome.permissions.getAll() : { origins: [], permissions: [] };
  }
  function sync() {
    queue = queue.catch(() => {}).then(async () => {
      if (!chrome.scripting?.getRegisteredContentScripts || !chrome.permissions?.getAll) return;
      const matches = patterns(await grants());
      const [existing] = await chrome.scripting.getRegisteredContentScripts({ ids: [id] });
      if (!matches.length) {
        if (existing) await chrome.scripting.unregisterContentScripts({ ids: [id] });
      } else {
        const script = { id, matches, js: files, world: "MAIN", runAt: "document_start",
          allFrames: true, matchOriginAsFallback: true, persistAcrossSessions: true };
        if (existing) await chrome.scripting.updateContentScripts([script]);
        else await chrome.scripting.registerContentScripts([script]);
      }
      error = "";
    }).catch(() => { error = "Early detection could not be registered. Update Chrome/Helium and retry site access."; });
    return queue;
  }
  async function frames(tab) {
    if (!chrome.webNavigation?.getAllFrames) return { targets: [], truncated: false };
    try {
      const permissions = await grants();
      // Legacy Kodik grants also work without granting access to every website.
      if (!(permissions.permissions ?? []).includes("webNavigation")) return { targets: [], truncated: false };
      const origins = patterns(permissions), all = await chrome.webNavigation.getAllFrames({ tabId: tab.id }) ?? [];
      const allowed = new Set([0]); // activeTab's same-origin access is enforced again by executeScript.
      const eligible = frame => {
        if (frame.errorOccurred || frame.documentLifecycle && frame.documentLifecycle !== "active") return false;
        try {
          const url = new URL(frame.url);
          return origins.some(pattern => matches(pattern, url.href))
            || ["http:", "https:"].includes(url.protocol) && url.origin === new URL(tab.url).origin;
        } catch { return false; }
      };
      for (const frame of all) if (eligible(frame)) allowed.add(frame.frameId);
      // Related about:blank/srcdoc/blob frames inherit an allowed parent origin.
      for (let i = 0; i < all.length; i++) {
        let added = false;
        for (const frame of all) if (!allowed.has(frame.frameId) && allowed.has(frame.parentFrameId)
          && !frame.errorOccurred && (!frame.documentLifecycle || frame.documentLifecycle === "active")
          && (/^about:(blank|srcdoc)([#?]|$)/.test(frame.url)
            || /^blob:https?:\/\//.test(frame.url) && eligible({ ...frame, url: frame.url.slice(5) }))) {
          allowed.add(frame.frameId); added = true;
        }
        if (!added) break;
      }
      const selected = all.filter(frame => frame.frameId > 0 && allowed.has(frame.frameId));
      return { truncated: selected.length > 16, targets: selected.slice(0, 16).map(frame => ({ tabId: tab.id,
        ...(frame.documentId ? { documentIds: [frame.documentId] } : { frameIds: [frame.frameId] }) })) };
    } catch { return { targets: [], truncated: true }; }
  }
  return { sync, frames, matches, error: () => error };
})();
