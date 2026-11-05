import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import { test } from "node:test";
const source = await readFile(new URL("mse-observer.js", import.meta.url), "utf8");
function setup() {
  let now = 0, timer, cleared = false, media = [];
  class Buffer { appendBuffer(data) { if (data === "throw") throw Error("playback error"); return 123; } changeType() { return 456; } }
  class Source { activeSourceBuffers = []; addSourceBuffer() { const buffer = new Buffer(); this.activeSourceBuffers.push(buffer); return buffer; } }
  class Media { paused = false; volume = 1; isConnected = true; play() { return Promise.resolve(789); } }
  const url = { createObjectURL: () => "blob:owned-stream" };
  const original = { append: Buffer.prototype.appendBuffer, add: Source.prototype.addSourceBuffer, play: Media.prototype.play, create: url.createObjectURL };
  const context = vm.createContext({ MediaSource: Source, SourceBuffer: Buffer, HTMLMediaElement: Media, URL: url,
    Uint8Array, ArrayBuffer, WeakRef, performance: { now: () => now },
    document: { querySelectorAll: () => media }, readPlayingMediaHeader: () => ({}),
    readStreamHeader: bytes => bytes.length >= 12 && bytes[8] === 42 ? 48000 : null,
    setInterval: handler => { timer = handler; return 1; }, clearInterval: () => { cleared = true; } });
  vm.runInContext(source, context);
  return { Source, Buffer, Media, original, context, url,
    observer: vm.runInContext('globalThis[Symbol.for("source-switcher.mse-observer.v1")]', context),
    setMedia: value => { media = value; }, time: value => { now = value; },
    tick: () => timer(), cleared: () => cleared };
}
const init = () => Uint8Array.of(0, 0, 0, 12, 109, 111, 111, 118, 42, 0, 0, 0);
function player(state) {
  const source = new state.Source(), buffer = source.addSourceBuffer("audio/mp4");
  const media = new state.Media(); media.currentSrc = state.url.createObjectURL(source); state.setMedia([media]);
  return { media, buffer, source };
}
test("document-start observation retains a header before Follow without a timer", () => {
  const state = setup(); state.observer.retain(); assert.equal(state.cleared(), true);
  state.time(60000); const { buffer } = player(state);
  assert.equal(buffer.appendBuffer(init()), 123);
  assert.equal(state.observer.read().rate, 48000);
});
test("ordinary Follow expires and restores only its own API wrappers", () => {
  const state = setup(); state.time(3000); state.tick();
  assert.equal(state.Buffer.prototype.appendBuffer, state.original.append);
  assert.equal(state.context[Symbol.for("source-switcher.mse-observer.v1")], undefined);
  const other = setup(), thirdParty = () => {};
  other.Buffer.prototype.appendBuffer = thirdParty; other.time(3000); other.tick();
  assert.equal(other.Buffer.prototype.appendBuffer, thirdParty);
});
test("split initialization is joined within a fixed bound; unknown new init clears old metadata", () => {
  const state = setup(), { buffer } = player(state), bytes = init();
  buffer.appendBuffer(bytes.subarray(0, 8)); assert.equal(state.observer.read().rate, undefined);
  buffer.appendBuffer(bytes.subarray(8)); assert.equal(state.observer.read().rate, 48000);
  const unknown = init(); unknown[8] = 0; buffer.appendBuffer(unknown);
  assert.equal(state.observer.read().rate, undefined);
});
test("changeType, DRM, srcObject and competing players cannot reuse a previous rate", () => {
  const state = setup(), { media, buffer } = player(state); buffer.appendBuffer(init());
  media.mediaKeys = {}; assert.equal(state.observer.read().rate, undefined);
  media.mediaKeys = null; media.srcObject = {}; assert.equal(state.observer.read().rate, undefined);
  media.srcObject = null; buffer.changeType("audio/webm"); assert.equal(state.observer.read().rate, undefined);
  state.setMedia([media, new state.Media()]); assert.equal(state.observer.read().conflict, true);
  state.setMedia([]); assert.equal(state.observer.read().paused, true);
});
test("instrumentation preserves playback results and exceptions", async () => {
  const state = setup(), { media, buffer } = player(state);
  assert.throws(() => buffer.appendBuffer("throw"), /playback error/);
  assert.equal(await media.play(), 789); assert.equal(buffer.changeType("video/mp4"), 456);
  state.observer.dispose(); assert.equal(state.url.createObjectURL, state.original.create);
});
