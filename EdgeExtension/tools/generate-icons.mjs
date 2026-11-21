import { mkdirSync, writeFileSync } from "node:fs";
import { deflateSync } from "node:zlib";

function crc32(bytes) {
    let crc = 0xffffffff;
    for (const byte of bytes) {
        crc ^= byte;
        for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0);
    }
    return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
    const contents = Buffer.concat([Buffer.from(type), data]);
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(contents));
    return Buffer.concat([length, contents, crc]);
}

// Original RSS glyph, sampled at 4x resolution for small toolbar sizes.
function icon(size) {
    const rows = Buffer.alloc(size * (1 + size * 4));
    for (let y = 0; y < size; y++) {
        for (let x = 0; x < size; x++) {
            let coverage = 0;
            for (let sy = 0; sy < 4; sy++) {
                for (let sx = 0; sx < 4; sx++) {
                    const px = (x + (sx + 0.5) / 4) / size;
                    const py = 1 - (y + (sy + 0.5) / 4) / size;
                    const radius = Math.hypot(px - 0.22, py - 0.22);
                    if (radius < 0.09 || px >= 0.18 && py >= 0.18
                        && (radius > 0.29 && radius < 0.40 || radius > 0.54 && radius < 0.65)) coverage++;
                }
            }
            const offset = y * (1 + size * 4) + 1 + x * 4;
            rows.set([220, 95, 10, Math.round(coverage / 16 * 255)], offset);
        }
    }
    const header = Buffer.alloc(13);
    header.writeUInt32BE(size, 0);
    header.writeUInt32BE(size, 4);
    header[8] = 8;
    header[9] = 6;
    return Buffer.concat([
        Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
        chunk("IHDR", header), chunk("IDAT", deflateSync(rows)), chunk("IEND", Buffer.alloc(0))
    ]);
}

mkdirSync(new URL("../icons/", import.meta.url), { recursive: true });
for (const size of [16, 32, 48, 128]) {
    writeFileSync(new URL(`../icons/feed-${size}.png`, import.meta.url), icon(size));
}
