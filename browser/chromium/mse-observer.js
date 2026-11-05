// Installed in the selected document's main world so it can observe page-owned buffers.
(() => {
  "use strict";
  const key = Symbol.for("source-switcher.mse-observer.v1");
  if (globalThis[key]) return globalThis[key].read();
  if (typeof MediaSource === "undefined" || typeof SourceBuffer === "undefined") return {};
  const sources = new Map(), buffers = new WeakMap();
  const recentlyPlayed = [], seen = new WeakSet();
  const add = MediaSource.prototype.addSourceBuffer, append = SourceBuffer.prototype.appendBuffer;
  const change = SourceBuffer.prototype.changeType, create = URL.createObjectURL;
  const play = HTMLMediaElement.prototype.play;
  const parse = readStreamHeader;
  const readFile = readPlayingMediaHeader;
  let deadline = performance.now() + 2200, retained = false;
  const enabled = () => retained || performance.now() < deadline;

  const metadata = mime => {
    const codecs = /codecs\s*=\s*["']?([^"';]+)/i.exec(mime)?.[1];
    const videoOnly = /^video\//i.test(mime) && codecs
      && codecs.split(",").every(codec => /^(avc[13]|hev1|hvc1|vp0?[89]|av01|theora)(\.|$)/i.test(codec.trim()));
    return { videoOnly: !!videoOnly, rate: null, pending: null };
  };
  function addBuffer(mime) {
    const buffer = Reflect.apply(add, this, arguments);
    if (enabled()) buffers.set(buffer, metadata(typeof mime === "string" ? mime : ""));
    return buffer;
  }
  function appendBuffer(data) {
    const result = Reflect.apply(append, this, arguments);
    const buffer = buffers.get(this);
    if (!buffer || buffer.videoOnly || !enabled()) return result;
    try {
      const bytes = ArrayBuffer.isView(data)
        ? new Uint8Array(data.buffer, data.byteOffset, Math.min(data.byteLength, 65536))
        : new Uint8Array(data, 0, Math.min(data.byteLength, 65536));
      // Only initialization data can change a rate, including split appends.
      const startsInit = bytes.length >= 8 && (bytes[0] === 0x1a && bytes[1] === 0x45 && bytes[2] === 0xdf && bytes[3] === 0xa3
        || ["ftyp", "moov", "free"].includes(String.fromCharCode(...bytes.subarray(4, 8))));
      if (startsInit) { buffer.rate = null; buffer.pending = bytes.slice(); }
      else if (buffer.pending) {
        const joined = new Uint8Array(Math.min(65536, buffer.pending.length + bytes.length));
        joined.set(buffer.pending);
        joined.set(bytes.subarray(0, joined.length - buffer.pending.length), buffer.pending.length);
        buffer.pending = joined;
      }
      if (buffer.pending) {
        buffer.rate = parse(buffer.pending);
        if (buffer.rate || buffer.pending.length === 65536) buffer.pending = null;
      }
    } catch { /* Instrumentation must never change playback exceptions or return values. */ }
    return result;
  }
  function changeType(mime) {
    const result = Reflect.apply(change, this, arguments);
    if (buffers.has(this)) buffers.set(this, metadata(typeof mime === "string" ? mime : ""));
    return result;
  }
  function createObjectURL(object) {
    const url = Reflect.apply(create, this, arguments);
    if (enabled() && object instanceof MediaSource) {
      sources.set(url, new WeakRef(object));
      if (sources.size > 64) sources.delete(sources.keys().next().value);
    }
    return url;
  }
  function playMedia() {
    const result = Reflect.apply(play, this, arguments);
    if (enabled() && !seen.has(this)) {
      if (recentlyPlayed.length === 32) {
        const oldest = recentlyPlayed.shift().deref();
        if (oldest) seen.delete(oldest);
      }
      seen.add(this);
      recentlyPlayed.push(new WeakRef(this));
    }
    return result;
  }
  function playingMedia() {
    return [...new Set([...document.querySelectorAll("video, audio"), ...recentlyPlayed.map(ref => ref.deref())])]
      .filter(media => media && !media.paused && !media.ended && !media.error && !media.muted && media.volume > 0);
  }
  function read() {
    deadline = performance.now() + 2200;
    const playing = playingMedia();
    if (playing.length !== 1) return { conflict: playing.length > 1, paused: playing.length === 0 };
    const [media] = playing;
    const source = sources.get(media.currentSrc)?.deref();
    if (media.mediaKeys || media.srcObject) return { playing: true };
    if (!source) return media.isConnected ? { playing: true } : readFile(playingMedia);
    const audio = [...source.activeSourceBuffers].map(buffer => buffers.get(buffer)).filter(buffer => !buffer?.videoOnly);
    const rate = audio.length === 1 ? audio[0]?.rate : null;
    return rate ? { rate, streaming: true, playing: true } : { playing: true };
  }

  MediaSource.prototype.addSourceBuffer = addBuffer;
  SourceBuffer.prototype.appendBuffer = appendBuffer;
  if (typeof change === "function") SourceBuffer.prototype.changeType = changeType;
  URL.createObjectURL = createObjectURL;
  HTMLMediaElement.prototype.play = playMedia;
  function dispose() {
    clearInterval(timer);
    retained = false; deadline = 0;
    sources.clear(); recentlyPlayed.length = 0;
    if (MediaSource.prototype.addSourceBuffer === addBuffer) MediaSource.prototype.addSourceBuffer = add;
    if (SourceBuffer.prototype.appendBuffer === appendBuffer) SourceBuffer.prototype.appendBuffer = append;
    if (SourceBuffer.prototype.changeType === changeType) SourceBuffer.prototype.changeType = change;
    if (URL.createObjectURL === createObjectURL) URL.createObjectURL = create;
    if (HTMLMediaElement.prototype.play === playMedia) HTMLMediaElement.prototype.play = play;
    if (globalThis[key] === observer) delete globalThis[key];
  }
  const observer = { read, dispose, retain() { retained = true; clearInterval(timer); } };
  globalThis[key] = observer;
  const timer = setInterval(() => {
    if (performance.now() < deadline) return;
    dispose();
  }, 1000);
  return read();
})();
