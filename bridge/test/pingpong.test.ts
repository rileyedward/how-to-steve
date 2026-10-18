import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { WebSocket } from 'ws';
import { silentLog } from '../src/log.js';
import { CloseCode, type Envelope } from '../src/protocol.js';
import { HOST, startBridgeServer, type BridgeServer } from '../src/server.js';

/** A fake plugin client: collects incoming envelopes and lets the test await them in order. */
class FakePlugin {
  readonly ws: WebSocket;
  private seq = 0;
  private inbox: Envelope[] = [];
  private waiters: Array<(e: Envelope) => void> = [];
  readonly closed: Promise<number>;

  constructor(port: number) {
    this.ws = new WebSocket(`ws://${HOST}:${port}`);
    this.ws.on('message', (d) => {
      const e = JSON.parse(d.toString()) as Envelope;
      const w = this.waiters.shift();
      if (w) w(e);
      else this.inbox.push(e);
    });
    this.closed = new Promise((resolve) => this.ws.on('close', (code) => resolve(code)));
  }

  open() {
    return new Promise<void>((resolve, reject) => {
      this.ws.once('open', () => resolve());
      this.ws.once('error', reject);
    });
  }

  send(type: string, payload: unknown, v = 0) {
    this.seq += 1;
    this.ws.send(JSON.stringify({ v, type, seq: this.seq, ts: Date.now(), re: null, payload }));
    return this.seq;
  }

  next(): Promise<Envelope> {
    const e = this.inbox.shift();
    if (e) return Promise.resolve(e);
    return new Promise((resolve) => this.waiters.push(resolve));
  }
}

const hello = (features: string[] = []) => ({
  plugin: 'howtosteve',
  pluginVersion: '0.1.0',
  game: 'How to Fish',
  unityVersion: '6000.4.4f1',
  transform: { mcOrigin: [0, 64, 0], scale: 1, flipZ: true },
  features,
});

let server: BridgeServer;
const clients: FakePlugin[] = [];
const connect = async () => {
  const c = new FakePlugin(server.port);
  clients.push(c);
  await c.open();
  return c;
};

beforeEach(async () => {
  server = await startBridgeServer({ port: 0, features: ['player_state', 'blocks_near'], log: silentLog });
});

afterEach(async () => {
  for (const c of clients.splice(0)) c.ws.terminate();
  await server.close();
});

describe('bridge server', () => {
  it('only listens on loopback', () => {
    expect(HOST).toBe('127.0.0.1');
  });

  it('hello -> hello_ack -> ping -> pong', async () => {
    const p = await connect();

    const helloSeq = p.send('hello', hello(['blocks_near', 'dig']));
    const ack = await p.next();
    expect(ack).toMatchObject({ v: 0, type: 'hello_ack', re: helloSeq });
    expect(ack.payload).toMatchObject({ mc: { spawned: false }, features: ['blocks_near'] });

    const pingSeq = p.send('ping', { nonce: 'abc123' });
    const pong = await p.next();
    expect(pong).toMatchObject({ v: 0, type: 'pong', re: pingSeq, payload: { nonce: 'abc123' } });
    expect(pong.seq).toBeGreaterThan(ack.seq);
  });

  it('rejects a v:1 hello with error{code:"version"} and closes', async () => {
    const p = await connect();
    p.send('hello', hello(), 1);
    const err = await p.next();
    expect(err).toMatchObject({ type: 'error', payload: { code: 'version' } });
    expect(await p.closed).toBe(CloseCode.version);
  });

  it('rejects a second client with error{code:"busy"} and keeps the first', async () => {
    const first = await connect();
    first.send('hello', hello());
    await first.next();

    const second = await connect();
    const err = await second.next();
    expect(err).toMatchObject({ type: 'error', payload: { code: 'busy' } });
    expect(await second.closed).toBe(CloseCode.busy);

    first.send('ping', { nonce: 'still-here' });
    expect(await first.next()).toMatchObject({ type: 'pong', payload: { nonce: 'still-here' } });
  });

  it('accepts a new client after the first disconnects', async () => {
    const first = await connect();
    first.ws.close();
    await first.closed;
    await new Promise((r) => setTimeout(r, 20));

    const again = await connect();
    again.send('hello', hello());
    expect(await again.next()).toMatchObject({ type: 'hello_ack' });
  });

  it('ignores unknown types and reports bad messages without closing', async () => {
    const p = await connect();
    p.send('hello', hello());
    await p.next();

    p.send('from_the_future', { x: 1 });
    p.ws.send('not json');
    expect(await p.next()).toMatchObject({ type: 'error', payload: { code: 'bad_message' } });

    p.send('ping', { nonce: 'n' });
    expect(await p.next()).toMatchObject({ type: 'pong' });
  });

  it('requires hello before anything else', async () => {
    const p = await connect();
    const seq = p.send('ping', { nonce: 'early' });
    expect(await p.next()).toMatchObject({ type: 'error', re: seq, payload: { code: 'bad_message' } });
  });
});
