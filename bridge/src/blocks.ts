// Streams the Minecraft world around Steve to the plugin as blocks_near (protocol.md §4.3).
import { chunkOf, sectionKey } from './coords.js';
import type { BlocksNearPayload, Vec3Tuple } from './protocol.js';

export const AIR = 'minecraft:air';
/** Blocks the bridge writes for terrain sync; invisible by design, never streamed (protocol §4.3). */
const HIDDEN = new Set(['minecraft:barrier']);
/** Blocks Steve may stray past a section border before the view volume follows. */
const HYSTERESIS = 2;

/** What the streamer needs from a Minecraft world. Implemented over Mineflayer in minecraft.ts. */
export interface WorldSource {
  /** Lowest section Y and section count of the dimension (e.g. -4 and 24 for the overworld). */
  readonly minSectionY: number;
  readonly sectionCount: number;
  isColumnLoaded(cx: number, cz: number): boolean;
  /** Block state ids in protocol order ((ly*16+lz)*16+lx), or null for an all-air / missing section. */
  sectionStates(cx: number, sy: number, cz: number): ArrayLike<number> | null;
  /** Namespaced block state string, e.g. "minecraft:oak_stairs[facing=north,half=bottom,...]". */
  stateString(stateId: number): string;
}

export interface StreamerOptions {
  viewRadius?: number;
  verticalRadius?: number;
}

/** Encodes one section. Hidden blocks become air; an all-air section has data:null. */
export function encodeSection(
  world: WorldSource,
  cx: number,
  sy: number,
  cz: number,
): Extract<BlocksNearPayload, { kind: 'section' }> {
  const section: Vec3Tuple = [cx, sy, cz];
  const states = world.sectionStates(cx, sy, cz);
  if (!states) return { kind: 'section', section, palette: [AIR], data: null, light: null };

  const palette: string[] = [AIR];
  const indexOfState = new Map<number, number>();
  const indexOfName = new Map<string, number>([[AIR, 0]]);
  const buf = Buffer.alloc(4096 * 2);
  let nonAir = 0;

  for (let i = 0; i < 4096; i++) {
    const id = states[i] ?? 0;
    let idx = indexOfState.get(id);
    if (idx === undefined) {
      let name = world.stateString(id);
      if (HIDDEN.has(name.split('[')[0]!)) name = AIR;
      idx = indexOfName.get(name);
      if (idx === undefined) {
        idx = palette.length;
        palette.push(name);
        indexOfName.set(name, idx);
      }
      indexOfState.set(id, idx);
    }
    if (idx !== 0) nonAir++;
    buf.writeUInt16LE(idx, i * 2);
  }

  if (nonAir === 0) return { kind: 'section', section, palette: [AIR], data: null, light: null };
  return { kind: 'section', section, palette, data: buf.toString('base64'), light: null };
}

/**
 * Tracks which sections the plugin has, relative to Steve's position. All methods return the
 * messages to send, nearest first, so the caller decides how to deliver them.
 */
export class BlockStreamer {
  private readonly viewRadius: number;
  private readonly verticalRadius: number;
  private readonly sent = new Set<string>();
  private center: { cx: number; sy: number; cz: number } | null = null;

  constructor(
    private readonly world: WorldSource,
    opts: StreamerOptions = {},
  ) {
    this.viewRadius = opts.viewRadius ?? 4;
    this.verticalRadius = opts.verticalRadius ?? 3;
  }

  get sentCount(): number {
    return this.sent.size;
  }

  /** Steve moved. Sends newly in-range loaded sections and unloads out-of-range ones. */
  moveTo(x: number, y: number, z: number): BlocksNearPayload[] {
    const next = { cx: chunkOf(x), sy: Math.floor(y / 16), cz: chunkOf(z) };
    const c = this.center;
    if (c && c.cx === next.cx && c.sy === next.sy && c.cz === next.cz) return [];
    // Hysteresis: standing on a section border (head bob at y=64) must not flip the view volume back and forth.
    const within = (v: number, cell: number) => v >= cell * 16 - HYSTERESIS && v < cell * 16 + 16 + HYSTERESIS;
    if (c && within(x, c.cx) && within(y, c.sy) && within(z, c.cz)) return [];
    this.center = next;
    return this.sync();
  }

