"use strict";
const statusElement = document.getElementById("status"), follow = document.getElementById("follow"), stop = document.getElementById("stop");
const autoFollow = document.getElementById("auto-follow");
let commandPending = false, pollPending = false, revision = 0;
function render(state) {
  statusElement.textContent = state?.status ?? "Extension state unavailable.";
  stop.disabled = !state?.following;
  autoFollow.checked = state?.autoFollow === true;
}
async function action(kind, fields = {}) {
  const current = ++revision;
  commandPending = true;
  follow.disabled = stop.disabled = autoFollow.disabled = true;
  try {
    const state = await chrome.runtime.sendMessage({ kind, ...fields });
    if (current === revision) render(state);
  } catch { statusElement.textContent = "Unable to read connection state. Reopen this popup to retry."; stop.disabled = false; }
  finally { if (current === revision) { commandPending = false; follow.disabled = autoFollow.disabled = false; } }
}
async function refreshStatus() {
  if (commandPending || pollPending) return;
  pollPending = true;
  const current = revision;
  try {
    const state = await chrome.runtime.sendMessage({ kind: "getStatus" });
    if (!commandPending && current === revision) render(state);
  } catch { /* Retain the last explicit error while the background is unavailable. */ }
  finally { pollPending = false; }
}
follow.addEventListener("click", () => action("followActiveTab"));
stop.addEventListener("click", () => action("stopFollowing"));
autoFollow.addEventListener("change", () => action("setAutoFollow", { enabled: autoFollow.checked }));
document.getElementById("fixtures").addEventListener("click", () => chrome.tabs.create({ url: chrome.runtime.getURL("fixture.html") }));
action("getStatus");
const statusTimer = setInterval(refreshStatus, 500);
window.addEventListener("unload", () => clearInterval(statusTimer), { once: true });
