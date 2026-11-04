// Runs only in the chosen tab's accessible isolated extension contexts. The Gecko DOM object
// stays wrapped; this never injects page-world code or treats sink/output rate as decoder evidence.
(() => {
  "use strict";
  if (globalThis.__DX5DecoderDocument) return globalThis.__DX5DecoderDocument.scan();

  const validRate = value => Number.isInteger(value) && value >= 8000 && value <= 768000;
  const evidenceField = "decoder.reader.audioRate";
  const documentNonce = crypto.randomUUID().replaceAll("-", "");
  let mediaEpoch = 0, disposed = false, lastMedia = null, lastElements = null, inFlight = null;
  let decoderSupported = true;
  const events = ["play", "playing", "pause", "ended", "loadstart", "emptied", "abort", "error", "loadedmetadata", "ratechange"];
  const isMedia = element => element && /^(AUDIO|VIDEO)$/.test(element.tagName);
  const elementsNow = () => {
    const nodes = document.querySelectorAll("audio, video");
    const elements = [];
    for (let index = 0; index < Math.min(nodes.length, 32); index++) elements.push(nodes[index]);
    elements.truncated = nodes.length > 32;
    return elements;
  };
  const active = element => !element.paused && !element.ended && element.readyState >= 2 && element.isConnected !== false;
  const capture = element => ({ element, paused: element.paused, ended: element.ended,
    readyState: element.readyState, source: element.currentSrc ?? "" }); // Resource identity stays inside this document.
  const sameCapture = before => before.element.isConnected !== false
    && before.paused === before.element.paused && before.ended === before.element.ended
    && before.readyState === before.element.readyState && before.source === (before.element.currentSrc ?? "");
  const sameElements = (left, right) => left?.length === right.length && left.truncated === right.truncated
    && left.every((element, index) => element === right[index]);

  function dirty(event) {
    if (disposed || (event?.target && !isMedia(event.target))) return;
    mediaEpoch++;
    if (event?.type === "loadstart" || event?.type === "emptied" || event?.type === "abort" || event?.type === "error")
      lastMedia = null;
    try { browser.runtime.sendMessage({ kind: "dx5DocumentDirty", documentNonce, mediaEpoch }).catch(() => {}); }
    catch { /* A disconnected extension cannot publish evidence. */ }
  }
  for (const name of events) document.addEventListener(name, dirty, true);

  function summary(elements, values = {}) {
    return {
      documentNonce, mediaEpoch, topFrame: window === window.top,
      playingCount: elements.filter(active).length,
      available: !disposed, truncated: elements.truncated === true, conflict: false,
      decoderSupported,
      paused: !elements.some(active), decoderRateHz: null, outputRateHz: null,
      evidenceField, ...values
    };
  }

  async function scan() {
    const elements = elementsNow();
    if (!sameElements(lastElements, elements)) { mediaEpoch++; lastElements = elements; }
    const playing = elements.filter(active);
    if (disposed || elements.truncated) return summary(elements, { available: false });
    if (playing.length > 1) { lastMedia = null; return summary(elements, { conflict: true }); }
    if (!playing.length) {
      const retained = lastMedia && elements.includes(lastMedia.element)
        && lastMedia.source === (lastMedia.element.currentSrc ?? "");
      return summary(elements, { decoderRateHz: retained ? lastMedia.decoderRateHz : null,
        outputRateHz: retained ? lastMedia.outputRateHz : null });
    }
    const element = playing[0];
    if (!decoderSupported || typeof element.mozRequestDebugInfo !== "function") {
      decoderSupported = false;
      lastMedia = null;
      return summary(elements, { available: false, errorCode: "apiUnavailable" });
    }
    // An uncancellable Gecko promise that timed out remains the one outstanding query.
    // Later requests must not accumulate another diagnostic promise behind a stuck decoder.
    if (inFlight) return summary(elements, { available: false, errorCode: "probeOutstanding" });
    const epochBefore = mediaEpoch;
    const before = capture(element);
    const marker = {};
    inFlight = marker;
    let timer;
    try {
      const pending = Promise.resolve().then(() => element.mozRequestDebugInfo());
      pending.then(() => { if (inFlight === marker) inFlight = null; }, () => { if (inFlight === marker) inFlight = null; });
      const debug = await Promise.race([
        pending,
        new Promise((_, reject) => { timer = setTimeout(() => reject(new Error("timeout")), 1250); })
      ]);
      const afterElements = elementsNow();
      if (disposed || !decoderSupported || epochBefore !== mediaEpoch || !sameCapture(before) || !sameElements(elements, afterElements)
        || afterElements.filter(active).length !== 1 || !active(element))
        return summary(afterElements, { available: false, errorCode: "contextChanged" });
      const decoderRateHz = validRate(debug?.decoder?.reader?.audioRate) ? debug.decoder.reader.audioRate : null;
      const sink = debug?.decoder?.stateMachine?.mediaSink?.audioSinkWrapper?.audioSink?.outputRate;
      const outputRateHz = validRate(sink) ? sink : null;
      if (!decoderRateHz) { lastMedia = null; return summary(elements, { available: false, outputRateHz, errorCode: "readerRateUnavailable" }); }
      lastMedia = { element, source: before.source, decoderRateHz, outputRateHz };
      return summary(elements, { decoderRateHz, outputRateHz });
    } catch (error) {
      lastMedia = null;
      return summary(elementsNow(), { available: false, errorCode: error?.message === "timeout" ? "timeout" : "diagnosticsUnavailable" });
    } finally { clearTimeout(timer); }
  }

  function stop() {
    disposed = true;
    mediaEpoch++;
    lastMedia = null;
    for (const name of events) document.removeEventListener(name, dirty, true);
    globalThis.__DX5DecoderDocument = null;
  }

  window.addEventListener("pagehide", () => { dirty(); stop(); }, { once: true });
  globalThis.__DX5DecoderDocument = Object.freeze({ scan, stop });
  return scan();
})();
