// Minimal Source RCON client for the local Paper server (127.0.0.1 only).
// Packet: int32 LE length | int32 LE id | int32 LE type | ASCII body | 0x00 0x00
import { Socket } from 'node:net';
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const TYPE_AUTH = 3;
const TYPE_COMMAND = 2;

export const DEFAULT_RCON_PORT = 25575;
export const DEFAULT_SECRET_PATH = resolve(dirname(fileURLToPath(import.meta.url)), '../../server/run/rcon.secret');

/** Password from STEVEFISH_RCON_PASSWORD, else server/run/rcon.secret (written by server/start.ps1). */
export function readRconPassword(path = DEFAULT_SECRET_PATH): string | null {
  if (process.env.STEVEFISH_RCON_PASSWORD) return process.env.STEVEFISH_RCON_PASSWORD;
  return existsSync(path) ? readFileSync(path, 'utf8').trim() : null;
}

export function encodePacket(id: number, type: number, body: string): Buffer {
  const payload = Buffer.from(body, 'utf8');
  const buf = Buffer.alloc(14 + payload.length);
  buf.writeInt32LE(10 + payload.length, 0);
  buf.writeInt32LE(id, 4);
  buf.writeInt32LE(type, 8);
  payload.copy(buf, 12);
  return buf; // last two bytes already 0
}

export class Rcon {
  private socket: Socket | null = null;
  private buffer = Buffer.alloc(0);
  private nextId = 1;
  private pending = new Map<number, { resolve: (s: string) => void; reject: (e: Error) => void }>();
  private chain: Promise<unknown> = Promise.resolve();

  constructor(
    private readonly password: string,
    private readonly port = DEFAULT_RCON_PORT,
    private readonly host = '127.0.0.1',
  ) {}

  /** Runs one command, connecting/authenticating on demand. Commands are serialized. */
  command(cmd: string): Promise<string> {
    const run = this.chain.then(async () => {
      await this.ensureConnected();
      return this.send(TYPE_COMMAND, cmd);
    });
    this.chain = run.catch(() => undefined);
    return run;
  }

  close(): void {
    this.socket?.destroy();
    this.socket = null;
  }

  private async ensureConnected(): Promise<void> {
    if (this.socket && !this.socket.destroyed) return;
    const socket = new Socket();
    this.socket = socket;
    this.buffer = Buffer.alloc(0);
    socket.on('data', (d) => this.onData(d));
    socket.on('close', () => {
      for (const p of this.pending.values()) p.reject(new Error('rcon connection closed'));
      this.pending.clear();
      if (this.socket === socket) this.socket = null;
    });
    await new Promise<void>((res, rej) => {
      socket.once('error', rej);
      socket.connect(this.port, this.host, () => {
        socket.off('error', rej);
        socket.on('error', () => undefined);
        res();
      });
    });
    try {
      await this.send(TYPE_AUTH, this.password);
    } catch (e) {
      this.close();
      throw e;
    }
  }

  private send(type: number, body: string): Promise<string> {
    const id = this.nextId++;
    return new Promise<string>((res, rej) => {
      this.pending.set(id, { resolve: res, reject: rej });
      this.socket!.write(encodePacket(id, type, body));
      setTimeout(() => {
        if (this.pending.delete(id)) rej(new Error(`rcon timeout for request ${id}`));
      }, 5000).unref();
    });
  }

  private onData(data: Buffer): void {
    this.buffer = Buffer.concat([this.buffer, data]);
    while (this.buffer.length >= 4) {
      const len = this.buffer.readInt32LE(0);
      if (this.buffer.length < 4 + len) return;
      const id = this.buffer.readInt32LE(4);
      const body = this.buffer.toString('utf8', 12, 4 + len - 2);
      this.buffer = this.buffer.subarray(4 + len);
      if (id === -1) {
        // Auth failure is reported with id -1.
        const first = this.pending.entries().next().value;
        if (first) {
          this.pending.delete(first[0]);
          first[1].reject(new Error('rcon authentication failed'));
        }
        continue;
      }
      const p = this.pending.get(id);
      if (p) {
        this.pending.delete(id);
        p.resolve(body);
      }
    }
  }
}
