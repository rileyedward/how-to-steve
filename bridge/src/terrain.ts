// terrain_upload (protocol.md §4.8): How to Fish island colliders → invisible minecraft:barrier blocks.
import type { Logger } from './log.js';
import type { Vec3Tuple } from './protocol.js';
import type { Rcon } from './rcon.js';

export interface TerrainUploadPayload {
  region: string;
  revision: number;
  origin: Vec3Tuple;
  size: Vec3Tuple;
  encoding: 'bitset-rle';
  data: string;
  final: boolean;
}

export interface TerrainAckPayload {
  region: string;
  revision: number;
  applied: number;
  skipped: number;
}

export function validateTerrainUpload(p: Record<string, unknown>): string | null {
  const intVec = (v: unknown) => Array.isArray(v) && v.length === 3 && v.every((n) => Number.isInteger(n));
  if (typeof p.region !== 'string' || p.region.length === 0) return 'terrain_upload.region must be a string';
  if (!Number.isInteger(p.revision)) return 'terrain_upload.revision must be an integer';
  if (!intVec(p.origin)) return 'terrain_upload.origin must be integer [x, y, z]';
  if (!intVec(p.size) || (p.size as number[]).some((n) => n <= 0 || n > 1024)) return 'terrain_upload.size must be [1..1024] x3';
  if (p.encoding !== 'bitset-rle') return 'terrain_upload.encoding must be bitset-rle';
  if (typeof p.data !== 'string') return 'terrain_upload.data must be base64';
  if (typeof p.final !== 'boolean') return 'terrain_upload.final must be a boolean';
  return null;
}

/** Cell index in protocol order: (y*sizeZ + z)*sizeX + x. */
export const cellIndex = (size: Vec3Tuple, x: number, y: number, z: number) => (y * size[2] + z) * size[0] + x;

/** Alternating uint16 LE run lengths, starting with an empty run. */
export function encodeBitsetRle(bits: Uint8Array): string {
  const runs: number[] = [];
  let current = 0;
  let run = 0;
  for (let i = 0; i < bits.length; i++) {
    const b = bits[i] ? 1 : 0;
    if (b === current) {
      if (run === 0xffff) {
        runs.push(run, 0); // split long runs with a zero-length run of the other value
        run = 0;
      }
      run++;
    } else {
      runs.push(run);
      current = b;
      run = 1;
    }
  }
  runs.push(run);
  const buf = Buffer.alloc(runs.length * 2);
  runs.forEach((r, i) => buf.writeUInt16LE(r, i * 2));
  return buf.toString('base64');
}

export function decodeBitsetRle(data: string, cells: number): Uint8Array {
  const buf = Buffer.from(data, 'base64');
  if (buf.length % 2 !== 0) throw new Error('bitset-rle data has an odd byte count');
  const out = new Uint8Array(cells);
  let pos = 0;
  let value = 0;
  for (let i = 0; i < buf.length; i += 2) {
    const run = buf.readUInt16LE(i);
    if (pos + run > cells) throw new Error(`bitset-rle overruns the region (${pos + run} > ${cells})`);
    if (value) out.fill(1, pos, pos + run);
    pos += run;
    value ^= 1;
  }
  if (pos !== cells) throw new Error(`bitset-rle covers ${pos} of ${cells} cells`);
  return out;
}

export interface ColumnRun {
  x: number;
  z: number;
  y1: number;
  y2: number;
}

/** Vertical runs of cells where `pick(i)` is true, in world coordinates. */
export function columnRuns(origin: Vec3Tuple, size: Vec3Tuple, pick: (i: number) => boolean): ColumnRun[] {
  const [sx, sy, sz] = size;
  const runs: ColumnRun[] = [];
  for (let z = 0; z < sz; z++) {
    for (let x = 0; x < sx; x++) {
      let start = -1;
      for (let y = 0; y <= sy; y++) {
        const on = y < sy && pick(cellIndex(size, x, y, z));
        if (on && start < 0) start = y;
        if (!on && start >= 0) {
          runs.push({ x: origin[0] + x, z: origin[2] + z, y1: origin[1] + start, y2: origin[1] + y - 1 });
          start = -1;
        }
      }
    }
  }
  return runs;
}

export interface BoxRun {
  x1: number;
  x2: number;
  z: number;
  y1: number;
  y2: number;
}

/** /fill's volume limit in vanilla. */
const FILL_LIMIT = 32768;

/**
 * Merges column runs that sit side by side along X with the same vertical extent into one box, so a flat
 * island top becomes a few long /fill commands instead of one per column.
 */
export function mergeRuns(runs: ColumnRun[]): BoxRun[] {
  const sorted = [...runs].sort((a, b) => a.z - b.z || a.y1 - b.y1 || a.y2 - b.y2 || a.x - b.x);
  const out: BoxRun[] = [];
  for (const r of sorted) {
    const last = out[out.length - 1];
    const height = r.y2 - r.y1 + 1;
    if (last && last.z === r.z && last.y1 === r.y1 && last.y2 === r.y2 && last.x2 === r.x - 1 && (last.x2 - last.x1 + 2) * height <= FILL_LIMIT) {
      last.x2 = r.x;
    } else {
      out.push({ x1: r.x, x2: r.x, z: r.z, y1: r.y1, y2: r.y2 });
    }
  }
  return out;
}

