/* global DX5GeckoProtocol */
(() => {
  "use strict";
  const protocol = DX5GeckoProtocol;
  const browserInstanceNonce = protocol.nonce();
  let selection = null, selectionGeneration = 0, sequence = 0, port = null, portGeneration = 0;
  let clearReason = "unfollowed", stopAcknowledgement = false, stopTimer = null;
  let status = "Not following a tab.", dirty = true, lastProbeAt = -Infinity;
  const now = () => performance.now();
  const wait = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
  const stopContentCode = "globalThis.__DX5DecoderDocument?.stop();";
  const scanContentCode = "globalThis.__DX5DecoderDocument?.scan() ?? null;";

  function publicStatus() {
    return { following: selection !== null, tabId: selection?.tabId ?? null, connected: port !== null, status };
  }
  function disconnect() {
    clearTimeout(stopTimer); stopTimer = null;
    const prior = selection;
    if (prior) { selection = null; selectionGeneration++; stopObservers(prior.tabId); }
    if (port) { const previous = port; port = null; portGeneration++; previous.disconnect(); }
  }
  function stopObservers(tabId) {
    if (tabId === null || tabId === undefined) return Promise.resolve();
    return browser.tabs.executeScript(tabId, { code: stopContentCode, allFrames: true }).catch(() => {});
  }
  function clearSelection(reason) {
    const prior = selection;
    selection = null;
    selectionGeneration++;
    clearReason = reason;
    dirty = true;
    stopAcknowledgement = false;
    stopObservers(prior?.tabId);
    status = "Not following a tab.";
    if (port) {
      clearTimeout(stopTimer);
      stopTimer = setTimeout(disconnect, 3000);
    }
  }

  function connect() {
    if (port) return;
    const native = browser.runtime.connectNative("local.dx5_source_switcher");
    port = native;
    let busy = false;
    const generation = ++portGeneration;
    native.onMessage.addListener(message => {
      if (port !== native || generation !== portGeneration) return;
      if (!protocol.isRequest(message)) { status = "Unsupported native-host request. Follow this tab again after updating the app."; disconnect(); return; }
      if (!selection && stopAcknowledgement) { disconnect(); return; }
      if (busy) { status = "Native host sent overlapping requests. Reconnect to retry."; disconnect(); return; }
      busy = true;
      serve(message, native, generation).catch(() => {
        status = "Native bridge unavailable. Follow this tab to reconnect.";
        disconnect();
      }).finally(() => { busy = false; });
    });
    native.onDisconnect.addListener(() => {
      if (port !== native) return;
      port = null;
      portGeneration++;
      const prior = selection;
      selection = null;
      selectionGeneration++;
      dirty = true;
      stopObservers(prior?.tabId);
      status = native.error ? "Native bridge unavailable. Enable browser integration in the app, then Follow this tab again."
        : "Native bridge disconnected. Follow this tab to reconnect.";
      clearTimeout(stopTimer); stopTimer = null;
    });
  }

  async function serve(request, native, generation) {
    const captured = selection;
    const capturedGeneration = selectionGeneration;
    const post = message => {
      if (port !== native || portGeneration !== generation) return false;
      native.postMessage(message);
      return true;
    };
    const clear = reason => {
      if (post(protocol.clearMessage(request, ++sequence, browserInstanceNonce, selection, reason))) {
        if (!selection) stopAcknowledgement = true;
        else if (clearReason === reason) clearReason = null;
      }
    };
    if (!captured) { clear(clearReason); return; }
    if (!captured.ready) { clear("unavailable"); return; }
    if (clearReason) { const reason = clearReason; clearReason = null; clear(reason); return; }
    try {
      const pausedCache = !dirty && captured.lastChosen?.paused && now() - lastProbeAt < 2000;
      if (!dirty && !pausedCache) await wait(Math.max(0, 500 - (now() - lastProbeAt)));
      if (captured !== selection || capturedGeneration !== selectionGeneration) { clear(clearReason ?? "navigation"); return; }
      dirty = false;
      let frames, chosen = pausedCache ? captured.lastChosen : null;
      if (!chosen && captured.initialized) {
        frames = await browser.tabs.executeScript(captured.tabId, { code: scanContentCode, allFrames: true });
        if (!frames.length || frames.some(frame => !frame)) captured.initialized = false;
      }
      if (!chosen && !captured.initialized) {
        frames = await browser.tabs.executeScript(captured.tabId, { file: "content.js", allFrames: true });
        captured.initialized = true;
      }
      if (!chosen) lastProbeAt = now();
      if (captured !== selection || capturedGeneration !== selectionGeneration) { clear(clearReason ?? "navigation"); return; }
      chosen ??= protocol.chooseFrames(frames);
      captured.lastChosen = chosen;
      if (chosen.fingerprint !== captured.fingerprint) { captured.mediaEpoch++; captured.fingerprint = chosen.fingerprint; }
      if (!chosen.documentNonce) { status = "Decoder context unavailable. The app will hold its rate."; clear("unavailable"); return; }
      if (!chosen.available && chosen.decoderSupported && !chosen.conflict && !chosen.truncated) {
        status = "Decoder diagnostics unavailable. The app will hold its rate.";
        clear("unavailable"); return;
      }
      const message = {
        protocolVersion: 1, kind: "decoderSnapshot", connectionNonce: request.connectionNonce, requestId: request.requestId,
        sequence: ++sequence, browserInstanceNonce, tabId: captured.tabId,
        documentNonce: captured.pinNonce + ":" + chosen.documentNonce, mediaEpoch: captured.mediaEpoch,
        paused: chosen.paused, decoderRateHz: chosen.decoderRateHz, outputRateHz: chosen.outputRateHz,
        decoderSupported: chosen.decoderSupported,
        evidenceField: protocol.evidenceField, conflict: chosen.conflict,
        coverage: "selected-accessible-frames", truncated: chosen.truncated
      };
      post(message);
      status = !chosen.decoderSupported ? "This browser cannot expose the tab decoder rate. Windows application-stream detection remains available."
        : chosen.truncated ? "Too many media elements or frames. The app will hold its rate."
        : chosen.conflict ? "Several media players are active. The app will hold its rate."
        : chosen.paused ? "Followed tab is paused or has no playing media. The app will hold its rate."
        : chosen.decoderRateHz ? `${chosen.decoderRateHz.toLocaleString()} Hz tab decoder. Browser output may differ.`
        : "Decoder rate unavailable. The app will hold its rate.";
    } catch {
      if (captured === selection) { captured.initialized = false; status = "Cannot inspect this tab. Follow a normal media tab to grant access again."; }
      clear(captured === selection ? "unavailable" : clearReason ?? "navigation");
    }
  }

  browser.runtime.onMessage.addListener(async (message, sender) => {
    if (message?.kind === "dx5DocumentDirty") {
      if (selection && sender?.tab?.id === selection.tabId) dirty = true;
      return;
    }
    if (sender?.tab) return; // Content scripts cannot choose tabs or command the native host.
    if (message?.kind === "getStatus") return publicStatus();
    if (message?.kind === "stopFollowing") { clearSelection("unfollowed"); return publicStatus(); }
    if (message?.kind === "followActiveTab") {
      const followGeneration = ++selectionGeneration;
      const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
      if (followGeneration !== selectionGeneration) return publicStatus();
      if (!Number.isSafeInteger(tab?.id) || tab.id < 0) throw new Error("No active browser tab.");
      const priorTab = selection?.tabId;
      selection = { tabId: tab.id, pinNonce: protocol.nonce(), initialized: false, mediaEpoch: 0,
        fingerprint: null, lastChosen: null, ready: false };
      clearReason = null;
      stopAcknowledgement = false;
      clearTimeout(stopTimer); stopTimer = null;
      dirty = true;
      status = "Following selected tab; waiting for the app's native bridge.";
      await stopObservers(priorTab);
      if (followGeneration !== selectionGeneration) return publicStatus();
      selection.ready = true;
      try { connect(); } catch { selection = null; selectionGeneration++; status = "Native bridge unavailable. Enable browser integration in the app first."; }
      return publicStatus();
    }
  });
  browser.tabs.onUpdated.addListener((tabId, change) => {
    if (!selection || tabId !== selection.tabId || !(change.status === "loading" || Object.hasOwn(change, "url"))) return;
    selection.pinNonce = protocol.nonce();
    selection.initialized = false;
    selection.fingerprint = null;
    selection.lastChosen = null;
    selection.mediaEpoch++;
    selectionGeneration++;
    clearReason = "navigation";
    dirty = true;
    status = "Tab changed. Holding until fresh decoder evidence is available.";
  });
  browser.tabs.onRemoved.addListener(tabId => {
    if (selection?.tabId === tabId) clearSelection("tabClosed");
  });
})();
