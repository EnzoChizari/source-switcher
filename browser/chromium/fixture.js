"use strict";
function makeWav(sampleRate, seconds = 5, bits = 16) {
  if (!Number.isInteger(sampleRate) || sampleRate < 8000 || sampleRate > 768000
    || !Number.isInteger(seconds) || seconds < 1 || seconds > 30 || ![16, 24, 32].includes(bits))
    throw new RangeError("Invalid fixture dimensions.");
  const frames = sampleRate * seconds, channels = 2;
  const sampleBytes = bits / 8, blockAlign = channels * sampleBytes, amplitude = (2 ** (bits - 1) - 1) * .1;
  const buffer = new ArrayBuffer(44 + frames * blockAlign), view = new DataView(buffer);
  const text = (offset, value) => [...value].forEach((character, index) => view.setUint8(offset + index, character.charCodeAt(0)));
  text(0, "RIFF"); view.setUint32(4, buffer.byteLength - 8, true); text(8, "WAVE");
  text(12, "fmt "); view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, channels, true);
  view.setUint32(24, sampleRate, true); view.setUint32(28, sampleRate * blockAlign, true);
  view.setUint16(32, blockAlign, true); view.setUint16(34, bits, true); text(36, "data"); view.setUint32(40, buffer.byteLength - 44, true);
  for (let frame = 0; frame < frames; frame++) {
    const fade = Math.min(1, frame / (sampleRate * .05), (frames - frame) / (sampleRate * .05));
    const sample = Math.round(Math.sin(2 * Math.PI * 440 * frame / sampleRate) * amplitude * fade);
    for (let channel = 0; channel < channels; channel++) {
      const offset = 44 + frame * blockAlign + channel * sampleBytes;
      for (let byte = 0; byte < sampleBytes; byte++) view.setUint8(offset + byte, sample >> (8 * byte));
    }
  }
  return new Blob([buffer], { type: "audio/wav" });
}
const players = [[44100, 16], [48000, 16], [96000, 24], [192000, 32]].map(([rate, bits]) => {
  const article = document.createElement("article"), heading = document.createElement("strong"), audio = document.createElement("audio");
  heading.textContent = `${rate.toLocaleString()} Hz · ${bits}-bit PCM WAV`;
  audio.controls = true; audio.preload = "metadata"; audio.volume = .05;
  const wav = makeWav(rate, 5, bits);
  audio.src = URL.createObjectURL(wav);
  audio.addEventListener("play", () => {
    if (document.getElementById("single").checked) players.forEach(player => { if (player.audio !== audio) player.audio.pause(); });
  });
  article.append(heading, audio); document.getElementById("players").append(article);
  const header = wav.slice(0, 36).arrayBuffer().then(bytes => {
    const view = new DataView(bytes);
    return { rate: view.getUint32(24, true), bits: view.getUint16(34, true) };
  });
  return { rate, bits, audio, header };
});
const connection = chrome.runtime.connect({ name: "pcm-demo" });
let generation = 0, connected = true;
connection.onDisconnect.addListener(() => { connected = false; });
async function reportPlayback() {
  const revision = ++generation;
  const active = players.filter(({ audio }) => !audio.paused && !audio.ended && !audio.muted && audio.volume > 0);
  const observation = active.length === 1 ? await active[0].header
    : active.length > 1 ? { conflict: true } : { paused: true };
  if (connected && revision === generation) connection.postMessage(observation);
}
for (const { audio } of players) {
  for (const event of ["playing", "pause", "ended", "volumechange"]) audio.addEventListener(event, reportPlayback);
}
void reportPlayback();
document.getElementById("read").addEventListener("click", () => {
  const rows = players.map(({ rate, bits, audio }) => ({ generatedFileRateHz: rate, bitsPerSample: bits, paused: audio.paused }));
  document.getElementById("result").textContent = JSON.stringify(rows, null, 2);
});
