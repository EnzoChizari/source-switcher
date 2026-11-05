"use strict";
const statusElement = document.getElementById("status"), follow = document.getElementById("follow"), stop = document.getElementById("stop");
const autoFollow = document.getElementById("auto-follow");
let commandPending = false, pollPending = false, revision = 0;
function render(state) {
  statusElement.textContent = state?.status ?? "Extension state unavailable.";
  stop.disabled = !state?.following;
  autoFollow.checked = state?.autoFollow === true;
  if (state?.siteError) document.getElementById("site-status").textContent = state.siteError;
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
const siteButton = document.getElementById("site-access"), allSites = document.getElementById("all-sites");
const siteStatus = document.getElementById("site-status");
const webOrigins = ["http://*/*", "https://*/*"];
let accessPending = false, siteOrigin;
if (siteButton && chrome.tabs?.query) {
  siteButton.disabled = true;
  void chrome.tabs.query({ active: true, lastFocusedWindow: true }).then(([tab]) => {
    const url = new URL(tab?.url);
    if (["http:", "https:"].includes(url.protocol)) siteOrigin = url.protocol + "//" + url.hostname + "/*";
    if (!accessPending) siteButton.disabled = !siteOrigin;
  }).catch(() => {});
}
async function accessChanged(all) {
  if (accessPending) return;
  accessPending = true;
  siteButton.disabled = allSites.disabled = true;
  try {
    let granted;
    if (all && !allSites.checked) {
      await chrome.permissions.remove({ origins: webOrigins });
      granted = false;
    } else {
      let origins = webOrigins;
      if (!all) {
        if (!siteOrigin) throw new Error("Open a website first.");
        origins = [siteOrigin];
      }
      granted = await chrome.permissions.request({ permissions: ["webNavigation"], origins });
    }
    const state = await chrome.runtime.sendMessage({ kind: "syncSiteAccess" });
    allSites.checked = await chrome.permissions.contains({ origins: webOrigins });
    siteStatus.textContent = state?.siteError || (granted
      ? "Early detection enabled. Reload the player once, then follow the tab."
      : "Access removed or declined. Reload open players to remove earlier page hooks.");
  } catch { siteStatus.textContent = "Could not change site access. Open a website and try again."; }
  finally { accessPending = false; siteButton.disabled = !siteOrigin; allSites.disabled = false; }
}
siteButton?.addEventListener("click", () => accessChanged(false));
allSites?.addEventListener("change", () => accessChanged(true));
if (allSites && chrome.permissions?.contains) {
  void chrome.permissions.contains({ origins: webOrigins }).then(value => { if (!accessPending) allSites.checked = value; }).catch(() => {});
}
document.getElementById("fixtures").addEventListener("click", () => chrome.tabs.create({ url: chrome.runtime.getURL("fixture.html") }));
action("getStatus");
const statusTimer = setInterval(refreshStatus, 500);
window.addEventListener("unload", () => clearInterval(statusTimer), { once: true });