interface Region {
  revision: number;
  origin: Vec3Tuple;
  size: Vec3Tuple;
  bits: Uint8Array;
}

/**
 * Applies terrain uploads through RCON. Barriers only ever replace air ("keep"), and removal only
 * replaces barriers, so blocks the player placed are never touched (D4).
 */
export class TerrainWriter {
  private regions = new Map<string, Region>();
  private pending = new Map<string, { payload: TerrainUploadPayload; parts: string[] }>();
  private chain: Promise<unknown> = Promise.resolve();
  private forced = new Set<string>();

  constructor(
    private readonly rcon: Rcon | null,
    private readonly log: Logger,
  ) {}

  /** Collects chunks; returns an ack promise once the final chunk has been applied. */
  accept(p: TerrainUploadPayload): Promise<TerrainAckPayload> | null {
    const key = `${p.region}#${p.revision}`;
    const entry = this.pending.get(key) ?? { payload: p, parts: [] };
    entry.parts.push(p.data);
    this.pending.set(key, entry);
    if (!p.final) return null;
    this.pending.delete(key);
    const data = Buffer.concat(entry.parts.map((d) => Buffer.from(d, 'base64'))).toString('base64');
    const run = this.chain.then(() => this.apply({ ...entry.payload, data }));
    this.chain = run.catch(() => undefined);
    return run;
  }

  private async apply(p: TerrainUploadPayload): Promise<TerrainAckPayload> {
    const cells = p.size[0] * p.size[1] * p.size[2];
    const bits = decodeBitsetRle(p.data, cells);
    if (!this.rcon) {
      this.log.warn('terrain_upload ignored: no RCON password');
      return { region: p.region, revision: p.revision, applied: 0, skipped: cells };
    }

    const prev = this.regions.get(p.region);
    const sameBox = prev && prev.origin.join() === p.origin.join() && prev.size.join() === p.size.join();
    const started = Date.now();


    let applied = 0;
    let commands = 0;
    let failed = 0;
    const ops: Array<{ box: BoxRun; what: string; counts: boolean }> = [];
    if (prev && !sameBox) {
      // The island's footprint changed: clear the old box's barriers, then write the new one fully.
      for (const r of mergeRuns(columnRuns(prev.origin, prev.size, (i) => prev.bits[i] === 1))) ops.push({ box: r, what: 'air replace minecraft:barrier', counts: false });
    }
    const old = sameBox ? prev!.bits : null;
    for (const r of mergeRuns(columnRuns(p.origin, p.size, (i) => bits[i] === 1 && (!old || old[i] === 0)))) ops.push({ box: r, what: 'minecraft:barrier keep', counts: true });
    if (old) {
      for (const r of mergeRuns(columnRuns(p.origin, p.size, (i) => bits[i] === 0 && old[i] === 1))) ops.push({ box: r, what: 'air replace minecraft:barrier', counts: false });
    }

    // /fill only works in loaded chunks: force-load exactly the chunks we touch (vanilla caps one
    // forceload command at 256 chunks, so go chunk by chunk), give the server a moment, then write.
    const chunks = new Set<string>();
    for (const { box } of ops) for (let x = box.x1 >> 4; x <= box.x2 >> 4; x++) chunks.add(`${x},${box.z >> 4}`);
    let newChunks = 0;
    for (const c of chunks) {
      if (this.forced.has(c)) continue;
      const [cx, cz] = c.split(',').map(Number) as [number, number];
      await this.rcon.command(`forceload add ${cx * 16} ${cz * 16}`);
      this.forced.add(c);
      newChunks++;
    }
    if (newChunks > 0) await new Promise((r) => setTimeout(r, Math.min(5000, 250 + newChunks * 10)));

    for (const op of ops) {
      const out = await this.fill(op.box, op.what);
      commands++;
      const m = /filled (\d+)/i.exec(out);
      if (m) {
        if (op.counts) applied += Number(m[1]);
      } else if (!/no blocks were filled/i.test(out)) {
        failed++;
        if (failed <= 3) this.log.warn(`terrain fill failed: ${out}`);
      }
    }

    this.regions.set(p.region, { revision: p.revision, origin: p.origin, size: p.size, bits });
    const solid = bits.reduce((n, b) => n + b, 0);
    this.log.info(
      `terrain ${p.region} rev ${p.revision}: ${solid} solid cells, ${applied} new barriers, ${commands} fill commands (${failed} failed) in ${Date.now() - started} ms`,
    );
    return { region: p.region, revision: p.revision, applied, skipped: solid - applied };
  }

  /** One /fill, retried while freshly force-loaded chunks finish loading. */
  private async fill(r: BoxRun, what: string): Promise<string> {
    let out = '';
    for (let attempt = 0; attempt < 6; attempt++) {
      out = await this.rcon!.command(`fill ${r.x1} ${r.y1} ${r.z} ${r.x2} ${r.y2} ${r.z} ${what}`);
      if (!/not loaded/i.test(out)) return out;
      await new Promise((res) => setTimeout(res, 1000));
    }
    return out;
  }
}
