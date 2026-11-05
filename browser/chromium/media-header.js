// Runs in the isolated world of the explicitly followed top-level tab.
async function readPlayingMediaHeader(playing = () => [...document.querySelectorAll("audio, video")]
  .filter(media => !media.paused && !media.ended && !media.error && !media.muted && media.volume > 0)) {
  const tracks = playing();
  if (tracks.length > 1) return { conflict: true };
  if (tracks.length === 0) return { paused: true };
  const [media] = tracks;
  let url;
  try { url = new URL(media.currentSrc); } catch { return { playing: true }; }
  if (!["http:", "https:"].includes(url.protocol)
      || url.username || url.password || media.srcObject || media.mediaKeys
      || (url.origin !== location.origin && !/\.(wav|flac|mp3|m4a|mp4|webm)$/i.test(url.pathname))) return { playing: true };
  const maximum = /\.(wav|flac|mp3)$/i.test(url.pathname) ? 4096 : 65536;

  let cache = globalThis.__sourceSwitcherMediaHeader;
  if (cache?.media !== media || cache.url !== url.href) {
    cache = { media, url: url.href, retryAt: Infinity, format: readHeader() };
    globalThis.__sourceSwitcherMediaHeader = cache;
    // One bounded retry for a transient failure. Unsupported headers stay cached.
    const entry = cache;
    entry.format = entry.format.then(format => {
      if (format === undefined) entry.retryAt = Date.now() + 1000;
      return format;
    });
  } else if (Date.now() >= cache.retryAt) {
    cache.retryAt = Infinity;
    cache.format = readHeader();
  }
  const format = await cache.format;
  const current = playing();
  return current.length === 1 && current[0] === media && media.currentSrc === url.href && !media.mediaKeys && !media.srcObject
    ? format ?? { playing: true } : { paused: current.length === 0, conflict: current.length > 1 };

  async function readHeader() {
    let reader;
    try {
      const response = await fetch(url, {
        headers: { Range: `bytes=0-${maximum - 1}` },
        credentials: "omit", referrerPolicy: "no-referrer", redirect: "error", signal: AbortSignal.timeout(900)
      });
      if (response.body) reader = response.body.getReader();
      if (response.status >= 500) return undefined;
      if (!reader) return null;
      if (!(response.status === 200 || response.status === 206
          && /^bytes 0-\d+\/(?:\d+|\*)$/i.test(response.headers.get("Content-Range") ?? ""))) return null;
      const bytes = new Uint8Array(maximum);
      let length = 0;
      while (length < maximum) {
        const { done, value } = await reader.read();
        if (done) break;
        const chunk = value.subarray(0, maximum - length);
        bytes.set(chunk, length);
        length += chunk.length;
        const format = parse(bytes.subarray(0, length));
        if (format) return format;
      }
      return null;
    } catch { return undefined; }
    finally { if (reader) void reader.cancel().catch(() => {}); }
  }

  function parse(bytes) {
    const at = (offset, text) => [...text].every((character, index) => bytes[offset + index] === character.charCodeAt(0));
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const valid = rate => rate >= 8000 && rate <= 768000 ? rate : null;
    const observed = (rate, bits) => valid(rate)
      ? { rate, ...([16, 24, 32].includes(bits) ? { bits } : {}) } : null;
    if (typeof readStreamHeader === "function") {
      const rate = readStreamHeader(bytes);
      if (rate) return observed(rate);
    }
    const mp3 = readMp3();
    if (mp3) return observed(mp3);
    // Native FLAC STREAMINFO is the first metadata block, with a 20-bit rate.
    if (bytes.length >= 42 && at(0, "fLaC") && (bytes[4] & 127) === 0
        && view.getUint32(4) % 0x1000000 === 34) {
      const fields = view.getUint32(18);
      return observed(fields >>> 12, ((fields >>> 4) & 31) + 1);
    }
    if (bytes.length < 12 || !at(0, "RIFF") || !at(8, "WAVE")) return null;
    for (let offset = 12; offset + 8 <= bytes.length;) {
      const size = view.getUint32(offset + 4, true);
      if (at(offset, "fmt ")) {
        if (size < 16 || offset + 24 > bytes.length) return null;
        let encoding = view.getUint16(offset + 8, true);
        const rate = view.getUint32(offset + 12, true);
        const container = view.getUint16(offset + 22, true);
        let bits = container;
        if (encoding === 0xfffe) {
          if (size < 40 || offset + 48 > bytes.length || view.getUint16(offset + 24, true) < 22
              || ![0, 0, 16, 0, 128, 0, 0, 170, 0, 56, 155, 113].every((byte, i) => bytes[offset + 36 + i] === byte))
            return null;
          encoding = view.getUint32(offset + 32, true);
          bits = view.getUint16(offset + 26, true);
        }
        if (encoding !== 1 && encoding !== 3) return null;
        const channels = view.getUint16(offset + 10, true);
        const align = view.getUint16(offset + 20, true);
        return observed(rate, encoding === 1 && bits <= container && channels > 0
          && align === channels * Math.ceil(container / 8) ? bits : null);
      }
      offset += 8 + size + size % 2;
    }
    return null;

    function readMp3() {
      let offset = 0;
      if (bytes.length >= 10 && at(0, "ID3")) {
        if (bytes.subarray(6, 10).some(byte => byte > 127)) return null;
        offset = 10 + (bytes[5] & 16 ? 10 : 0);
        offset += ((bytes[6] * 128 + bytes[7]) * 128 + bytes[8]) * 128 + bytes[9];
      }
      const frame = position => {
        if (position + 4 > bytes.length) return null;
        const [sync, versionByte, rateByte] = bytes.subarray(position, position + 3);
        const version = (versionByte >> 3) & 3, index = (rateByte >> 2) & 3, bit = rateByte >> 4;
        if (sync !== 255 || (versionByte & 230) !== 226 || version === 1 || index === 3 || bit === 0 || bit === 15) return null;
        const v1 = version === 3;
        const rate = [44100, 48000, 32000][index] / (v1 ? 1 : version === 2 ? 2 : 4);
        const bitrates = v1 ? [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320]
          : [8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
        return { rate, length: Math.floor((v1 ? 144000 : 72000) * bitrates[bit - 1] / rate) + ((rateByte >> 1) & 1) };
      };
      const first = frame(offset);
      return first && frame(offset + first.length)?.rate === first.rate ? valid(first.rate) : null;
    }
  }
}
