import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import { test } from "node:test";
const source = await readFile(new URL("popup.js", import.meta.url), "utf8");
const flush = async () => { for (let i = 0; i < 5; i++) await Promise.resolve(); };
function setup() {
  const elements = new Map(), requests = [];
  let tick, unload, cleared = false;
  for (const id of ["status", "follow", "stop", "fixtures", "auto-follow"]) elements.set(id, { textContent: "", listeners: {}, addEventListener(name, handler) { this.listeners[name] = handler; } });
  vm.runInNewContext(source, { document: { getElementById: id => elements.get(id) },
    chrome: { runtime: { sendMessage: message => new Promise(resolve => requests.push({ message, resolve })) } },
    setInterval: handler => { tick = handler; return 123; }, clearInterval: id => { assert.equal(id, 123); cleared = true; },
    window: { addEventListener: (_, handler) => { unload = handler; } } });
  return { elements, requests, tick: () => tick(), unload: () => unload(), cleared: () => cleared };
}
test("an asynchronous native failure appears while the popup remains open", async () => {
  const state = setup(); state.requests[0].resolve({ status: "Linking", following: true }); await flush();
  state.tick(); state.requests[1].resolve({ status: "Native bridge unavailable: host not found", following: false }); await flush();
  assert.equal(state.elements.get("status").textContent, "Native bridge unavailable: host not found");
  assert.equal(state.elements.get("stop").disabled, true);
});
test("stale status reads cannot overwrite a newer Follow action", async () => {
  const state = setup(); state.requests[0].resolve({ status: "Old", following: false }); await flush();
  state.tick(); state.elements.get("follow").listeners.click();
  state.requests[2].resolve({ status: "New Follow", following: true }); await flush();
  state.requests[1].resolve({ status: "Stale disconnected", following: false }); await flush();
  assert.equal(state.elements.get("status").textContent, "New Follow");
});
test("status polling neither overlaps itself nor an explicit command", async () => {
  const state = setup(); state.tick(); assert.equal(state.requests.length, 1);
  state.requests[0].resolve({ status: "Ready" }); await flush();
  state.tick(); state.tick(); assert.equal(state.requests.length, 2);
});
test("closing the popup clears its status timer", () => { const state = setup(); state.unload(); assert.equal(state.cleared(), true); });
