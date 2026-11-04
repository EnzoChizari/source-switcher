"use strict";
function makeWav(sampleRate, seconds = 5) {
  if (!Number.isInteger(sampleRate) || sampleRate < 8000 || sampleRate > 768000
    || !Number.isInteger(seconds) || seconds < 1 || seconds > 30) throw new RangeError("Invalid fixture dimensions.");
  const frames = sampleRate * seconds, channels = 2;
  const buffer = new ArrayBuffer(44 + frames * channels * 2), view = new DataView(buffer);
  const text = (offset, value) => [...value].forEach((character, index) => view.setUint8(offset + index, character.charCodeAt(0)));
  text(0, "RIFF"); view.setUint32(4, buffer.byteLength - 8, true); text(8, "WAVE");
  text(12, "fmt "); view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, channels, true);
  view.setUint32(24, sampleRate, true); view.setUint32(28, sampleRate * channels * 2, true);
  view.setUint16(32, channels * 2, true); view.setUint16(34, 16, true); text(36, "data"); view.setUint32(40, buffer.byteLength - 44, true);
  for (let frame = 0; frame < frames; frame++) {
    const fade = Math.min(1, frame / (sampleRate * .05), (frames - frame) / (sampleRate * .05));
    const sample = Math.round(Math.sin(2 * Math.PI * 440 * frame / sampleRate) * 32767 * .1 * fade);
    for (let channel = 0; channel < channels; channel++) view.setInt16(44 + (frame * channels + channel) * 2, sample, true);
  }
  return new Blob([buffer], { type: "audio/wav" });
}
const players = [44100, 48000, 96000, 192000].map(rate => {
  const article = document.createElement("article"), heading = document.createElement("strong"), audio = document.createElement("audio");
  heading.textContent = `${rate.toLocaleString()} Hz PCM WAV`;
  audio.controls = true; audio.preload = "metadata"; audio.volume = .05;
  const wav = makeWav(rate);
  audio.src = URL.createObjectURL(wav);
  audio.addEventListener("play", () => {
    if (document.getElementById("single").checked) players.forEach(player => { if (player.audio !== audio) player.audio.pause(); });
  });
  article.append(heading, audio); document.getElementById("players").append(article);
  const headerRate = wav.slice(24, 28).arrayBuffer().then(bytes => new DataView(bytes).getUint32(0, true));
  return { rate, audio, headerRate };
});
const connection = chrome.runtime.connect({ name: "pcm-demo" });
let generation = 0, connected = true;
connection.onDisconnect.addListener(() => { connected = false; });
async function reportPlayback() {
  const revision = ++generation;
  const active = players.filter(({ audio }) => !audio.paused && !audio.ended && !audio.muted && audio.volume > 0);
  const observation = active.length === 1 ? { rate: await active[0].headerRate }
    : active.length > 1 ? { conflict: true } : { paused: true };
  if (connected && revision === generation) connection.postMessage(observation);
}
for (const { audio } of players) {
  for (const event of ["playing", "pause", "ended", "volumechange"]) audio.addEventListener(event, reportPlayback);
}
void reportPlayback();
document.getElementById("read").addEventListener("click", () => {
  const rows = players.map(({ rate, audio }) => ({ generatedFileRateHz: rate, paused: audio.paused }));
  document.getElementById("result").textContent = JSON.stringify(rows, null, 2);
});
