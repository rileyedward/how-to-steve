import { describe, expect, it } from 'vitest';
import { silentLog } from '../src/log.js';
import type { Rcon } from '../src/rcon.js';
import { cellIndex, columnRuns, mergeRuns, decodeBitsetRle, encodeBitsetRle, TerrainWriter, validateTerrainUpload } from '../src/terrain.js';
import type { Vec3Tuple } from '../src/protocol.js';

describe('bitset-rle', () => {
  it('round-trips and starts with an empty run', () => {
    const bits = new Uint8Array([1, 1, 0, 0, 0, 1, 0, 1, 1, 1]);
    const enc = encodeBitsetRle(bits);
    const raw = Buffer.from(enc, 'base64');
    expect(raw.readUInt16LE(0)).toBe(0); // leading empty run
    expect([...decodeBitsetRle(enc, bits.length)]).toEqual([...bits]);
  });

  it('splits runs longer than 65535', () => {
    const bits = new Uint8Array(70000).fill(1);
    expect([...decodeBitsetRle(encodeBitsetRle(bits), 70000)].every((b) => b === 1)).toBe(true);
  });

  it('rejects data that does not cover the region', () => {
    const enc = encodeBitsetRle(new Uint8Array([1, 0, 1]));
    expect(() => decodeBitsetRle(enc, 4)).toThrow(/covers/);
    expect(() => decodeBitsetRle(enc, 2)).toThrow(/overruns/);
  });
});

describe('columnRuns', () => {
  it('finds vertical runs in world coordinates', () => {
    const size: Vec3Tuple = [2, 4, 1];
    const bits = new Uint8Array(8);
    for (const y of [0, 1, 3]) bits[cellIndex(size, 1, y, 0)] = 1;
    expect(columnRuns([10, 60, -5], size, (i) => bits[i] === 1)).toEqual([
      { x: 11, z: -5, y1: 60, y2: 61 },
      { x: 11, z: -5, y1: 63, y2: 63 },
    ]);
  });
});

describe('mergeRuns', () => {
  it('merges neighbouring columns with the same height into one box', () => {
    const runs = [
      { x: 0, z: 0, y1: 60, y2: 63 }, { x: 1, z: 0, y1: 60, y2: 63 }, { x: 2, z: 0, y1: 60, y2: 63 },
      { x: 3, z: 0, y1: 61, y2: 63 }, { x: 0, z: 1, y1: 60, y2: 63 },
    ];
    expect(mergeRuns(runs)).toEqual([
      { x1: 0, x2: 2, z: 0, y1: 60, y2: 63 },
      { x1: 3, x2: 3, z: 0, y1: 61, y2: 63 },
      { x1: 0, x2: 0, z: 1, y1: 60, y2: 63 },
    ]);
  });
});

describe('TerrainWriter', () => {
  const fakeRcon = () => {
    const cmds: string[] = [];
    // Answers like Paper: fill reports how many blocks changed.
    const reply = (c: string) => {
      const m = /^fill (-?\d+) (-?\d+) (-?\d+) (-?\d+) (-?\d+) (-?\d+)/.exec(c);
      if (!m) return 'Marked chunk for force loading';
      const [x1, y1, z1, x2, y2, z2] = m.slice(1).map(Number) as number[];
      return `Successfully filled ${(x2! - x1! + 1) * (y2! - y1! + 1) * (z2! - z1! + 1)} block(s)`;
    };
    return { cmds, rcon: { command: async (c: string) => (cmds.push(c), reply(c)) } as unknown as Rcon };
  };
  const upload = (rev: number, bits: Uint8Array, size: Vec3Tuple = [1, 3, 1], final = true) => ({
    region: 'island:Island1', revision: rev, origin: [0, 60, 0] as Vec3Tuple, size, encoding: 'bitset-rle' as const,
    data: encodeBitsetRle(bits), final,
  });

  it('writes barriers with keep, and diffs later revisions', async () => {
    const { cmds, rcon } = fakeRcon();
    const w = new TerrainWriter(rcon, silentLog);
    const ack1 = await w.accept(upload(1, new Uint8Array([1, 1, 0])));
    expect(ack1).toEqual({ region: 'island:Island1', revision: 1, applied: 2, skipped: 0 });
    expect(cmds).toEqual(['forceload add 0 0', 'fill 0 60 0 0 61 0 minecraft:barrier keep']);

    cmds.length = 0;
    const ack2 = await w.accept(upload(2, new Uint8Array([0, 1, 1])));
    expect(ack2).toMatchObject({ applied: 1, skipped: 1 });
    expect(cmds).toEqual([
      'fill 0 62 0 0 62 0 minecraft:barrier keep',
      'fill 0 60 0 0 60 0 air replace minecraft:barrier',
    ]);
  });

  it('waits for the final chunk before applying', async () => {
    const { cmds, rcon } = fakeRcon();
    const w = new TerrainWriter(rcon, silentLog);
    expect(w.accept({ ...upload(1, new Uint8Array([1, 1, 1])), final: false })).toBeNull();
    expect(cmds).toEqual([]);
  });

  it('validates the payload', () => {
    expect(validateTerrainUpload(upload(1, new Uint8Array(3)) as unknown as Record<string, unknown>)).toBeNull();
    expect(validateTerrainUpload({ ...upload(1, new Uint8Array(3)), size: [0, 1, 1] })).toMatch(/size/);
    expect(validateTerrainUpload({ ...upload(1, new Uint8Array(3)), encoding: 'raw' })).toMatch(/encoding/);
  });
});
