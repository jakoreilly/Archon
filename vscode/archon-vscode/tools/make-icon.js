// Generates icon.png, the marketplace icon: an arch on a dark tile.
//
// Run with `node tools/make-icon.js`. It has no dependencies and writes a PNG
// directly, so the icon can be regenerated without an image editor installed.

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const SIZE = 256;
const SAMPLES = 4; // per axis, so 16 coverage samples per pixel

// Tile: a full-bleed rounded square, dark enough that the mark carries the
// contrast on both the light and the dark marketplace theme.
const TILE_RADIUS = 52;
const TILE_TOP = [0x26, 0x2e, 0x52];
const TILE_BOTTOM = [0x0d, 0x10, 0x20];

// Mark: a semicircular arch on two piers, standing on a plinth.
const CX = 128;
const CY = 102;
const OUTER = 62;
const INNER = 36;
const PIER_BOTTOM = 198;
const PLINTH = { left: 52, top: 194, right: 204, bottom: 216, radius: 7 };
const MARK_TOP = CY - OUTER;
const MARK_BOTTOM = PLINTH.bottom;
const MARK_TOP_COLOUR = [0x67, 0xe8, 0xf9];
const MARK_BOTTOM_COLOUR = [0xa7, 0x8b, 0xfa];

const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);

function insideRoundedRect(x, y, left, top, right, bottom, radius) {
    if (x < left || x > right || y < top || y > bottom) return false;
    const nearestX = clamp(x, left + radius, right - radius);
    const nearestY = clamp(y, top + radius, bottom - radius);
    return Math.hypot(x - nearestX, y - nearestY) <= radius;
}

function insideTile(x, y) {
    return insideRoundedRect(x, y, 0, 0, SIZE, SIZE, TILE_RADIUS);
}

function insideMark(x, y) {
    if (insideRoundedRect(x, y, PLINTH.left, PLINTH.top, PLINTH.right, PLINTH.bottom, PLINTH.radius)) {
        return true;
    }
    if (y <= CY) {
        const distance = Math.hypot(x - CX, y - CY);
        return distance >= INNER && distance <= OUTER;
    }
    const offset = Math.abs(x - CX);
    return y <= PIER_BOTTOM && offset >= INNER && offset <= OUTER;
}

function mix(from, to, t) {
    const e = clamp(t, 0, 1);
    return [
        Math.round(from[0] + (to[0] - from[0]) * e),
        Math.round(from[1] + (to[1] - from[1]) * e),
        Math.round(from[2] + (to[2] - from[2]) * e)
    ];
}

function colourAt(x, y) {
    if (insideMark(x, y)) {
        return mix(MARK_TOP_COLOUR, MARK_BOTTOM_COLOUR, (y - MARK_TOP) / (MARK_BOTTOM - MARK_TOP));
    }
    return mix(TILE_TOP, TILE_BOTTOM, (x + y) / (SIZE * 2));
}

function render() {
    const rows = [];
    const step = 1 / SAMPLES;
    const total = SAMPLES * SAMPLES;

    for (let py = 0; py < SIZE; py++) {
        const row = Buffer.alloc(1 + SIZE * 4); // leading byte selects filter 0
        for (let px = 0; px < SIZE; px++) {
            let r = 0;
            let g = 0;
            let b = 0;
            let covered = 0;

            for (let sy = 0; sy < SAMPLES; sy++) {
                for (let sx = 0; sx < SAMPLES; sx++) {
                    const x = px + (sx + 0.5) * step;
                    const y = py + (sy + 0.5) * step;
                    if (!insideTile(x, y)) continue;
                    const [cr, cg, cb] = colourAt(x, y);
                    r += cr;
                    g += cg;
                    b += cb;
                    covered++;
                }
            }

            const at = 1 + px * 4;
            if (covered > 0) {
                row[at] = Math.round(r / covered);
                row[at + 1] = Math.round(g / covered);
                row[at + 2] = Math.round(b / covered);
                row[at + 3] = Math.round((covered / total) * 255);
            }
        }
        rows.push(row);
    }

    return Buffer.concat(rows);
}

const CRC_TABLE = (() => {
    const table = new Int32Array(256);
    for (let n = 0; n < 256; n++) {
        let c = n;
        for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
        table[n] = c;
    }
    return table;
})();

function crc32(buffer) {
    let c = -1;
    for (let i = 0; i < buffer.length; i++) c = CRC_TABLE[(c ^ buffer[i]) & 0xff] ^ (c >>> 8);
    return (c ^ -1) >>> 0;
}

function chunk(type, data) {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length, 0);
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const checksum = Buffer.alloc(4);
    checksum.writeUInt32BE(crc32(body), 0);
    return Buffer.concat([length, body, checksum]);
}

function png(raw) {
    const header = Buffer.alloc(13);
    header.writeUInt32BE(SIZE, 0);
    header.writeUInt32BE(SIZE, 4);
    header[8] = 8; // bit depth
    header[9] = 6; // truecolour with alpha
    return Buffer.concat([
        Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
        chunk('IHDR', header),
        chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
        chunk('IEND', Buffer.alloc(0))
    ]);
}

const target = path.join(__dirname, '..', 'icon.png');
fs.writeFileSync(target, png(render()));
console.log(`Wrote ${target} (${SIZE}x${SIZE})`);