  /** A chunk column arrived from the server. */
  columnLoaded(cx: number, cz: number): BlocksNearPayload[] {
    if (!this.center || !this.inRangeXZ(cx, cz)) return [];
    return this.sync();
  }

  /** A chunk column was dropped by the server. */
  columnUnloaded(cx: number, cz: number): BlocksNearPayload[] {
    const out: BlocksNearPayload[] = [];
    for (const key of [...this.sent]) {
      const [kx, ky, kz] = key.split(',').map(Number) as Vec3Tuple;
      if (kx === cx && kz === cz) {
        this.sent.delete(key);
        out.push({ kind: 'unload', section: [kx, ky, kz] });
      }
    }
    return out;
  }

  /** A single block changed. Only forwarded if the plugin has that section. */
  blockChanged(x: number, y: number, z: number, stateId: number): BlocksNearPayload[] {
    const key = sectionKey(chunkOf(x), Math.floor(y / 16), chunkOf(z));
    if (!this.sent.has(key)) return [];
    let state = this.world.stateString(stateId);
    if (HIDDEN.has(state.split('[')[0]!)) state = AIR;
    return [{ kind: 'update', pos: [x, y, z], state }];
  }

  /** Plugin asked for a resend (blocks_request). */
  resend(sections: Vec3Tuple[]): BlocksNearPayload[] {
    const out: BlocksNearPayload[] = [];
    for (const [cx, sy, cz] of sections) {
      if (!this.world.isColumnLoaded(cx, cz)) continue;
      this.sent.add(sectionKey(cx, sy, cz));
      out.push(encodeSection(this.world, cx, sy, cz));
    }
    return out;
  }

  /** Forget everything (Minecraft disconnected); tells the plugin to drop what it has. */
  reset(): BlocksNearPayload[] {
    const out: BlocksNearPayload[] = [...this.sent].map((key) => ({
      kind: 'unload' as const,
      section: key.split(',').map(Number) as Vec3Tuple,
    }));
    this.sent.clear();
    this.center = null;
    return out;
  }

  private inRangeXZ(cx: number, cz: number): boolean {
    const c = this.center!;
    return Math.abs(cx - c.cx) <= this.viewRadius && Math.abs(cz - c.cz) <= this.viewRadius;
  }

  private sync(): BlocksNearPayload[] {
    const c = this.center!;
    const minSy = Math.max(this.world.minSectionY, c.sy - this.verticalRadius);
    const maxSy = Math.min(this.world.minSectionY + this.world.sectionCount - 1, c.sy + this.verticalRadius);

    const wanted: Array<{ key: string; cx: number; sy: number; cz: number; d: number }> = [];
    const wantedKeys = new Set<string>();
    for (let dx = -this.viewRadius; dx <= this.viewRadius; dx++) {
      for (let dz = -this.viewRadius; dz <= this.viewRadius; dz++) {
        const cx = c.cx + dx;
        const cz = c.cz + dz;
        if (!this.world.isColumnLoaded(cx, cz)) continue;
        for (let sy = minSy; sy <= maxSy; sy++) {
          const key = sectionKey(cx, sy, cz);
          wantedKeys.add(key);
          if (!this.sent.has(key)) wanted.push({ key, cx, sy, cz, d: dx * dx + dz * dz + (sy - c.sy) ** 2 });
        }
      }
    }

    const out: BlocksNearPayload[] = [];
    for (const key of [...this.sent]) {
      if (!wantedKeys.has(key)) {
        this.sent.delete(key);
        out.push({ kind: 'unload', section: key.split(',').map(Number) as Vec3Tuple });
      }
    }
    wanted.sort((a, b) => a.d - b.d);
    for (const w of wanted) {
      this.sent.add(w.key);
      out.push(encodeSection(this.world, w.cx, w.sy, w.cz));
    }
    return out;
  }
}
