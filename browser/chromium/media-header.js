// Runs in the isolated world of the explicitly followed top-level tab.
async function readPlayingMediaHeader(playing = () => [...document.querySelectorAll("audio, video")]
  .filter(media => !media.paused && !media.ended && !media.error && !media.muted && media.volume > 0)) {
  const tracks = playing();
  if (tracks.length > 1) return { conflict: true };
  if (tracks.length === 0) return { paused: true };
  const [media] = tracks;
  let url;
  try { url = new URL(media.currentSrc); } catch { return {}; }
  if (!["http:", "https:"].includes(url.protocol)
      || url.username || url.password || media.srcObject || media.mediaKeys
      || (url.origin !== location.origin && !/\.(wav|flac|mp3|m4a|mp4|webm)$/i.test(url.pathname))) return {};
  const maximum = /\.(wav|flac|mp3)$/i.test(url.pathname) ? 4096 : 65536;

  const cache = globalThis.__sourceSwitcherMediaHeader;
  const rate = cache?.media === media && cache.url === url.href
    ? await cache.rate
    : await (globalThis.__sourceSwitcherMediaHeader = { media, url: url.href, rate: readHeader() }).rate;
  const current = playing();
  return current.length === 1 && current[0] === media && media.currentSrc === url.href && !media.mediaKeys && !media.srcObject
    ? { rate } : { paused: current.length === 0, conflict: current.length > 1 };

  async function readHeader() {
    let reader;
    try {
      const response = await fetch(url, {
        headers: { Range: `bytes=0-${maximum - 1}` },
        credentials: "omit", referrerPolicy: "no-referrer", redirect: "error", signal: AbortSignal.timeout(900)
      });
      if (!response.body) return null;
      reader = response.body.getReader();
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
        const rate = parse(bytes.subarray(0, length));
        if (rate) return rate;
      }
      return null;
    } catch { return null; }
    finally { if (reader) void reader.cancel().catch(() => {}); }
  }

  function parse(bytes) {
    const at = (offset, text) => [...text].every((character, index) => bytes[offset + index] === character.charCodeAt(0));
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const valid = rate => rate >= 8000 && rate <= 768000 ? rate : null;
    if (typeof readStreamHeader === "function") {
      const rate = readStreamHeader(bytes);
      if (rate) return rate;
    }
    const mp3 = readMp3();
    if (mp3) return mp3;
    // Native FLAC STREAMINFO is the first metadata block, with a 20-bit rate.
    if (bytes.length >= 42 && at(0, "fLaC") && (bytes[4] & 127) === 0
        && view.getUint32(4) % 0x1000000 === 34)
      return valid(view.getUint32(18) >>> 12);
    if (bytes.length < 12 || !at(0, "RIFF") || !at(8, "WAVE")) return null;
    for (let offset = 12; offset + 8 <= bytes.length;) {
      const size = view.getUint32(offset + 4, true);
      if (at(offset, "fmt ")) {
        if (size < 16 || offset + 24 > bytes.length) return null;
        const format = view.getUint16(offset + 8, true);
        return format === 1 || format === 3 ? valid(view.getUint32(offset + 12, true)) : null;
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
