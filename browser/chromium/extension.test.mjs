import assert from "node:assert/strict";
import { webcrypto, createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import { test } from "node:test";

const [protocolSource, backgroundSource, fixtureSource, manifestSource, headerSource] = await Promise.all(
  ["protocol.js", "background.js", "fixture.js", "manifest.json", "media-header.js"].map(file => readFile(new URL(file, import.meta.url), "utf8")));
const a = "a".repeat(32), b = "b".repeat(32), c = "c".repeat(32);
const flush = async () => { for (let index = 0; index < 12; index++) await new Promise(resolve => setImmediate(resolve)); };
function deferred() { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
function event() { const listeners = []; return { addListener: listener => listeners.push(listener), emit: (...args) => listeners.map(listener => listener(...args)) }; }
function protocol() { const context = vm.createContext({ crypto: webcrypto }); vm.runInContext(protocolSource, context); return context.DX5ChromiumProtocol; }
function background(frameOptions) {
  const ports = [], sent = [], lookups = [], timers = new Map();
  let nextTimer = 0, query = async () => [{ id: 7, title: "Selected media tab", url: "https://must-not-leave.invalid/private" }];
  let get = async id => ({ id, title: "Selected media tab", audible: true, url: "https://must-not-leave.invalid/private" });
  let probe = async () => [];
  const messages = event(), updated = event(), removed = event(), activated = event(), focused = event(), connected = event();
  const fixtureUrl = "chrome-extension://fixture-id/fixture.html";
  const chrome = {
    runtime: { id: "fixture-id", getURL: () => fixtureUrl, getManifest: () => JSON.parse(manifestSource),
      onMessage: messages, onConnect: connected, connectNative: name => {
      assert.equal(name, "local.dx5_source_switcher");
      const port = { sent: [], disconnected: false, onMessage: event(), onDisconnect: event(),
        postMessage(message) { this.sent.push(message); sent.push(message); },
        disconnect() { this.disconnected = true; this.onDisconnect.emit(); } };
      ports.push(port); return port;
    } },
    tabs: { query: (...args) => query(...args), get: id => { lookups.push(id); return get(id); },
      onUpdated: updated, onRemoved: removed, onActivated: activated },
    windows: { onFocusChanged: focused },
    scripting: { executeScript: (...args) => probe(...args) }
  };
  if (frameOptions) {
    chrome.permissions = { contains: async () => frameOptions.granted, onAdded: event(), onRemoved: event() };
    chrome.webNavigation = { getAllFrames: async ({ tabId }) => { assert.equal(tabId, 7); return frameOptions.frames; } };
  }
  const context = vm.createContext({ chrome, crypto: webcrypto, URL,
    setTimeout: (callback, ms) => { const id = ++nextTimer; timers.set(id, { callback, ms }); return id; },
    clearTimeout: id => timers.delete(id) });
  context.importScripts = (...files) => { for (const file of files) vm.runInContext(file === "protocol.js" ? protocolSource : headerSource, context); };
  vm.runInContext(backgroundSource, context);
  return { ports, sent, lookups, updated, removed, activated, focused, connected, fixtureUrl,
    extension: (kind, fields = {}) => new Promise(resolve => { messages.emit({ kind, ...fields }, {}, resolve); }),
    contentMessage: message => messages.emit(message, { tab: { id: 7 } }, () => { throw new Error("Content script cannot invoke actions"); }),
    setQuery: value => { query = value; }, setGet: value => { get = value; }, setProbe: value => { probe = value; },
    request: async (id = a) => { ports.at(-1).onMessage.emit({ protocolVersion: 1, kind: "request", connectionNonce: b, requestId: id }); await flush(); },
    timeout: milliseconds => { for (const [id, timer] of [...timers]) if (timer.ms === milliseconds) { timers.delete(id); timer.callback(); } }
  };
}

test("embedded Kodik probes only allowed followed-tab documents and holds on competing audio", async () => {
  const frames = [{ frameId: 1, documentId: "kodik", url: "https://player.kodikplayer.com/video" },
    { frameId: 2, documentId: "unrelated", url: "https://kodikplayer.com.example.invalid/track" }];
  const state = background({ granted: true, frames });
  let mainPlaying = false;
  const documents = new Set();
  state.setProbe(async ({ target }) => {
    assert.equal(target.tabId, 7);
    const id = target.documentIds?.[0] ?? "main";
    documents.add(id);
    return [{ result: id === "kodik" ? { rate: 48000, streaming: true } : mainPlaying ? { rate: 44100 } : { paused: true } }];
  });
  await state.extension("followActiveTab"); await state.request();
  assert.deepEqual([...documents].sort(), ["kodik", "main"]);
  assert.equal(state.sent.at(-1).decoderRateHz, 48000);
  assert.equal(state.sent.at(-1).evidenceField, "stream-init.header.sampleRate");
  mainPlaying = true; await state.request();
  assert.equal(state.sent.at(-1).conflict, true);
  assert.equal(state.sent.at(-1).decoderRateHz, null);

  const denied = background({ granted: false, frames });
  denied.setProbe(async ({ target }) => {
    assert.equal(target.documentIds, undefined);
    return [{ result: { paused: true } }];
  });
  await denied.extension("followActiveTab"); await denied.request();
  assert.equal(denied.sent.at(-1).decoderRateHz, null);
});

test("manifest limits script access to user-granted active tabs, without debugger or host permissions", () => {
  const manifest = JSON.parse(manifestSource);
  assert.equal(manifest.manifest_version, 3); assert.deepEqual(manifest.permissions, ["nativeMessaging", "tabs", "activeTab", "scripting"]);
  for (const name of ["host_permissions", "content_scripts", "externally_connectable", "web_accessible_resources"]) assert.equal(manifest[name], undefined);
  assert.equal(manifest.background.service_worker, "background.js");
  assert.equal(createHash("sha256").update(Buffer.from(manifest.key, "base64")).digest("hex").slice(0, 32)
    .replace(/./g, value => String.fromCharCode(97 + parseInt(value, 16))), "igialjjnjplkdmieelilccmcnkbbgchp");
});
test("auto-follow changes tab identity and clearing the checkbox keeps the current link", async () => {
  const state = background();
  await state.extension("setAutoFollow", { enabled: true });
  await state.request();
  assert.equal(state.sent.at(-1).tabId, 7);
  state.setQuery(async () => [{ id: 9, title: "New active tab" }]);
  state.activated.emit({ tabId: 9 }); await flush();
  await state.request();
  assert.equal(state.sent.at(-1).clearReason, "navigation");
  await state.request();
  assert.equal(state.sent.at(-1).tabId, 9);
  await state.extension("setAutoFollow", { enabled: false });
  state.setQuery(async () => [{ id: 12 }]);
  state.focused.emit(2); await flush();
  await state.request();
  assert.equal(state.sent.at(-1).tabId, 9);
  await state.extension("stopFollowing");
  assert.equal((await state.extension("getStatus")).autoFollow, false);
});
test("a header rate is separate from output and pause holds only previously observed media", async () => {
  const state = background();
  state.setProbe(async () => [{ result: { rate: 96000 } }]);
  await state.extension("followActiveTab"); await state.request();
  assert.equal(state.sent.at(-1).evidenceField, "direct-media.header.sampleRate");
  assert.equal(state.sent.at(-1).decoderRateHz, 96000);
  assert.equal(state.sent.at(-1).outputRateHz, null);
  state.setProbe(async () => [{ result: { paused: true } }]); await state.request();
  assert.equal(state.sent.at(-1).paused, true);
  assert.equal(state.sent.at(-1).decoderSupported, true);
  assert.equal(state.sent.at(-1).decoderRateHz, null);
});
test("only the owned PCM page can report a playing fixture for its linked tab", async () => {
  const state = background(), onMessage = event(), onDisconnect = event();
  const connection = { name: "pcm-demo", sender: { id: "fixture-id", url: state.fixtureUrl, tab: { id: 7 } }, onMessage, onDisconnect };
  state.setGet(async id => ({ id, url: state.fixtureUrl }));
  state.connected.emit({ ...connection, sender: { ...connection.sender, url: "https://example.invalid/fixture.html" } });
  onMessage.emit({ rate: 192000 });
  await state.extension("followActiveTab"); await state.request();
  assert.equal(state.sent.at(-1).decoderRateHz, null);
  state.connected.emit(connection); onMessage.emit({ rate: 192000 });
  await state.request();
  assert.equal(state.sent.at(-1).decoderRateHz, 192000);
  onMessage.emit({ paused: true }); await state.request();
  assert.equal(state.sent.at(-1).paused, true);
  onDisconnect.emit(); await state.request();
  assert.equal(state.sent.at(-1).decoderRateHz, null);
});
test("a native FLAC STREAMINFO header supplies the rate without repeated reads", async () => {
  const data = new Uint8Array(42);
  data.set([102, 76, 97, 67, 128, 0, 0, 34]);
  new DataView(data.buffer).setUint32(18, (192000 << 12) | (23 << 4));
  const media = { currentSrc: "https://example.invalid/track.flac", volume: 1 };
  let requests = 0;
  const context = vm.createContext({ URL, Uint8Array, DataView, AbortSignal,
    document: { querySelectorAll: () => [media] }, location: { origin: "https://example.invalid" },
    fetch: async (_, options) => {
      requests++;
      assert.equal(options.credentials, "omit");
      assert.equal(options.headers.Range, "bytes=0-4095");
      return new Response(data);
    } });
  vm.runInContext(headerSource, context);
  assert.equal((await context.readPlayingMediaHeader()).rate, 192000);
  assert.equal((await context.readPlayingMediaHeader()).bits, 24);
  assert.equal((await context.readPlayingMediaHeader()).rate, 192000);
  assert.equal(requests, 1);
  media.currentSrc = "https://other.invalid/track.bin";
  assert.equal((await context.readPlayingMediaHeader()).rate, undefined);
  assert.equal(requests, 1);
});
test("only integer WAV supplies a depth, including valid bits in an extensible container", async () => {
  for (const [encoding, container, bits, expected] of [[1, 16, 16, 16], [1, 32, 32, 32], [3, 32, 32, undefined], [0xfffe, 32, 24, 24]]) {
    const data = new Uint8Array(encoding === 0xfffe ? 60 : 36);
    const view = new DataView(data.buffer);
    data.set(Buffer.from("RIFF"), 0); data.set(Buffer.from("WAVEfmt "), 8);
    view.setUint32(16, data.length - 20, true);
    view.setUint16(20, encoding, true); view.setUint16(22, 2, true);
    view.setUint32(24, 96000, true); view.setUint16(32, container / 4, true); view.setUint16(34, container, true);
    if (encoding === 0xfffe) {
      view.setUint16(36, 22, true); view.setUint16(38, bits, true);
      data.set([1, 0, 0, 0, 0, 0, 16, 0, 128, 0, 0, 170, 0, 56, 155, 113], 44);
    }
    const media = { currentSrc: "https://example.invalid/track.wav", volume: 1 };
    const context = vm.createContext({ URL, Uint8Array, DataView, AbortSignal,
      document: { querySelectorAll: () => [media] }, location: { origin: "https://example.invalid" },
      fetch: async () => new Response(data) });
    vm.runInContext(headerSource, context);
    const format = await context.readPlayingMediaHeader();
    assert.equal(format.rate, 96000);
    assert.equal(format.bits, expected);
  }
});

test("file depth is sent only to a receiver requesting it and never for stream headers", () => {
  const request = { connectionNonce: a, requestId: b }, selection = { tabId: 7, documentNonce: a };
  const header = { rate: 96000, bits: 24 };
  const reply = (request, evidence) => protocol().reply(request, 1, c, selection, null, evidence);
  assert.equal(reply(request, header).fileBitsPerSample, undefined);
  assert.equal(reply({ ...request, fileBitDepth: true }, header).fileBitsPerSample, 24);
  assert.equal(reply({ ...request, fileBitDepth: true }, { ...header, streaming: true }).fileBitsPerSample, undefined);
});
test("a transient header failure retries once per media URL and caches the result", async () => {
  const data = new Uint8Array(42);
  data.set([102, 76, 97, 67, 128, 0, 0, 34]);
  new DataView(data.buffer).setUint32(18, 96000 << 12);
  const media = { currentSrc: "https://example.invalid/track.flac", volume: 1 };
  let now = 0, requests = 0;
  const context = vm.createContext({ URL, Uint8Array, DataView, AbortSignal, Date: { now: () => now },
    document: { querySelectorAll: () => [media] }, location: { origin: "https://example.invalid" },
    fetch: async () => {
      requests++;
      return requests === 2 ? new Response(data) : new Response(null, { status: 503 });
    } });
  vm.runInContext(headerSource, context);
  const read = () => context.readPlayingMediaHeader();
  assert.equal((await read()).rate, undefined);
  now = 999;
  assert.equal((await read()).rate, undefined);
  assert.equal(requests, 1);
  now = 1000;
  const retried = await Promise.all([read(), read()]);
  assert.deepEqual(retried.map(result => result.rate), [96000, 96000]);
  assert.equal(requests, 2);
  media.currentSrc = "https://example.invalid/other.flac";
  await read();
  now = 2000;
  await read();
  now = 10000;
  await read();
  assert.equal(requests, 4);
});
test("requests require exact protocol, kind and 128-bit lowercase receiver nonces", () => {
  const parse = protocol().isRequest, valid = { protocolVersion: 1, kind: "request", connectionNonce: a, requestId: b };
  assert.equal(parse(valid), true);
  for (const change of [{ protocolVersion: 2 }, { kind: "command" }, { requestId: "short" }, { connectionNonce: a.toUpperCase() }, { requestId: 1 }])
    assert.equal(parse({ ...valid, ...change }), false);
});
test("unsupported snapshots never supply decoder or output rates", () => {
  const reply = protocol().reply({ connectionNonce: a, requestId: b }, 1, c,
    { tabId: 7, documentNonce: a, mediaEpoch: 2, title: "title", decoderRateHz: 192000, outputRateHz: 48000 });
  assert.equal(reply.kind, "decoderSnapshot"); assert.equal(reply.decoderSupported, false);
  assert.equal(reply.decoderRateHz, null); assert.equal(reply.outputRateHz, null);
  assert.equal(reply.paused, false); assert.equal(reply.conflict, false);
  assert.equal(reply.evidenceField, "cdp.Media.kAudioTracks.samples per second");
  assert.equal(reply.coverage, "selected-cdp-target");
  assert.equal(reply.documentNonce, a); assert.equal(reply.mediaEpoch, 2);
});
test("titles remove controls, cap 160 characters, and reject non-strings", () => {
  const title = protocol().title;
  assert.equal(title(" \u0000Secret\ntrack\t "), "Secret track");
  assert.equal(title("a".repeat(300)).length, 160); assert.equal(title({ url: "private" }), null);
});
test("background neither connects nor reads tabs before explicit Follow", async () => {
  const state = background(), status = await state.extension("getStatus");
  assert.equal(status.following, false); assert.equal(status.decoderSupported, false);
  assert.equal(state.ports.length, 0); assert.equal(state.lookups.length, 0);
});
test("Follow opens only a native connection and waits for a receiver request", async () => {
  const state = background(); await state.extension("followActiveTab");
  assert.equal(state.ports.length, 1); assert.equal(state.sent.length, 0); assert.equal(state.lookups.length, 0);
});
test("native response pins selected tab and nonces without URLs, paths, PIDs or claimed rates", async () => {
  const state = background(); await state.extension("followActiveTab"); await state.request();
  const message = state.sent[0];
  assert.equal(message.tabId, 7); assert.equal(message.title, "Selected media tab");
  assert.equal(message.connectionNonce, b); assert.equal(message.requestId, a); assert.equal(message.sequence, 1);
  assert.match(message.browserInstanceNonce, /^[a-f0-9]{32}$/); assert.match(message.documentNonce, /^[a-f0-9]{32}$/);
  assert.equal(message.decoderSupported, false); assert.equal(message.decoderRateHz, null); assert.equal(message.outputRateHz, null);
  assert.deepEqual(state.lookups, [7]);
  assert.equal(/must-not-leave|https:|executablePath|processId|endpointId/.test(JSON.stringify(message)), false);
});
test("identity-only snapshots do not infer pause or content rate from audible, title or URL", async () => {
  const state = background(); state.setGet(async id => ({ id, audible: false, title: "192 kHz source", url: "https://example.invalid/192000.wav" }));
  await state.extension("followActiveTab"); await state.request();
  assert.equal(state.sent[0].paused, false); assert.equal(state.sent[0].decoderRateHz, null); assert.equal(state.sent[0].decoderSupported, false);
});
test("sequences increase while repeated reads preserve the same selection identity", async () => {
  const state = background(); await state.extension("followActiveTab"); await state.request(); await state.request(c);
  assert.equal(state.sent[1].sequence, state.sent[0].sequence + 1);
  assert.equal(state.sent[1].documentNonce, state.sent[0].documentNonce);
  assert.equal(state.sent[1].browserInstanceNonce, state.sent[0].browserInstanceNonce);
});
test("Stop sends a clear before disconnecting when receiver asks again", async () => {
  const state = background(); await state.extension("followActiveTab"); await state.request();
  await state.extension("stopFollowing"); await state.request(c);
  const clear = state.sent.at(-1); assert.equal(clear.kind, "clear"); assert.equal(clear.clearReason, "unfollowed");
  assert.equal(clear.documentNonce, null); assert.equal(clear.decoderRateHz, null); assert.equal(clear.decoderSupported, false);
  assert.equal(state.ports[0].disconnected, false); await state.request(); assert.equal(state.ports[0].disconnected, true);
});
test("Stop disconnects after a bounded wait if no native request consumes the clear", async () => {
  const state = background(); await state.extension("followActiveTab"); await state.extension("stopFollowing");
  state.timeout(3000); assert.equal(state.ports[0].disconnected, true);
});
test("navigation invalidates the document and first clears before publishing fresh identity", async () => {
  const state = background(); await state.extension("followActiveTab"); await state.request(); const first = state.sent[0];
  state.updated.emit(7, { status: "loading", url: "https://private-navigation.invalid/secret" }); await state.request(c);
  assert.equal(state.sent.at(-1).kind, "clear"); assert.equal(state.sent.at(-1).clearReason, "navigation");
  await state.request(); const fresh = state.sent.at(-1);
  assert.equal(fresh.kind, "decoderSnapshot"); assert.notEqual(fresh.documentNonce, first.documentNonce);
  assert.ok(fresh.mediaEpoch > first.mediaEpoch); assert.equal(JSON.stringify(fresh).includes("private-navigation"), false);
});
test("late tab lookup cannot republish identity after navigation", async () => {
  const pending = deferred(), state = background(); state.setGet(() => pending.promise);
  await state.extension("followActiveTab"); await state.request();
  state.updated.emit(7, { status: "loading" }); pending.resolve({ id: 7, title: "Old late title" }); await flush();
  assert.equal(state.sent[0].kind, "clear"); assert.equal(state.sent[0].clearReason, "navigation");
  assert.equal(JSON.stringify(state.sent).includes("Old late title"), false);
});
test("late tab lookup cannot republish identity after Stop", async () => {
  const pending = deferred(), state = background(); state.setGet(() => pending.promise);
  await state.extension("followActiveTab"); await state.request(); await state.extension("stopFollowing");
  pending.resolve({ id: 7, title: "Old late title" }); await flush();
  assert.equal(state.sent[0].kind, "clear"); assert.equal(state.sent[0].clearReason, "unfollowed");
});
test("re-Follow makes a new document identity even in the same tab", async () => {
  const state = background(); await state.extension("followActiveTab"); await state.request(); const first = state.sent[0];
  await state.extension("followActiveTab"); await state.request(c);
  assert.notEqual(state.sent.at(-1).documentNonce, first.documentNonce); assert.equal(state.ports.length, 1);
});
test("Stop cancels a Follow whose active-tab query has not returned", async () => {
  const pending = deferred(), state = background(); state.setQuery(() => pending.promise);
  const follow = state.extension("followActiveTab"); await Promise.resolve(); await state.extension("stopFollowing");
  pending.resolve([{ id: 7, title: "old" }]); await follow;
  assert.equal((await state.extension("getStatus")).following, false); assert.equal(state.ports.length, 0);
});
test("closed followed tabs clear; other tabs cannot affect the link", async () => {
  const state = background(); await state.extension("followActiveTab"); state.removed.emit(10); state.updated.emit(10, { status: "loading" }); await state.request();
  assert.equal(state.sent[0].kind, "decoderSnapshot");
  state.removed.emit(7); await state.request(c); assert.equal(state.sent.at(-1).clearReason, "tabClosed");
});
test("unknown tabs and denied tab lookups produce clear rather than fabricated evidence", async () => {
  for (const getter of [async () => null, async () => { throw new Error("denied"); }]) {
    const state = background(); state.setGet(getter); await state.extension("followActiveTab"); await state.request();
    assert.equal(state.sent[0].kind, "clear"); assert.equal(state.sent[0].decoderRateHz, null);
  }
});
test("hung tab lookup times out without accepting a later response", async () => {
  const pending = deferred(), state = background(); state.setGet(() => pending.promise);
  await state.extension("followActiveTab"); await state.request(); state.timeout(1250); await flush();
  assert.equal(state.sent[0].kind, "clear"); assert.equal(state.sent[0].clearReason, "unavailable");
  pending.resolve({ id: 7, title: "Too late" }); await flush(); assert.equal(state.sent.length, 1);
});
test("overlapping native requests disconnect and late lookups cannot reply", async () => {
  const pending = deferred(), state = background(); state.setGet(() => pending.promise);
  await state.extension("followActiveTab"); await state.request(); await state.request(c);
  assert.equal(state.ports[0].disconnected, true); pending.resolve({ id: 7 }); await flush(); assert.equal(state.sent.length, 0);
});
test("unsupported commands disconnect without sending messages", async () => {
  const state = background(); await state.extension("followActiveTab");
  state.ports[0].onMessage.emit({ protocolVersion: 1, kind: "setEndpoint", connectionNonce: b, requestId: a }); await flush();
  assert.equal(state.ports[0].disconnected, true); assert.equal(state.sent.length, 0);
});
test("content scripts cannot select tabs or open the native host", async () => {
  const state = background(); state.contentMessage({ kind: "followActiveTab" }); await flush();
  assert.equal(state.ports.length, 0); assert.equal(state.lookups.length, 0);
});
test("native disconnect drops selection and does not reconnect automatically", async () => {
  const state = background(); await state.extension("followActiveTab"); state.ports[0].onDisconnect.emit();
  assert.equal((await state.extension("getStatus")).following, false); assert.equal(state.ports.length, 1);
});
test("fixtures encode actual 16/24/32-bit stereo PCM at 44.1/48/96/192 kHz", async () => {
  const stubElement = () => ({ append() {}, addEventListener() {}, pause() {} });
  const context = vm.createContext({ document: { createElement: stubElement, getElementById: stubElement }, Blob,
    chrome: { runtime: { connect: () => ({ onDisconnect: event(), postMessage() {} }) } },
    URL: { createObjectURL: () => "blob:owned-fixture" } });
  vm.runInContext(fixtureSource, context);
  for (const [rate, bits] of [[44100, 16], [48000, 16], [96000, 24], [192000, 32]]) {
    const blob = vm.runInContext(`makeWav(${rate}, 1, ${bits})`, context), bytes = Buffer.from(await blob.arrayBuffer());
    const blockAlign = bits / 4;
    assert.equal(bytes.toString("ascii", 0, 4), "RIFF"); assert.equal(bytes.readUInt16LE(20), 1);
    assert.equal(bytes.readUInt16LE(22), 2); assert.equal(bytes.readUInt32LE(24), rate);
    assert.equal(bytes.readUInt16LE(34), bits); assert.equal(bytes.readUInt32LE(28), rate * blockAlign);
    assert.equal(bytes.length, 44 + rate * blockAlign); assert.ok(bytes.subarray(44).some(value => value !== 0));
  }
  assert.equal(fixtureSource.includes("AudioContext"), false); assert.equal(fixtureSource.includes("mozRequestDebugInfo"), false);
});
