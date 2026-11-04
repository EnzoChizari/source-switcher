// Background-only pure helpers. No page-world objects, URLs, browser storage, or network access.
(() => {
  "use strict";
  const evidenceField = "decoder.reader.audioRate";
  const validRate = value => Number.isInteger(value) && value >= 8000 && value <= 768000;
  const validNonce = value => typeof value === "string" && /^[0-9a-f]{32}$/.test(value);
  const nonce = () => crypto.randomUUID().replaceAll("-", "");
  const isRequest = message => message && message.protocolVersion === 1 && message.kind === "request"
    && validNonce(message.connectionNonce) && validNonce(message.requestId);

  function chooseFrames(frames) {
    if (!Array.isArray(frames) || !frames.length || frames.length > 32)
      return { available: false, paused: false, conflict: false, truncated: frames?.length > 32,
        decoderSupported: true,
        decoderRateHz: null, outputRateHz: null, documentNonce: null, fingerprint: "unavailable" };
    const top = frames.filter(frame => frame?.topFrame === true);
    const malformed = frames.some(frame => !frame || !validNonce(frame.documentNonce)
      || !Number.isSafeInteger(frame.mediaEpoch) || frame.mediaEpoch < 0
      || !Number.isSafeInteger(frame.playingCount) || frame.playingCount < 0 || frame.playingCount > 32
      || typeof frame.conflict !== "boolean" || typeof frame.truncated !== "boolean"
      || typeof frame.decoderSupported !== "boolean" || (!frame.decoderSupported && frame.decoderRateHz !== null)
      || frame.evidenceField !== evidenceField);
    if (malformed || top.length !== 1)
      return { available: false, paused: false, conflict: false, truncated: false,
        decoderSupported: true,
        decoderRateHz: null, outputRateHz: null, documentNonce: null, fingerprint: "malformed" };
    const playingCount = frames.reduce((count, frame) => count + frame.playingCount, 0);
    const truncated = frames.some(frame => frame.truncated);
    const conflict = playingCount > 1 || frames.some(frame => frame.conflict);
    const playing = frames.find(frame => frame.playingCount === 1);
    const retained = frames.filter(frame => frame.playingCount === 0 && validRate(frame.decoderRateHz));
    const chosen = playing ?? (retained.length === 1 ? retained[0] : top[0]);
    const available = !truncated && frames.every(frame => frame.available === true);
    const decoderSupported = frames.every(frame => frame.decoderSupported);
    const decoderRateHz = available && decoderSupported && !conflict && validRate(chosen.decoderRateHz) ? chosen.decoderRateHz : null;
    return {
      available, decoderSupported, paused: playingCount === 0, conflict, truncated, decoderRateHz,
      outputRateHz: validRate(chosen.outputRateHz) ? chosen.outputRateHz : null,
      documentNonce: top[0].documentNonce + ":" + chosen.documentNonce,
      fingerprint: frames.map(frame => `${frame.documentNonce}:${frame.mediaEpoch}:${frame.playingCount}:${frame.decoderRateHz}:${frame.conflict}:${frame.truncated}:${frame.available}:${frame.decoderSupported}`)
        .sort().join(";")
    };
  }

  function clearMessage(request, sequence, browserInstanceNonce, selection, reason) {
    return {
      protocolVersion: 1, kind: "clear", connectionNonce: request.connectionNonce, requestId: request.requestId,
      sequence, browserInstanceNonce, tabId: selection?.tabId ?? null,
      documentNonce: null, mediaEpoch: selection?.mediaEpoch ?? 0,
      paused: true, decoderRateHz: null, outputRateHz: null, conflict: false,
      decoderSupported: selection?.lastChosen?.decoderSupported ?? true,
      evidenceField, coverage: "selected-accessible-frames", truncated: false, clearReason: reason
    };
  }

  globalThis.DX5GeckoProtocol = Object.freeze({ evidenceField, validRate, validNonce, nonce,
    isRequest, chooseFrames, clearMessage });
})();
