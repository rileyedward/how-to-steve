// npm run demo  (optionally: npm run demo -- clear)
// Builds a small block sampler 4 blocks in front of Steve via RCON, to check rendering in How to Fish.
import { Rcon, readRconPassword } from '../rcon.js';

const password = readRconPassword();
if (!password) {
  console.error('no RCON password: start the server once with server/start.ps1');
  process.exit(1);
}

// Positioned 4 blocks ahead of Steve, level with his feet, in world-axis-aligned relative coords.
const at = 'execute at Steve rotated ~ 0 positioned ^ ^ ^5 align xyz run';
const clear = process.argv[2] === 'clear';

const row: string[] = [
  'stone', 'oak_planks', 'oak_log', 'glass', 'oak_leaves[persistent=true]', 'cobblestone', 'bricks',
  'oak_stairs[facing=south]', 'oak_slab', 'crafting_table', 'furnace[facing=south]', 'bookshelf', 'gold_block',
];
const commands: string[] = [];
const half = Math.floor(row.length / 2);
commands.push(`${at} fill ~-${half} ~-1 ~-1 ~${half} ~3 ~1 air`);
if (!clear) {
  commands.push(`${at} fill ~-${half} ~-1 ~-1 ~${half} ~-1 ~1 grass_block`);
  row.forEach((block, i) => commands.push(`${at} setblock ~${i - half} ~ ~ ${block}`));
  const top = ['poppy', 'dandelion', 'short_grass', 'torch', 'oak_fence', 'oak_sapling', 'blue_orchid'];
  top.forEach((block, i) => commands.push(`${at} setblock ~${i * 2 - half} ~ ~1 ${block}`));
  commands.push(`${at} setblock ~ ~1 ~ glowstone`);
}

const rcon = new Rcon(password);
try {
  for (const c of commands) {
    const out = await rcon.command(c);
    if (/error|unknown|incorrect|expected|no entity/i.test(out)) console.error(`${c}\n  -> ${out}`);
  }
  console.log(clear ? 'demo cleared' : `demo built (${commands.length} commands)`);
} catch (e) {
  console.error(`rcon: ${(e as Error).message}`);
  process.exitCode = 1;
} finally {
  rcon.close();
}
