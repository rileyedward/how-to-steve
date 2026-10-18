import { describe, expect, it } from 'vitest';
import { BlockStreamer, encodeSection, type WorldSource } from '../src/blocks.js';
import { notchianToMineflayerPitch, notchianToMineflayerYaw } from '../src/coords.js';
import { encodePacket } from '../src/rcon.js';
import { validatePlayerState } from '../src/protocol.js';
import { titleText } from '../src/minecraft.js';

const NAMES: Record<number, string> = {
  0: 'minecraft:air',
  1: 'minecraft:stone',
  2: 'minecraft:oak_stairs[facing=north,half=bottom,shape=straight,waterlogged=false]',
  3: 'minecraft:barrier',
};

/** Fake world: a set of loaded columns and explicit blocks. */
function fakeWorld(blocks: Record<string, number>, loaded: Array<[number, number]>): WorldSource {
  const loadedSet = new Set(loaded.map(([x, z]) => `${x},${z}`));
  return {
    minSectionY: -4,
    sectionCount: 24,
    isColumnLoaded: (cx, cz) => loadedSet.has(`${cx},${cz}`),
    sectionStates: (cx, sy, cz) => {
      const out = new Uint32Array(4096);
      let any = false;
      for (const [k, id] of Object.entries(blocks)) {
        const [x, y, z] = k.split(',').map(Number) as [number, number, number];
        if (Math.floor(x / 16) !== cx || Math.floor(y / 16) !== sy || Math.floor(z / 16) !== cz) continue;
        const lx = ((x % 16) + 16) % 16, ly = ((y % 16) + 16) % 16, lz = ((z % 16) + 16) % 16;
        out[(ly * 16 + lz) * 16 + lx] = id;
        any = true;
      }
      return any ? out : null;
    },
    stateString: (id) => NAMES[id] ?? 'minecraft:air',
  };
}

const decode = (data: string) => {
  const buf = Buffer.from(data, 'base64');
  return Array.from({ length: 4096 }, (_, i) => buf.readUInt16LE(i * 2));
};

