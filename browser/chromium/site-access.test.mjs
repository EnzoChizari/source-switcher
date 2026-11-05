import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import { test } from "node:test";
const source = await readFile(new URL("site-access.js", import.meta.url), "utf8");
function setup() {
  let permissions = { origins: [], permissions: ["webNavigation"] }, scripts = [], frames = [], fail = false;
  const changes = [];
  const context = vm.createContext({ URL, chrome: {
    permissions: { getAll: async () => permissions },
    scripting: {
      getRegisteredContentScripts: async () => scripts,
      registerContentScripts: async value => { if (fail) throw Error("denied"); changes.push("register"); scripts = value; },
      updateContentScripts: async value => { changes.push("update"); scripts = value; },
      unregisterContentScripts: async () => { changes.push("remove"); scripts = []; }
    },
    webNavigation: { getAllFrames: async () => frames }
  } });
  vm.runInContext(source, context);
  return { api: context.DX5SiteAccess, changes, scripts: () => scripts,
    grants: value => { permissions = value; }, frames: value => { frames = value; }, fail: value => { fail = value; } };
}
test("early scripts persist only for granted HTTP sites and unregister after revocation", async () => {
  const state = setup(); await state.api.sync(); assert.equal(state.scripts().length, 0);
  state.grants({ origins: ["https://*.example.org/*", "file:///*"], permissions: [] });
  await state.api.sync();
  const [script] = state.scripts();
  assert.equal(script.runAt, "document_start"); assert.equal(script.world, "MAIN");
  assert.equal(script.allFrames, true); assert.equal(script.matchOriginAsFallback, true);
  assert.equal(script.persistAcrossSessions, true);
  assert.deepEqual(Array.from(script.matches), ["https://*.example.org/*"]);
  assert.equal(script.js.at(-1), "early-media.js");
  state.grants({ origins: ["https://another.org/*"] }); await state.api.sync();
  state.grants({ origins: [] }); await state.api.sync();
  assert.deepEqual(state.changes, ["register", "update", "remove"]); assert.equal(state.scripts().length, 0);
});
test("registration failures stay visible and a later grant retries", async () => {
  const state = setup(); state.grants({ origins: ["https://*/*"] }); state.fail(true);
  await state.api.sync(); assert.match(state.api.error(), /could not be registered/);
  state.fail(false); await state.api.sync(); assert.equal(state.api.error(), "");
});
test("frame selection includes granted hosts and same-origin documents with exact identity", async () => {
  const state = setup();
  state.grants({ origins: ["https://*.kodikplayer.com/*"], permissions: ["webNavigation"] });
  state.frames([
    { frameId: 1, documentId: "embedded", url: "https://player.kodikplayer.com/secret" },
    { frameId: 2, url: "https://kodikplayer.com.evil.org/secret" },
    { frameId: 3, url: "https://page.example.org/player" },
    { frameId: 4, documentId: "blank", parentFrameId: 1, url: "about:blank" },
    { frameId: 5, parentFrameId: 2, url: "about:blank" },
    { frameId: 6, parentFrameId: 1, url: "blob:https://evil.org/token" },
    { frameId: 7, url: "https://player.kodikplayer.com/old", documentLifecycle: "cached" }
  ]);
  const result = await state.api.frames({ id: 42, url: "https://page.example.org/" });
  assert.deepEqual(result.targets.map(target => target.documentIds?.[0] ?? target.frameIds[0]), ["embedded", 3, "blank"]);
  assert.ok(result.targets.every(target => target.tabId === 42)); assert.equal(result.truncated, false);
});
test("the bounded frame census reports truncation rather than claiming unique media", async () => {
  const state = setup(); state.grants({ origins: ["https://*/*"], permissions: ["webNavigation"] });
  state.frames(Array.from({ length: 17 }, (_, i) => ({ frameId: i + 1, url: "https://embed.example.org/" })));
  const result = await state.api.frames({ id: 7 });
  assert.equal(result.targets.length, 16); assert.equal(result.truncated, true);
  state.grants({ origins: ["https://*/*"], permissions: [] });
  assert.equal((await state.api.frames({ id: 7 })).targets.length, 0);
});
