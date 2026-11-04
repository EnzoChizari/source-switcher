import assert from "node:assert/strict";
import { webcrypto } from "node:crypto";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import { test } from "node:test";
const protocol = await readFile(new URL("protocol.js", import.meta.url), "utf8");
const source = await readFile(new URL("background.js", import.meta.url), "utf8");
function setup(error, throws = false) {
  let listener, disconnected, attempts = 0;
  const chrome = { runtime: { lastError: error,
    onMessage: { addListener(value) { listener = value; } }, onConnect: { addListener() {} },
    connectNative() {
      attempts++;
      if (throws) throw error;
      return { onMessage: { addListener() {} }, onDisconnect: { addListener(value) { disconnected = value; } }, disconnect() {} };
    } }, tabs: { query: async () => [{ id: 7 }], onUpdated: { addListener() {} },
      onRemoved: { addListener() {} }, onActivated: { addListener() {} } },
    windows: { onFocusChanged: { addListener() {} } } };
  const context = vm.createContext({ chrome, crypto: webcrypto, setTimeout: () => 1, clearTimeout() {} });
  context.importScripts = () => vm.runInContext(protocol, context);
  vm.runInContext(source, context);
  return { invoke: kind => new Promise(resolve => listener({ kind }, {}, resolve)),
    disconnect: () => disconnected(), attempts: () => attempts };
}
for (const message of ["Specified native messaging host not found.", "Native host has exited.", "Access to the specified native messaging host is forbidden."]) {
  test("native failure is visible: " + message, async () => {
    const state = setup({ message }); await state.invoke("followActiveTab"); state.disconnect();
    const status = await state.invoke("getStatus");
    assert.equal(status.following, false); assert.equal(status.connected, false);
    assert.ok(status.status.includes(message)); assert.equal(state.attempts(), 1);
  });
}
test("native error controls are collapsed and detail is bounded", async () => {
  const state = setup({ message: "Bad\n\r\0\t host " + "x".repeat(1000) });
  await state.invoke("followActiveTab"); state.disconnect();
  const status = (await state.invoke("getStatus")).status;
  assert.ok(status.includes("Bad host")); assert.ok(status.length <= "Native bridge unavailable: ".length + 240);
  assert.equal(/[\x00-\x1f\x7f-\x9f]/.test(status), false);
});
test("synchronous connection failure clears selection and exposes the cause", async () => {
  const state = setup(new Error("Native API rejected this connection"), true);
  const status = await state.invoke("followActiveTab");
  assert.ok(status.status.includes("Native API rejected this connection"));
  assert.equal(status.following, false); assert.equal(status.connected, false); assert.equal(state.attempts(), 1);
});
test("normal disconnect remains distinct and status reads do not reconnect", async () => {
  const state = setup(undefined); await state.invoke("followActiveTab"); state.disconnect();
  assert.equal((await state.invoke("getStatus")).status, "Native bridge disconnected. Follow this tab to reconnect.");
  await state.invoke("getStatus"); assert.equal(state.attempts(), 1);
});