describe('encodeSection', () => {
  it('uses Minecraft index order and a palette with air first', () => {
    const w = fakeWorld({ '1,65,2': 1, '-1,64,-1': 2 }, [[0, 0]]);
    const s = encodeSection(w, 0, 4, 0);
    expect(s.palette).toEqual(['minecraft:air', 'minecraft:stone']);
    const idx = decode(s.data!);
    expect(idx[(1 * 16 + 2) * 16 + 1]).toBe(1);
    expect(idx.filter((n) => n !== 0)).toHaveLength(1);

    const neg = encodeSection(w, -1, 4, -1);
    expect(neg.palette[1]).toMatch(/^minecraft:oak_stairs\[facing=north/);
    expect(decode(neg.data!)[(0 * 16 + 15) * 16 + 15]).toBe(1);
  });

  it('sends all-air and barrier-only sections as data:null', () => {
    const w = fakeWorld({ '3,70,3': 3 }, [[0, 0]]);
    expect(encodeSection(w, 0, 4, 0)).toEqual({ kind: 'section', section: [0, 4, 0], palette: ['minecraft:air'], data: null, light: null });
    expect(encodeSection(w, 5, 4, 5).data).toBeNull();
  });

  it('hides barriers among other blocks', () => {
    const w = fakeWorld({ '0,64,0': 3, '1,64,0': 1 }, [[0, 0]]);
    const s = encodeSection(w, 0, 4, 0);
    expect(s.palette).toEqual(['minecraft:air', 'minecraft:stone']);
    expect(decode(s.data!)[0]).toBe(0);
  });
});

describe('BlockStreamer', () => {
  const loaded: Array<[number, number]> = [];
  for (let x = -6; x <= 6; x++) for (let z = -6; z <= 6; z++) loaded.push([x, z]);

  it('sends the view volume nearest first, and only the delta when moving', () => {
    const st = new BlockStreamer(fakeWorld({}, loaded), { viewRadius: 1, verticalRadius: 1 });
    const first = st.moveTo(8, 70, 8);
    expect(first).toHaveLength(3 * 3 * 3);
    expect(first[0]).toMatchObject({ kind: 'section', section: [0, 4, 0] });

    expect(st.moveTo(9, 71, 9)).toEqual([]); // same section
    expect(st.moveTo(9, 63.5, 9)).toEqual([]); // just across the y=64 border: hysteresis holds
    expect(st.moveTo(17, 70, 9)).toEqual([]); // 1 block into the next chunk: still held

    const moved = st.moveTo(24, 70, 8); // one chunk east
    expect(moved.filter((m) => m.kind === 'unload')).toHaveLength(9);
    expect(moved.filter((m) => m.kind === 'section')).toHaveLength(9);
    expect(st.sentCount).toBe(27);
  });

  it('clamps to world height', () => {
    const st = new BlockStreamer(fakeWorld({}, loaded), { viewRadius: 0, verticalRadius: 3 });
    expect(st.moveTo(0, -60, 0)).toHaveLength(4); // sections -4..-1
  });

  it('skips unloaded columns until they load', () => {
    const st = new BlockStreamer(fakeWorld({}, [[0, 0]]), { viewRadius: 1, verticalRadius: 0 });
    expect(st.moveTo(0, 64, 0)).toHaveLength(1);
  });

  it('forwards block updates only for sections the plugin has, hiding barriers', () => {
    const st = new BlockStreamer(fakeWorld({}, loaded), { viewRadius: 0, verticalRadius: 0 });
    st.moveTo(0, 64, 0);
    expect(st.blockChanged(1, 65, 1, 1)).toEqual([{ kind: 'update', pos: [1, 65, 1], state: 'minecraft:stone' }]);
    expect(st.blockChanged(1, 65, 1, 3)).toEqual([{ kind: 'update', pos: [1, 65, 1], state: 'minecraft:air' }]);
    expect(st.blockChanged(100, 65, 1, 1)).toEqual([]);
  });

  it('unloads a column the server dropped, and resets everything', () => {
    const st = new BlockStreamer(fakeWorld({}, loaded), { viewRadius: 1, verticalRadius: 0 });
    st.moveTo(0, 64, 0);
    expect(st.columnUnloaded(1, 1)).toEqual([{ kind: 'unload', section: [1, 4, 1] }]);
    expect(st.reset()).toHaveLength(8);
    expect(st.sentCount).toBe(0);
  });
});

describe('coords', () => {
  const close = (a: number, b: number) => expect(Math.abs(a - b)).toBeLessThan(1e-9);
  it('matches mineflayer conversions (notchian = deg(PI - yaw), deg(-pitch))', () => {
    for (const yaw of [0, 90, 180, -90, 135]) {
      const r = notchianToMineflayerYaw(yaw);
      const back = ((((180 - (r * 180) / Math.PI) % 360) + 540) % 360) - 180;
      close(((back - yaw + 540) % 360) - 180, 0);
    }
    close(notchianToMineflayerPitch(90), -Math.PI / 2);
  });
});

describe('validatePlayerState', () => {
  it('accepts the protocol example and rejects bad shapes', () => {
    expect(validatePlayerState({ pos: [12.5, 70, -3.25], yaw: 135, pitch: 10 })).toBeNull();
    expect(validatePlayerState({ pos: [1, 2], yaw: 0, pitch: 0 })).toMatch(/pos/);
    expect(validatePlayerState({ pos: [1, 2, NaN], yaw: 0, pitch: 0 })).toMatch(/pos/);
    expect(validatePlayerState({ pos: [1, 2, 3], yaw: 0, pitch: 91 })).toMatch(/pitch/);
  });
});

describe('rcon', () => {
  it('encodes packets as length|id|type|body|00 00', () => {
    const p = encodePacket(7, 2, 'list');
    expect(p.readInt32LE(0)).toBe(14);
    expect(p.readInt32LE(4)).toBe(7);
    expect(p.readInt32LE(8)).toBe(2);
    expect(p.toString('utf8', 12, 16)).toBe('list');
    expect([...p.subarray(16)]).toEqual([0, 0]);
  });
});

describe('titleText', () => {
  it('reads plain, JSON and NBT-style chat components', () => {
    expect(titleText('Chest')).toBe('Chest');
    expect(titleText('{"translate":"container.crafting"}')).toBe('container.crafting');
    expect(titleText({ type: 'compound', value: { translate: { type: 'string', value: 'container.crafting' } } })).toBe('container.crafting');
    expect(titleText({ text: '', translate: 'container.furnace' })).toBe('container.furnace');
  });
});