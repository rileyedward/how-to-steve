// npm run clear-barriers -- x1 y1 z1 x2 y2 z2
// Removes every minecraft:barrier (terrain sync / ground anchors) in a box, slice by slice under /fill's
// 32768-block limit. Only barriers are touched; blocks the player placed are never replaced.
import { Rcon, readRconPassword } from '../rcon.js';

const nums = process.argv.slice(2).map(Number);
if (nums.length !== 6 || nums.some((n) => !Number.isInteger(n))) {
  console.error('usage: npm run clear-barriers -- x1 y1 z1 x2 y2 z2');
  process.exit(2);
}
const [ax, ay, az, bx, by, bz] = nums as [number, number, number, number, number, number];
const [x1, x2] = [Math.min(ax, bx), Math.max(ax, bx)];
const [y1, y2] = [Math.min(ay, by), Math.max(ay, by)];
const [z1, z2] = [Math.min(az, bz), Math.max(az, bz)];

const password = readRconPassword();
if (!password) {
  console.error('no RCON password: start the server once with server/start.ps1');
  process.exit(1);
}
const rcon = new Rcon(password);
const TILE = 256; // 16x16 chunks: vanilla's forceload limit per command
let cleared = 0;
let failed = 0;
try {
  // Tiles aligned to the chunk grid, so each one is at most 16x16 chunks.
  for (let ax0 = Math.floor(x1 / TILE) * TILE; ax0 <= x2; ax0 += TILE) {
    for (let az0 = Math.floor(z1 / TILE) * TILE; az0 <= z2; az0 += TILE) {
      const tx = Math.max(x1, ax0);
      const tz = Math.max(z1, az0);
      const ex = Math.min(x2, ax0 + TILE - 1);
      const ez = Math.min(z2, az0 + TILE - 1);
      await rcon.command(`forceload add ${tx} ${tz} ${ex} ${ez}`);
      await new Promise((r) => setTimeout(r, 1500)); // let the chunks load
      const perSlice = (ex - tx + 1) * (y2 - y1 + 1);
      const step = Math.max(1, Math.floor(32768 / perSlice));
      for (let z = tz; z <= ez; z += step) {
        const zEnd = Math.min(ez, z + step - 1);
        let out = '';
        for (let attempt = 0; attempt < 6; attempt++) {
          out = await rcon.command(`fill ${tx} ${y1} ${z} ${ex} ${y2} ${zEnd} air replace minecraft:barrier`);
          if (!/not loaded/i.test(out)) break;
          await new Promise((r) => setTimeout(r, 1000));
        }
        const m = /filled (\d+)/i.exec(out);
        if (m) cleared += Number(m[1]);
        else if (!/no blocks were filled/i.test(out) && ++failed <= 3) console.error(out);
      }
      await rcon.command(`forceload remove ${tx} ${tz} ${ex} ${ez}`);
    }
  }
  console.log(`cleared ${cleared} barrier blocks${failed ? ` (${failed} fills failed)` : ''}`);
} catch (e) {
  console.error(`rcon: ${(e as Error).message}`);
  process.exitCode = 1;
} finally {
  rcon.close();
}