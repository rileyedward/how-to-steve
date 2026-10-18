import { log } from './log.js';
import { MinecraftLink } from './minecraft.js';
import { Rcon, readRconPassword } from './rcon.js';
import { DEFAULT_PORT, startBridgeServer } from './server.js';
import { TerrainWriter } from './terrain.js';

const port = process.env.HOWTOSTEVE_PORT ? Number(process.env.HOWTOSTEVE_PORT) : DEFAULT_PORT;
if (!Number.isInteger(port) || port <= 0 || port > 65535) {
  log.error(`invalid HOWTOSTEVE_PORT: ${process.env.HOWTOSTEVE_PORT}`);
  process.exit(1);
}

const mcPort = process.env.HOWTOSTEVE_MC_PORT ? Number(process.env.HOWTOSTEVE_MC_PORT) : 25565;
const offline = process.env.HOWTOSTEVE_NO_MC === '1';

const password = readRconPassword();
const rcon = password ? new Rcon(password) : null;
if (!rcon && !offline) log.warn('no RCON password (server/run/rcon.secret); long-distance teleports disabled');

const minecraft = offline ? null : new MinecraftLink({ host: '127.0.0.1', port: mcPort, rcon, log });
const terrain = offline ? null : new TerrainWriter(rcon, log);
const server = await startBridgeServer({ port, minecraft, terrain });
minecraft?.start();

const shutdown = async () => {
  log.info('shutting down');
  minecraft?.stop();
  rcon?.close();
  await server.close();
  process.exit(0);
};
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);

// The launcher runs us without a console, so it asks us to stop by writing "stop" to stdin.
if (!process.stdin.isTTY) {
  process.stdin.setEncoding('utf8');
  process.stdin.on('data', (chunk: string) => {
    if (chunk.split(/\r?\n/).some((l) => l.trim() === 'stop')) void shutdown();
  });
}
