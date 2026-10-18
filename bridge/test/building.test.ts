import { EventEmitter } from 'node:events';
import { afterEach, describe, expect, it } from 'vitest';
import { WebSocket } from 'ws';
import { silentLog } from '../src/log.js';
import type { Envelope, InventoryPayload } from '../src/protocol.js';
import { HOST, startBridgeServer, type BridgeServer, type MinecraftSource } from '../src/server.js';

/** Fake Minecraft side: records calls, answers like a spawned Steve. */
class FakeMc extends EventEmitter {
  status = { state: 'spawned' as const, reason: null };
  username = 'Steve';
  version = '1.21.11';
  calls: string[] = [];
  world() {
    return null;
  }
  applyPlayerState() {}
  dig(pos: [number, number, number], face: string, onResult: (r: unknown) => void) {
    this.calls.push(`dig ${pos.join(',')} ${face}`);
    onResult({ pos, ok: null, reason: null, progress: 0.5 });
    onResult({ pos, ok: true, reason: null, progress: 1 });
  }
  cancelDig() {
    this.calls.push('cancel');
  }
  async place(against: [number, number, number], face: string) {
    this.calls.push(`place ${against.join(',')} ${face}`);
    return { pos: [against[0], against[1] + 1, against[2]] as [number, number, number], ok: true, state: 'minecraft:cobblestone', reason: null };
  }
  async clickWindow(id: number, slot: number, button: number, mode: number) {
    this.calls.push(`click ${id}/${slot}/${button}/${mode}`);
    return null;
  }
  closeWindow(id: number) {
    this.calls.push(`close ${id}`);
  }
  selectSlot(n: number) {
    this.calls.push(`slot ${n}`);
  }
  inventorySnapshot(): InventoryPayload {
    const slots = Array.from({ length: 46 }, () => null) as InventoryPayload['slots'];
    slots[36] = { slot: 36, item: 'minecraft:cobblestone', count: 64, nbt: null };
    return { heldSlot: 0, slots, cursor: null };
  }
  hudSnapshot() {
    return { health: 20, food: 20, saturation: 5, xpLevel: 0, xpProgress: 0, air: 300, armor: 0, gameMode: 'survival', dead: false, effects: [] };
  }
}

let server: BridgeServer;
let ws: WebSocket;
afterEach(async () => {
  ws?.terminate();
  await server.close();
});

async function connect(mc: FakeMc) {
  server = await startBridgeServer({ port: 0, minecraft: mc as unknown as MinecraftSource, log: silentLog });
  ws = new WebSocket(`ws://${HOST}:${server.port}`);
  const inbox: Envelope[] = [];
  const waiters: Array<(e: Envelope) => void> = [];
  ws.on('message', (d) => {
    const e = JSON.parse(d.toString()) as Envelope;
    const w = waiters.shift();
    if (w) w(e);
    else inbox.push(e);
  });
  await new Promise((r) => ws.once('open', r));
  let seq = 0;
  const send = (type: string, payload: unknown) => {
    ws.send(JSON.stringify({ v: 0, type, seq: ++seq, ts: Date.now(), re: null, payload }));
    return seq;
  };
  const next = () => (inbox.length ? Promise.resolve(inbox.shift()!) : new Promise<Envelope>((r) => waiters.push(r)));
  const nextOf = async (type: string) => {
    for (;;) {
      const e = await next();
      if (e.type === type) return e;
    }
  };
  send('hello', {
    plugin: 'howtosteve', pluginVersion: '0.2.0', game: 'How to Fish', unityVersion: '6000.4.4f1',
    features: ['player_state', 'blocks_near', 'dig', 'place', 'inventory', 'hud_state', 'window'],
  });
  return { send, next, nextOf };
}

describe('building messages', () => {
  it('sends inventory and hud_state right after hello_ack', async () => {
    const mc = new FakeMc();
    const c = await connect(mc);
    const ack = await c.nextOf('hello_ack');
    expect(ack.payload.features).toEqual(['player_state', 'blocks_near', 'dig', 'place', 'inventory', 'hud_state', 'window']);
    const inv = await c.nextOf('inventory');
    expect((inv.payload as unknown as InventoryPayload).slots[36]).toMatchObject({ item: 'minecraft:cobblestone', count: 64 });
    expect((await c.nextOf('hud_state')).payload).toMatchObject({ health: 20, food: 20 });
  });

  it('dig -> progress then final dig_result with re; cancel goes through', async () => {
    const mc = new FakeMc();
    const c = await connect(mc);
    await c.nextOf('hello_ack');
    const seq = c.send('dig', { action: 'start', pos: [1, 64, 2], face: 'up' });
    const p = await c.nextOf('dig_result');
    expect(p).toMatchObject({ re: seq, payload: { ok: null, progress: 0.5 } });
    expect(await c.nextOf('dig_result')).toMatchObject({ re: seq, payload: { ok: true, pos: [1, 64, 2] } });
    c.send('dig', { action: 'cancel', pos: [1, 64, 2], face: 'up' });
    c.send('select_slot', { hotbar: 3 });
    c.send('ping', { nonce: 'sync' });
    await c.nextOf('pong');
    expect(mc.calls).toEqual(['dig 1,64,2 up', 'cancel', 'slot 3']);
  });

  it('place -> place_result with re; bad payloads are rejected', async () => {
    const mc = new FakeMc();
    const c = await connect(mc);
    await c.nextOf('hello_ack');
    const seq = c.send('place', { against: [0, 63, 0], face: 'up', cursor: [0.5, 1, 0.5] });
    expect(await c.nextOf('place_result')).toMatchObject({ re: seq, payload: { ok: true, pos: [0, 64, 0], state: 'minecraft:cobblestone' } });

    c.send('place', { against: [0, 63.5, 0], face: 'up' });
    expect((await c.nextOf('error')).payload).toMatchObject({ code: 'bad_message' });
    c.send('select_slot', { hotbar: 9 });
    expect((await c.nextOf('error')).payload).toMatchObject({ code: 'bad_message' });
  });

  it('forwards window open/close events and window_click/window_close', async () => {
    const mc = new FakeMc();
    const c = await connect(mc);
    await c.nextOf('hello_ack');
    mc.emit('window', { kind: 'open', windowId: 3, type: 'minecraft:crafting', title: 'Crafting', slots: [null], cursor: null });
    expect(await c.nextOf('window_open')).toMatchObject({ payload: { windowId: 3, type: 'minecraft:crafting', title: 'Crafting' } });
    c.send('window_click', { windowId: 3, slot: 1, button: 0, mode: 0 });
    c.send('window_close', { windowId: 3 });
    mc.emit('window', { kind: 'close', windowId: 3 });
    expect(await c.nextOf('window_close')).toMatchObject({ payload: { windowId: 3 } });
    c.send('window_click', { windowId: 3, slot: 1, button: 0, mode: 9 });
    expect((await c.nextOf('error')).payload).toMatchObject({ code: 'bad_message' });
    expect(mc.calls).toEqual(['click 3/1/0/0', 'close 3']);
  });
});