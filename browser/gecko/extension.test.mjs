import assert from "node:assert/strict";
import { webcrypto } from "node:crypto";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import { test } from "node:test";

const [protocolSource, contentSource, backgroundSource, fixtureSource, manifestSource] = await Promise.all(
  ["protocol.js", "content.js", "background.js", "fixture.js", "manifest.json"].map(file => readFile(new URL(file, import.meta.url), "utf8")));
const a = "a".repeat(32), b = "b".repeat(32), c = "c".repeat(32);
function protocol() {
  const context = vm.createContext({ crypto: webcrypto });
  vm.runInContext(protocolSource, context);
  return context.DX5GeckoProtocol;
}
function frame(overrides = {}) {
  return { documentNonce: a, mediaEpoch: 1, topFrame: true, playingCount: 1,
    available: true, truncated: false, conflict: false, paused: false, decoderRateHz: 96000,
    decoderSupported: true,
    outputRateHz: 48000, evidenceField: "decoder.reader.audioRate", ...overrides };
}
function decoder(rate = 96000, sink = 48000) {
  return { decoder: { rate: 192000, reader: { audioRate: rate },
    stateMachine: { mediaSink: { audioSinkWrapper: { audioSink: { outputRate: sink } } } } } };
}
function element(overrides = {}) {
  return { tagName: "AUDIO", paused: false, ended: false, readyState: 4, isConnected: true,
    currentSrc: "private-resource-that-must-not-leave-document", mozRequestDebugInfo: async () => decoder(), ...overrides };
}
function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
function content(elements) {
  const documentListeners = new Map(), windowListeners = new Map(), timers = new Map();
  let timerId = 0, currentElements = elements;
  const document = {
    querySelectorAll: () => currentElements,
    addEventListener: (name, listener) => documentListeners.set(name, listener),
    removeEventListener: name => documentListeners.delete(name)
  };
  const window = { addEventListener: (name, listener) => windowListeners.set(name, listener) };
  window.top = window;
  const messages = [];
  const context = vm.createContext({ document, window, crypto: webcrypto,
    browser: { runtime: { sendMessage: async message => { messages.push(message); } } },
    setTimeout: callback => { timers.set(++timerId, callback); return timerId; },
    clearTimeout: id => timers.delete(id) });
  const first = vm.runInContext(contentSource, context);
  return {
    first, scan: () => context.__DX5DecoderDocument.scan(),
    event: (name, target = currentElements[0]) => documentListeners.get(name)?.({ type: name, target }),
    navigate: () => windowListeners.get("pagehide")?.(),
    replace: values => { currentElements = values; },
    timeout: () => { const callbacks = [...timers.values()]; timers.clear(); callbacks.forEach(callback => callback()); },
    messages, context
  };
}
function event() {
  const listeners = [];
  return { addListener: listener => listeners.push(listener),
    emit: (...args) => Promise.all(listeners.map(listener => listener(...args))) };
}
function background() {
  const sent = [], scriptCalls = [], responseQueue = [], ports = [], timers = new Map();
  let currentTime = 1000, nextTimer = 0, query = async () => [{ id: 7 }];
  const runtimeMessages = event(), updated = event(), removed = event();
  const browser = {
    runtime: {
      onMessage: runtimeMessages,
      connectNative: name => {
        assert.equal(name, "local.dx5_source_switcher");
        const port = { onMessage: event(), onDisconnect: event(), sent: [], disconnected: false,
          postMessage(message) { this.sent.push(message); sent.push(message); },
          disconnect() { this.disconnected = true; this.onDisconnect.emit(); } };
        ports.push(port); return port;
      }
    },
    tabs: {
      query: (...args) => query(...args), onUpdated: updated, onRemoved: removed,
      executeScript: async (tabId, details) => {
        scriptCalls.push({ tabId, ...details });
        if (details.code?.includes(".stop()")) return [];
        const next = responseQueue.shift();
        if (next instanceof Error) throw next;
        return next ?? [frame()];
      }
    }
  };
  const context = vm.createContext({ browser, crypto: webcrypto, performance: { now: () => currentTime },
    setTimeout: (callback, ms) => {
      const id = ++nextTimer;
      if (ms <= 500) { currentTime += ms; queueMicrotask(callback); }
      else timers.set(id, callback);
      return id;
    }, clearTimeout: id => timers.delete(id) });
  vm.runInContext(protocolSource, context); vm.runInContext(backgroundSource, context);
  return {
    sent, scriptCalls, responseQueue, ports, updated, removed,
    extension: async kind => (await runtimeMessages.emit({ kind }, {}))[0],
    contentMessage: async message => runtimeMessages.emit(message, { tab: { id: 7 } }),
    setQuery: value => { query = value; },
    request: async (id = a) => {
      await ports.at(-1).onMessage.emit({ protocolVersion: 1, kind: "request", connectionNonce: b, requestId: id });
      for (let index = 0; index < 12; index++) await new Promise(resolve => setImmediate(resolve));
    }
  };
}

