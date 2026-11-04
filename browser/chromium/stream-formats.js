// Reads an audio rate declared by bounded initialization data. No audio is decoded.
function readStreamHeader(bytes) {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const valid = rate => Number.isInteger(rate) && rate >= 8000 && rate <= 768000 ? rate : null;
  const text = (start, length) => String.fromCharCode(...bytes.subarray(start, start + length));
  const unique = rates => rates.length === 1 ? valid(rates[0]) : null;
  const length = bytes.length;
  if (length < 8) return null;

  function* boxes(start, end) {
    for (let offset = start; offset + 8 <= end;) {
      let size = view.getUint32(offset), header = 8;
      if (size === 1) {
        if (offset + 16 > end || view.getUint32(offset + 8) !== 0) break;
        size = view.getUint32(offset + 12);
        header = 16;
      } else if (size === 0) size = end - offset;
      if (size < header || offset + size > end) break;
      yield { type: text(offset + 4, 4), start: offset + header, end: offset + size };
      offset += size;
    }
  }
  function child(box, type) {
    return [...boxes(box.start, box.end)].find(item => item.type === type);
  }
  if (["ftyp", "moov", "free"].includes(text(4, 4))) {
    const movie = child({ start: 0, end: length }, "moov");
    if (!movie) return null;
    const rates = [];
    for (const track of boxes(movie.start, movie.end)) {
      if (track.type !== "trak") continue;
      const media = child(track, "mdia"), handler = media && child(media, "hdlr");
      if (!handler || handler.start + 12 > handler.end || text(handler.start + 8, 4) !== "soun") continue;
      const information = child(media, "minf"), samples = information && child(information, "stbl");
      const descriptions = samples && child(samples, "stsd");
      if (!descriptions || descriptions.start + 8 > descriptions.end
          || view.getUint32(descriptions.start + 4) !== 1) return null;
      const [entry] = boxes(descriptions.start + 8, descriptions.end);
      if (!entry || !["mp4a", "Opus", "fLaC", "alac"].includes(entry.type)
          || entry.start + 28 > entry.end || view.getUint16(entry.start + 8) !== 0) return null;
      rates.push(view.getUint16(entry.start + 24));
    }
    return unique(rates);
  }

  // EBML TrackEntry records are scoped to the WebM Tracks element.
  function variable(offset, end, id) {
    const first = bytes[offset];
    if (!first || offset >= end) return null;
    let marker = 128, count = 1;
    while ((first & marker) === 0) { marker >>= 1; count++; }
    if (offset + count > end || id && count > 4) return null;
    let number = id ? first : first & (marker - 1), unknown = !id && number === marker - 1;
    for (let index = offset + 1; index < offset + count; index++) {
      number = number * 256 + bytes[index];
      unknown &&= bytes[index] === 255;
    }
    return { number, unknown, next: offset + count };
  }
  function* elements(start, end) {
    for (let offset = start; offset < end;) {
      const id = variable(offset, end, true), size = id && variable(id.next, end, false);
      if (!size) break;
      const stop = size.unknown ? end : size.next + size.number;
      if (stop > end && id.number !== 0x18538067) break;
      yield { id: id.number, start: size.next, end: Math.min(stop, end) };
      offset = stop;
    }
  }
  function element(parent, id) {
    for (const item of elements(parent.start, parent.end)) if (item.id === id) return item;
  }
  if (view.getUint32(0) === 0x1a45dfa3) {
    const segment = element({ start: 0, end: length }, 0x18538067);
    const tracks = segment && element(segment, 0x1654ae6b);
    if (!tracks) return null;
    const rates = [];
    for (const entry of elements(tracks.start, tracks.end)) {
      if (entry.id !== 0xae) continue;
      const type = element(entry, 0x83);
      if (!type || type.end - type.start !== 1 || bytes[type.start] !== 2) continue;
      const codec = element(entry, 0x86);
      if (!codec || !["A_OPUS", "A_VORBIS"].includes(text(codec.start, Math.min(codec.end - codec.start, 16)))
          || element(entry, 0x6d80)) return null;
      const audio = element(entry, 0xe1);
      const sample = audio && (element(audio, 0x78b5) ?? element(audio, 0xb5));
      if (!sample) return null;
      const size = sample.end - sample.start;
      rates.push(size === 4 ? view.getFloat32(sample.start) : size === 8 ? view.getFloat64(sample.start) : null);
    }
    return unique(rates);
  }
  return null;
}
