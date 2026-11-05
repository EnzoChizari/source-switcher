import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import { test } from "node:test";
const source = await readFile(new URL("popup.js", import.meta.url), "utf8");
const flush = async () => { for (let i = 0; i < 2; i++) await new Promise(resolve => setImmediate(resolve)); };
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

function permissionPopup(accepted = true) {
  const elements = new Map(), requests = [], messages = [], removals = [];
  for (const id of ["status", "follow", "stop", "fixtures", "auto-follow", "site-access", "all-sites", "site-status"])
    elements.set(id, { textContent: "", listeners: {}, addEventListener(name, handler) { this.listeners[name] = handler; } });
  let granted = false, calls = 0;
  vm.runInNewContext(source, { URL, document: { getElementById: id => elements.get(id) },
    chrome: {
      runtime: { sendMessage: async message => { messages.push(message); return { status: "Ready" }; } },
      tabs: { query: async () => [{ url: "https://player.example.org/private/video" }] },
      permissions: {
        contains: async () => granted,
        request: value => { calls++; requests.push(value); granted = accepted; return Promise.resolve(accepted); },
        remove: async value => { removals.push(value); granted = false; return true; }
      }
    },
    setInterval() {}, clearInterval() {}, window: { addEventListener() {} } });
  return { elements, requests, messages, removals, calls: () => calls };
}
test("site permission is requested inside the click gesture with only the current host", async () => {
  const state = permissionPopup(); await flush();
  state.elements.get("site-access").listeners.click();
  assert.equal(state.calls(), 1); // no awaited lookup before the request
  assert.equal(state.requests[0].origins[0], "https://player.example.org/*");
  await flush(); assert.ok(state.messages.some(value => value.kind === "syncSiteAccess"));
  assert.match(state.elements.get("site-status").textContent, /Reload the player/);
});
test("declining all-sites access resets the checkbox; disabling removes the grant", async () => {
  const denied = permissionPopup(false); await flush();
  denied.elements.get("all-sites").checked = true;
  denied.elements.get("all-sites").listeners.change(); await flush();
  assert.equal(denied.elements.get("all-sites").checked, false);
  const state = permissionPopup(); await flush();
  state.elements.get("all-sites").checked = true; state.elements.get("all-sites").listeners.change(); await flush();
  assert.equal(state.requests[0].origins.length, 2);
  state.elements.get("all-sites").checked = false; state.elements.get("all-sites").listeners.change(); await flush();
  assert.equal(state.removals[0].origins.length, 2);
  assert.match(state.elements.get("site-status").textContent, /remove earlier page hooks/);
});
