"use strict";
const statusElement = document.getElementById("status");
const follow = document.getElementById("follow");
const stop = document.getElementById("stop");
async function action(kind) {
  follow.disabled = stop.disabled = true;
  try {
    const state = await browser.runtime.sendMessage({ kind });
    statusElement.textContent = state?.status ?? "Extension state is unavailable.";
    stop.disabled = !state?.following;
  } catch {
    statusElement.textContent = "Unable to read the extension state. Close and reopen this popup to retry.";
    stop.disabled = false;
  } finally { follow.disabled = false; }
}
follow.addEventListener("click", () => action("followActiveTab"));
stop.addEventListener("click", () => action("stopFollowing"));
document.getElementById("fixtures").addEventListener("click", () => browser.tabs.create({ url: browser.runtime.getURL("fixture.html") }));
action("getStatus");