test("manifest is opt-in and requests only native messaging, tabs and activeTab", () => {
  const manifest = JSON.parse(manifestSource);
  assert.deepEqual(manifest.permissions, ["nativeMessaging", "tabs", "activeTab"]);
  assert.equal(manifest.browser_specific_settings.gecko.id, "dx5-native-rate-probe@local.invalid");
  assert.equal(manifest.content_scripts, undefined);
  assert.equal(manifest.externally_connectable, undefined);
});
test("decoder reader rate stays distinct from browser sink and generic decoder rate", async () => {
  const state = content([element()]);
  const result = await state.first;
  assert.equal(result.decoderRateHz, 96000);
  assert.equal(result.outputRateHz, 48000);
  assert.equal(result.evidenceField, "decoder.reader.audioRate");
  assert.equal(result.paused, false);
  assert.equal(JSON.stringify(result).includes("private-resource"), false);
});
test("output-only diagnostics never become decoder evidence", async () => {
  const state = content([element({ mozRequestDebugInfo: async () => decoder(0, 192000) })]);
  const result = await state.first;
  assert.equal(result.decoderRateHz, null);
  assert.equal(result.outputRateHz, 192000);
  assert.equal(result.available, false);
});
test("two playing elements conflict even when their rates would agree", async () => {
  let reads = 0;
  const media = () => element({ mozRequestDebugInfo: async () => { reads++; return decoder(48000); } });
  const result = await content([media(), media()]).first;
  assert.equal(result.conflict, true); assert.equal(result.decoderRateHz, null); assert.equal(reads, 0);
});
test("one playing element and paused companions use only the playing decoder", async () => {
  const result = await content([element({ paused: true, mozRequestDebugInfo: () => { throw new Error("must not inspect"); } }), element()]).first;
  assert.equal(result.playingCount, 1); assert.equal(result.decoderRateHz, 96000);
});
test("paused media retains decoder identity while explicitly reporting paused", async () => {
  const audio = element(), state = content([audio]);
  const first = await state.first;
  audio.paused = true; state.event("pause");
  const paused = await state.scan();
  assert.equal(paused.decoderRateHz, 96000); assert.equal(paused.paused, true);
  assert.equal(paused.documentNonce, first.documentNonce); assert.ok(paused.mediaEpoch > first.mediaEpoch);
});
test("resource replacement clears retained paused rate", async () => {
  const audio = element(), state = content([audio]);
  await state.first;
  audio.paused = true; audio.currentSrc = "replacement-private-resource"; state.event("loadstart");
  const result = await state.scan();
  assert.equal(result.decoderRateHz, null); assert.equal(result.paused, true);
});
test("pause during asynchronous diagnostics discards the late rate", async () => {
  const pending = deferred(), audio = element({ mozRequestDebugInfo: () => pending.promise });
  const state = content([audio]); await Promise.resolve();
  audio.paused = true; state.event("pause"); pending.resolve(decoder(192000));
  const result = await state.first;
  assert.equal(result.decoderRateHz, null); assert.equal(result.available, false);
  assert.equal(result.errorCode, "contextChanged");
});
test("resource, element replacement and navigation during diagnostics discard old results", async () => {
  for (const change of ["resource", "element", "navigation"]) {
    const pending = deferred(), audio = element({ mozRequestDebugInfo: () => pending.promise });
    const state = content([audio]); await Promise.resolve();
    if (change === "resource") { audio.currentSrc = "new-private-resource"; state.event("loadstart"); }
    if (change === "element") state.replace([element()]);
    if (change === "navigation") state.navigate();
    pending.resolve(decoder(192000));
    const result = await state.first;
    assert.equal(result.decoderRateHz, null, change); assert.equal(result.available, false, change);
  }
});
test("diagnostic timeout holds one uncancellable promise rather than accumulating queries", async () => {
  const pending = deferred(); let reads = 0;
  const state = content([element({ mozRequestDebugInfo: () => { reads++; return pending.promise; } })]);
  await Promise.resolve(); state.timeout();
  const first = await state.first;
  assert.equal(first.errorCode, "timeout");
  const second = await state.scan();
  assert.equal(second.errorCode, "probeOutstanding"); assert.equal(reads, 1);
  pending.resolve(decoder()); await Promise.resolve(); await Promise.resolve();
});
test("more than 32 elements are truncated without starting decoder queries", async () => {
  let reads = 0;
  const result = await content(Array.from({ length: 1000 }, () => element({ mozRequestDebugInfo: async () => { reads++; return decoder(); } }))).first;
  assert.equal(result.truncated, true); assert.equal(result.available, false);
  assert.equal(result.playingCount, 32); assert.equal(reads, 0);
});
test("missing privileged API remains unknown", async () => {
  const result = await content([element({ mozRequestDebugInfo: undefined })]).first;
  assert.equal(result.errorCode, "apiUnavailable"); assert.equal(result.decoderRateHz, null);
  assert.equal(result.decoderSupported, false);
});
test("unsupported capability stays unavailable during pause and later media in the same document", async () => {
  const audio = element({ mozRequestDebugInfo: undefined }), state = content([audio]);
  const first = await state.first;
  assert.equal(first.decoderSupported, false);
  audio.paused = true; state.event("pause");
  const paused = await state.scan();
  assert.equal(paused.paused, true); assert.equal(paused.decoderSupported, false); assert.equal(paused.decoderRateHz, null);
  let queries = 0;
  state.replace([element({ mozRequestDebugInfo: async () => { queries++; return decoder(192000); } })]);
  const replacement = await state.scan();
  assert.equal(replacement.decoderSupported, false); assert.equal(replacement.decoderRateHz, null); assert.equal(queries, 0);
  const freshDocument = await content([element()]).first;
  assert.equal(freshDocument.decoderSupported, true); assert.equal(freshDocument.decoderRateHz, 96000);
  assert.notEqual(freshDocument.documentNonce, first.documentNonce);
});
test("decoder reader observations support all four known PCM fixture rates", async () => {
  for (const rate of [44100, 48000, 96000, 192000]) {
    const result = await content([element({ mozRequestDebugInfo: async () => decoder(rate, 48000) })]).first;
    assert.equal(result.decoderRateHz, rate); assert.equal(result.decoderSupported, true);
    assert.equal(result.outputRateHz, 48000);
  }
});
test("missing decoder API is distinguished from a temporary diagnostic timeout", async () => {
  const choose = protocol().chooseFrames;
  const unsupported = choose([frame({ available: false, decoderSupported: false, decoderRateHz: null })]);
  assert.equal(unsupported.decoderSupported, false); assert.equal(unsupported.decoderRateHz, null);
  const temporary = choose([frame({ available: false, decoderSupported: true, decoderRateHz: null })]);
  assert.equal(temporary.decoderSupported, true); assert.equal(temporary.decoderRateHz, null);
  for (const bad of [frame({ decoderSupported: "false" }), frame({ decoderSupported: false, decoderRateHz: 96000 })]) {
    const rejected = choose([bad]); assert.equal(rejected.available, false); assert.equal(rejected.documentNonce, null);
  }
});
test("mixed frame capabilities never combine unsupported status with a positive decoder rate", () => {
  const result = protocol().chooseFrames([frame(), frame({ documentNonce: c, topFrame: false,
    playingCount: 0, paused: true, available: true, decoderSupported: false, decoderRateHz: null, outputRateHz: null })]);
  assert.equal(result.decoderSupported, false); assert.equal(result.decoderRateHz, null);
});
test("frame aggregation detects distinct simultaneous players and missing frame evidence", () => {
  const choose = protocol().chooseFrames;
  const result = choose([frame(), frame({ documentNonce: c, topFrame: false, decoderRateHz: 48000 })]);
  assert.equal(result.conflict, true); assert.equal(result.decoderRateHz, null);
  const unavailable = choose([frame(), frame({ documentNonce: c, topFrame: false, playingCount: 0, available: false })]);
  assert.equal(unavailable.available, false); assert.equal(unavailable.decoderRateHz, null);
});
test("frame aggregation rejects malformed provenance, unknown top document and oversized census", () => {
  const choose = protocol().chooseFrames;
  for (const frames of [[frame({ evidenceField: "outputRate" })], [frame({ topFrame: false })], [null]])
    assert.equal(choose(frames).available, false);
  const oversized = choose(Array.from({ length: 33 }, () => frame()));
  assert.equal(oversized.truncated, true); assert.equal(oversized.decoderRateHz, null);
});
test("native requests require exact protocol and fresh-shaped receiver nonces", () => {
  const parse = protocol().isRequest;
  assert.equal(parse({ protocolVersion: 1, kind: "request", connectionNonce: a, requestId: b }), true);
  for (const change of [{ protocolVersion: 2 }, { kind: "command" }, { requestId: "short" }, { connectionNonce: a.toUpperCase() }])
    assert.equal(parse({ protocolVersion: 1, kind: "request", connectionNonce: a, requestId: b, ...change }), false);
});
test("background does not inspect or connect before explicit Follow", async () => {
  const state = background();
  const status = await state.extension("getStatus");
  assert.equal(status.following, false); assert.equal(state.ports.length, 0); assert.equal(state.scriptCalls.length, 0);
});
test("background inspects only chosen tab and echoes host request identity without URLs", async () => {
  const state = background(); await state.extension("followActiveTab"); await state.request();
  const message = state.sent[0];
  assert.equal(message.kind, "decoderSnapshot"); assert.equal(message.tabId, 7);
  assert.equal(message.connectionNonce, b); assert.equal(message.requestId, a);
  assert.equal(message.decoderRateHz, 96000); assert.equal(message.outputRateHz, 48000);
  assert.equal(message.decoderSupported, true);
  assert.equal(message.documentNonce.length, 98); assert.equal(message.coverage, "selected-accessible-frames");
  assert.equal(state.scriptCalls.every(call => call.tabId === 7), true);
  assert.equal(Object.keys(message).some(key => /url|path|pid|title|history/i.test(key)), false);
});
test("unsupported API retains followed-tab identity but never fabricates a rate", async () => {
  const state = background();
  state.responseQueue.push([frame({ available: false, decoderSupported: false, decoderRateHz: null, outputRateHz: null })]);
  await state.extension("followActiveTab"); await state.request();
  const message = state.sent[0];
  assert.equal(message.kind, "decoderSnapshot"); assert.equal(message.decoderSupported, false);
  assert.equal(message.decoderRateHz, null); assert.equal(message.tabId, 7); assert.ok(message.documentNonce);
  assert.equal((await state.extension("getStatus")).following, true);
  await state.request(c);
  const recovered = state.sent.at(-1);
  assert.equal(recovered.kind, "decoderSnapshot"); assert.equal(recovered.decoderSupported, true);
  assert.equal(recovered.decoderRateHz, 96000); assert.ok(recovered.mediaEpoch > message.mediaEpoch);
});
test("temporary diagnostic unknown clears playing evidence while preserving supported capability", async () => {
  const state = background();
  state.responseQueue.push([frame({ available: false, decoderSupported: true, decoderRateHz: null })]);
  await state.extension("followActiveTab"); await state.request();
  assert.equal(state.sent[0].kind, "clear"); assert.equal(state.sent[0].decoderSupported, true);
  assert.equal(state.sent[0].clearReason, "unavailable");
});
test("Stop replies with an unfollowed clear before disconnecting after receiver consumption", async () => {
  const state = background(); await state.extension("followActiveTab"); await state.request();
  await state.extension("stopFollowing"); await state.request(c);
  assert.equal(state.sent.at(-1).kind, "clear"); assert.equal(state.sent.at(-1).clearReason, "unfollowed");
  assert.equal(state.ports[0].disconnected, false);
  await state.request(a);
  assert.equal(state.ports[0].disconnected, true);
});
test("navigation during an outstanding query produces clear rather than late old decoder evidence", async () => {
  const pending = deferred(), state = background();
  state.responseQueue.push(pending.promise);
  await state.extension("followActiveTab");
  await state.ports[0].onMessage.emit({ protocolVersion: 1, kind: "request", connectionNonce: b, requestId: a });
  await Promise.resolve(); await state.updated.emit(7, { status: "loading", url: "must-not-be-retained" });
  pending.resolve([frame({ decoderRateHz: 192000 })]);
  for (let index = 0; index < 12; index++) await new Promise(resolve => setImmediate(resolve));
  assert.equal(state.sent[0].kind, "clear"); assert.equal(state.sent[0].clearReason, "navigation");
  await state.request(c);
  const fresh = state.sent.at(-1);
  assert.equal(fresh.kind, "decoderSnapshot"); assert.equal(fresh.decoderRateHz, 96000);
  assert.equal(JSON.stringify(fresh).includes("must-not-be-retained"), false);
});
test("re-Follow to the same document creates a new selection identity", async () => {
  const state = background(); await state.extension("followActiveTab"); await state.request();
  const first = state.sent[0];
  await state.extension("followActiveTab"); await state.request(c);
  assert.notEqual(state.sent.at(-1).documentNonce, first.documentNonce);
  assert.ok(state.sent.at(-1).sequence > first.sequence);
});
test("tab closure and denied access clear evidence honestly", async () => {
  const state = background(); await state.extension("followActiveTab");
  state.responseQueue.push(new Error("denied")); await state.request();
  assert.equal(state.sent[0].clearReason, "unavailable");
  await state.removed.emit(7); await state.request(c);
  assert.equal(state.sent.at(-1).clearReason, "tabClosed");
});
test("content scripts cannot invoke Follow or native commands", async () => {
  const state = background();
  await state.contentMessage({ kind: "followActiveTab" });
  assert.equal(state.ports.length, 0); assert.equal(state.scriptCalls.length, 0);
});
test("Stop cancels a Follow action whose active-tab query is still pending", async () => {
  const pending = deferred(), state = background();
  state.setQuery(() => pending.promise);
  const follow = state.extension("followActiveTab"); await Promise.resolve();
  await state.extension("stopFollowing"); pending.resolve([{ id: 7 }]); await follow;
  assert.equal((await state.extension("getStatus")).following, false);
  assert.equal(state.ports.length, 0); assert.equal(state.scriptCalls.length, 0);
});
test("paused responses stay explicitly paused and do not keep querying decoders", async () => {
  const state = background();
  state.responseQueue.push([frame({ playingCount: 0, paused: true })]);
  await state.extension("followActiveTab"); await state.request();
  const scripts = state.scriptCalls.length;
  await state.request(c);
  assert.equal(state.scriptCalls.length, scripts);
  assert.equal(state.sent.at(-1).paused, true);
  assert.equal(state.sent.at(-1).documentNonce, state.sent[0].documentNonce);
});
test("unsupported native request disconnects without accepting commands or fabricated evidence", async () => {
  const state = background(); await state.extension("followActiveTab");
  await state.ports[0].onMessage.emit({ protocolVersion: 1, kind: "setEndpoint", connectionNonce: b, requestId: a });
  assert.equal(state.ports[0].disconnected, true); assert.equal(state.sent.length, 0);
  assert.equal((await state.extension("getStatus")).following, false);
});
test("known-rate fixtures encode 44.1/48/96/192 kHz headers and actual stereo PCM dimensions", async () => {
  const stubElement = () => ({ append() {}, addEventListener() {}, pause() {} });
  const context = vm.createContext({ document: { createElement: stubElement, getElementById: stubElement },
    Blob, URL: { createObjectURL: () => "blob:owned-fixture" } });
  vm.runInContext(fixtureSource, context);
  for (const rate of [44100, 48000, 96000, 192000]) {
    const blob = vm.runInContext(`makeWav(${rate})`, context), bytes = Buffer.from(await blob.arrayBuffer());
    assert.equal(bytes.toString("ascii", 0, 4), "RIFF"); assert.equal(bytes.readUInt16LE(20), 1);
    assert.equal(bytes.readUInt16LE(22), 2); assert.equal(bytes.readUInt32LE(24), rate);
    assert.equal(bytes.readUInt32LE(28), rate * 4); assert.equal(bytes.length, 44 + rate * 5 * 4);
  }
});
