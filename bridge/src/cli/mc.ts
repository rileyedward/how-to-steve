// npm run mc -- "<server command>"   e.g. npm run mc -- "data get entity Steve Pos"
import { Rcon, readRconPassword } from '../rcon.js';

const cmd = process.argv.slice(2).join(' ').trim();
if (!cmd) {
  console.error('usage: npm run mc -- "<command>"');
  process.exit(2);
}
const password = readRconPassword();
if (!password) {
  console.error('no RCON password: start the server once with server/start.ps1 (it writes server/run/rcon.secret)');
  process.exit(1);
}
const rcon = new Rcon(password);
try {
  const out = await rcon.command(cmd.replace(/^\//, ''));
  console.log(out || '(no output)');
} catch (e) {
  console.error(`rcon: ${(e as Error).message}`);
  process.exitCode = 1;
} finally {
  rcon.close();
}
