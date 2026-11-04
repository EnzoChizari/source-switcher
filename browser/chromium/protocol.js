// Pure background helpers. Tab identity is not decoder or output-format evidence.
(() => {
  "use strict";
  const evidenceField = "cdp.Media.kAudioTracks.samples per second";
  const headerEvidence = "direct-media.header.sampleRate";
  const streamEvidence = "stream-init.header.sampleRate";
  const validNonce = value => typeof value === "string" && /^[0-9a-f]{32}$/.test(value);
  const nonce = () => crypto.randomUUID().replaceAll("-", "");
  const isRequest = message => message && message.protocolVersion === 1 && message.kind === "request"
    && validNonce(message.connectionNonce) && validNonce(message.requestId);
  const title = value => typeof value === "string" ? value.replace(/[\u0000-\u001f\u007f]/g, " ").trim().slice(0, 160) : null;

  function reply(request, sequence, browserInstanceNonce, selection, clearReason = null, observation = null) {
    const supported = !clearReason && !!observation;
    return {
      protocolVersion: 1, kind: clearReason ? "clear" : "decoderSnapshot",
      connectionNonce: request.connectionNonce, requestId: request.requestId,
      sequence, browserInstanceNonce, tabId: selection?.tabId ?? null,
      documentNonce: clearReason ? null : selection?.documentNonce ?? null,
      mediaEpoch: selection?.mediaEpoch ?? 0,
      paused: !!clearReason || (supported && observation.paused === true), decoderSupported: supported,
      decoderRateHz: supported ? observation.rate ?? null : null, outputRateHz: null,
      ...(request.fileBitDepth === true && supported && !observation.streaming && [16, 24, 32].includes(observation.bits)
        ? { fileBitsPerSample: observation.bits } : {}),
      conflict: supported && observation.conflict === true,
      evidenceField: supported ? observation.streaming ? streamEvidence : headerEvidence : evidenceField,
      coverage: supported ? observation.streaming ? "selected-stream-init" : "selected-direct-media" : "selected-cdp-target", truncated: false,
      ...(clearReason ? { clearReason } : { title: title(selection?.title) })
    };
  }
  globalThis.DX5ChromiumProtocol = Object.freeze({ evidenceField, validNonce, nonce, isRequest, title, reply });
})();
