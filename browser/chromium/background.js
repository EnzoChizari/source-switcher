/* global DX5ChromiumProtocol */
"use strict";
importScripts("protocol.js", "site-access.js");
(() => {
  const protocol = DX5ChromiumProtocol;
  const browserInstanceNonce = protocol.nonce();
  const demos = new Map();
  const linkedStatus = "Tab linked. No supported media header is available; Windows stream detection can still be used.";
  let autoFollow = false;
  const sites = DX5SiteAccess;
  void sites.sync();
  let selection = null, selectionGeneration = 0, followGeneration = 0, port = null, portGeneration = 0, sequence = 0;
  let clearReason = "unfollowed", stopAcknowledgement = false, stopTimer = null, status = "Not following a tab.";
  const publicStatus = () => ({ following: selection !== null, tabId: selection?.tabId ?? null,
    connected: port !== null, autoFollow, decoderSupported: false, status, siteError: sites.error() });

  function sanitizeError(error) {
    if (!error) return "";
    let raw = "";
    if (typeof error === "string") {
      raw = error;
    } else if (typeof error.message === "string") {
      raw = error.message;
    } else if (typeof error.toString === "function" && error.toString !== Object.prototype.toString) {
      raw = error.toString();
    }
    return raw.replace(/[\x00-\x1f\x7f-\x9f\s]+/g, " ").trim().slice(0, 240);
  }

  function disconnect() {
    autoFollow = false;
    clearTimeout(stopTimer); stopTimer = null;
    selection = null; selectionGeneration++; followGeneration++;
    if (port) { const previous = port; port = null; portGeneration++; previous.disconnect(); }
  }
  function clearSelection(reason) {
    selection = null; selectionGeneration++; followGeneration++; clearReason = reason; stopAcknowledgement = false;
    status = "Not following a tab.";
    if (port) { clearTimeout(stopTimer); stopTimer = setTimeout(disconnect, 3000); }
  }
  function connect() {
    if (port) return;
    const native = chrome.runtime.connectNative("local.dx5_source_switcher");
    port = native;
    let busy = false;
    const generation = ++portGeneration;
    native.onMessage.addListener(request => {
      if (port !== native || generation !== portGeneration) return;
      if (!protocol.isRequest(request)) { status = "Unsupported app request. Update the app and follow again."; disconnect(); return; }
      if (!selection && stopAcknowledgement) { disconnect(); return; }
      if (busy) { status = "The native bridge sent overlapping requests. Follow again to reconnect."; disconnect(); return; }
      busy = true;
      serve(request, native, generation).catch(() => {
        status = "Native bridge unavailable. Follow this tab again to reconnect."; disconnect();
      }).finally(() => { busy = false; });
    });
    native.onDisconnect.addListener(() => {
      if (port !== native) return;
      const failed = chrome.runtime.lastError;
      autoFollow = false;
      port = null; portGeneration++; selection = null; selectionGeneration++; followGeneration++;
      clearTimeout(stopTimer); stopTimer = null;
      const detail = sanitizeError(failed);
      status = failed
        ? (detail ? `Native bridge unavailable: ${detail}` : "Native bridge unavailable. Enable browser integration in the app, then follow again.")
        : "Native bridge disconnected. Follow this tab to reconnect.";
    });
  }
  async function serve(request, native, generation) {
    const captured = selection, capturedGeneration = selectionGeneration;
    const post = message => { if (port !== native || generation !== portGeneration) return false; native.postMessage(message); return true; };
    const clear = reason => {
      if (post(protocol.reply(request, ++sequence, browserInstanceNonce, selection, reason))) {
        if (!selection) stopAcknowledgement = true;
        else if (clearReason === reason) clearReason = null;
      }
    };
    if (!captured) { clear(clearReason ?? "unfollowed"); return; }
    if (clearReason) { const reason = clearReason; clearReason = null; clear(reason); return; }
    let timer;
    try {
      // tabs.get is bounded; a missing/hung tab cannot hold a native request forever.
      const [tab, media] = await Promise.race([observe(captured.tabId),
        new Promise((_, reject) => { timer = setTimeout(() => reject(new Error("timeout")), 1250); })]);
      if (captured !== selection || capturedGeneration !== selectionGeneration) { clear(clearReason ?? "navigation"); return; }
      if (!tab || tab.id !== captured.tabId) { clearSelection("tabClosed"); clear("tabClosed"); return; }
      captured.title = protocol.title(tab.title);
      const rate = Number.isInteger(media?.rate) && media.rate >= 8000 && media.rate <= 768000 ? media.rate : null;
      captured.hasRate ||= rate !== null;
      const bits = rate !== null && !media.streaming && [16, 24, 32].includes(media.bits) ? media.bits : null;
      const evidence = rate !== null ? { rate, bits, streaming: media.streaming === true } : captured.hasRate && media?.conflict ? { conflict: true }
        : captured.hasRate && media?.paused ? { paused: true } : null;
      post(protocol.reply(request, ++sequence, browserInstanceNonce, captured, null, evidence));
      status = rate !== null ? `Tab linked. ${media.streaming ? "Stream header" : "Media file header"}: ${rate / 1000} kHz${bits ? ` · ${bits}-bit` : ""}.`
        : evidence?.paused ? "Tab paused. Holding the last rate."
        : evidence?.conflict ? "Multiple media elements are playing. Holding the last rate."
        : tab.audible ? "Tab audio detected. Media format unavailable; Windows stream detection can still be used." : linkedStatus;
    } catch {
      if (captured === selection) status = "Followed tab is unavailable. The app keeps Windows stream detection available.";
      clear(captured === selection ? "unavailable" : clearReason ?? "navigation");
    } finally { clearTimeout(timer); }
  }
  async function observe(tabId) {
    const tab = await chrome.tabs.get(tabId);
    const demo = demos.get(tabId);
    if (tab?.url === chrome.runtime.getURL("fixture.html")) return [tab, demo?.observation];
    const [main, frameSet] = await Promise.all([probeDocument({ tabId }), sites.frames(tab)]);
    if (frameSet.truncated) return [tab, { conflict: true }];
    const frames = await Promise.all(frameSet.targets.map(probeDocument));
    if (frames.length === 0) return [tab, main];
    const observations = [main, ...frames];
    const formats = observations.filter(media => media?.rate);
    if (formats.length > 1 || observations.some(media => media?.conflict)
      || formats.length && observations.some(media => media?.playing && !media.rate)) return [tab, { conflict: true }];
    return [tab, formats[0] ?? (observations.every(media => media?.paused) ? { paused: true } : undefined)];
  }
  chrome.permissions?.onAdded?.addListener(() => { void sites.sync(); });
  chrome.permissions?.onRemoved?.addListener(() => { void sites.sync(); });
  async function probeDocument(target) {
    const probe = async injection => {
      try { return (await chrome.scripting.executeScript({ target, ...injection }))[0]?.result; }
      catch { return undefined; }
    };
    const stream = async () => {
      const existing = await probe({ world: "MAIN", func: () => globalThis[Symbol.for("source-switcher.mse-observer.v1")]?.read() ?? null });
      return existing === null
        ? await probe({ world: "MAIN", files: ["stream-formats.js", "media-header.js", "mse-observer.js"] }) : existing;
    };
    const direct = async () => {
      const existing = await probe({ func: () => typeof readPlayingMediaHeader === "function" ? readPlayingMediaHeader() : null });
      return existing === null
        ? await probe({ files: ["stream-formats.js", "media-header.js", "read-media.js"] }) : existing;
    };
    const [file, mse] = await Promise.all([direct(), stream()]);
    // Only the isolated file reader can supply bit depth. The page-world hook is for rates.
    const pageObservation = mse?.rate ? { rate: mse.rate, streaming: mse.streaming, playing: true } : mse;
    return mse?.conflict ? mse : file?.rate || file?.conflict ? file
      : mse?.rate || mse?.playing ? pageObservation : file ?? pageObservation;
  }
  chrome.runtime.onConnect.addListener(connection => {
    const tabId = connection.sender?.tab?.id;
    if (connection.name !== "pcm-demo" || connection.sender?.id !== chrome.runtime.id
        || connection.sender?.url !== chrome.runtime.getURL("fixture.html")
        || !Number.isInteger(tabId)) return;
    const demo = { observation: null };
    demos.set(tabId, demo);
    connection.onMessage.addListener(value => {
      const rate = value?.rate;
      demo.observation = [44100, 48000, 96000, 192000].includes(rate)
        ? { rate, bits: [16, 24, 32].includes(value.bits) ? value.bits : null }
        : value?.conflict === true ? { conflict: true } : { paused: true };
    });
    connection.onDisconnect.addListener(() => {
      if (demos.get(tabId) === demo) demos.delete(tabId);
    });
  });
  async function command(message) {
    if (message?.kind === "syncSiteAccess") { await sites.sync(); return publicStatus(); }
    if (message?.kind === "getStatus") return publicStatus();
    if (message?.kind === "stopFollowing") { autoFollow = false; clearSelection("unfollowed"); return publicStatus(); }
    if (message?.kind === "setAutoFollow") {
      autoFollow = message.enabled === true;
      if (!autoFollow) return publicStatus();
    } else if (message?.kind !== "followActiveTab") return undefined;
    return followActiveTab();
  }
  async function followActiveTab(automatic = false) {
    const generation = ++followGeneration;
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    if (generation !== followGeneration || (automatic && !autoFollow)) return publicStatus();
    if (automatic && selection?.tabId === tab?.id) return publicStatus();
    if (!Number.isSafeInteger(tab?.id) || tab.id < 0) throw new Error("No active tab.");
    const previous = selection;
    selectionGeneration++;
    selection = { tabId: tab.id, documentNonce: protocol.nonce(), mediaEpoch: 0, title: protocol.title(tab.title) };
    clearReason = previous && previous.tabId !== tab.id ? "navigation" : null;
    stopAcknowledgement = false; clearTimeout(stopTimer); stopTimer = null;
    status = "Linking selected tab; waiting for the app's native bridge.";
    try {
      connect();
    } catch (err) {
      if (port) { port = null; portGeneration++; }
      selection = null;
      autoFollow = false;
      selectionGeneration++;
      const detail = sanitizeError(err);
      status = detail
        ? `Native bridge unavailable: ${detail}`
        : "Native bridge unavailable. Enable browser integration in the app first.";
    }
    return publicStatus();
  }
  function activeTabChanged() {
    if (autoFollow) void followActiveTab(true).catch(() => {});
  }
  // Use sendResponse for compatibility with Chromium versions that do not accept
  // Promise-returning onMessage listeners. Only extension pages may choose tabs.
  chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (sender?.tab) return false;
    command(message).then(sendResponse, () => sendResponse({ ...publicStatus(), status: "Could not read the selected tab. Try Follow again." }));
    return true;
  });
  chrome.tabs.onUpdated.addListener((tabId, change) => {
    if (!selection || tabId !== selection.tabId) return;
    if (typeof change.title === "string") selection.title = protocol.title(change.title);
    if (!(change.status === "loading" || Object.hasOwn(change, "url"))) return;
    // The URL is never retained or sent. Navigation invalidates document identity.
    selection = { ...selection, documentNonce: protocol.nonce(), mediaEpoch: selection.mediaEpoch + 1, hasRate: false };
    selectionGeneration++; clearReason = "navigation";
    status = "Tab changed. Waiting for fresh media evidence.";
  });
  chrome.tabs.onActivated.addListener(activeTabChanged);
  chrome.windows.onFocusChanged.addListener(windowId => { if (windowId >= 0) activeTabChanged(); });
  chrome.tabs.onRemoved.addListener(tabId => {
    if (selection?.tabId === tabId) {
      clearSelection("tabClosed");
      activeTabChanged();
    }
  });
})();
