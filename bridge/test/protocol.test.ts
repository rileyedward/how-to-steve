import { describe, expect, it } from 'vitest';
import { EnvelopeWriter, intersectFeatures, parseEnvelope, validateHello } from '../src/protocol.js';

const env = (over: Record<string, unknown> = {}) =>
  JSON.stringify({ v: 0, type: 'ping', seq: 1, ts: 1760770000123, re: null, payload: { nonce: 'a' }, ...over });

describe('parseEnvelope', () => {
  it('accepts a valid envelope', () => {
    const r = parseEnvelope(env());
    expect(r).toEqual({ ok: true, env: { v: 0, type: 'ping', seq: 1, ts: 1760770000123, re: null, payload: { nonce: 'a' } } });
  });

  it('rejects non-JSON and non-objects as bad_message', () => {
    expect(parseEnvelope('{nope')).toMatchObject({ ok: false, code: 'bad_message' });
    expect(parseEnvelope('[1,2]')).toMatchObject({ ok: false, code: 'bad_message' });
    expect(parseEnvelope('null')).toMatchObject({ ok: false, code: 'bad_message' });
  });

  it('rejects missing or mistyped envelope fields', () => {
    expect(parseEnvelope(env({ type: 'PlayerState' }))).toMatchObject({ ok: false, code: 'bad_message' });
    expect(parseEnvelope(env({ seq: 'x' }))).toMatchObject({ ok: false, code: 'bad_message' });
    expect(parseEnvelope(env({ ts: 1.5 }))).toMatchObject({ ok: false, code: 'bad_message' });
    expect(parseEnvelope(env({ re: 'x' }))).toMatchObject({ ok: false, code: 'bad_message' });
    expect(parseEnvelope(env({ payload: [] }))).toMatchObject({ ok: false, code: 'bad_message' });
    expect(parseEnvelope(env({ v: '0' }))).toMatchObject({ ok: false, code: 'bad_message' });
  });

  it('reports a version mismatch as code "version"', () => {
    expect(parseEnvelope(env({ v: 1 }))).toMatchObject({ ok: false, code: 'version' });
  });

  it('tolerates unknown types and unknown payload fields', () => {
    const r = parseEnvelope(env({ type: 'from_the_future', payload: { surprise: true }, extra: 1 }));
    expect(r.ok).toBe(true);
    if (r.ok) expect(r.env.payload).toEqual({ surprise: true });
  });

  it('defaults missing re to null and missing payload to {}', () => {
    const r = parseEnvelope(JSON.stringify({ v: 0, type: 'ping', seq: 3, ts: 0 }));
    expect(r).toMatchObject({ ok: true, env: { re: null, payload: {} } });
  });
});

describe('validateHello', () => {
  const hello = { plugin: 'stevefish', pluginVersion: '0.1.0', game: 'How to Fish', unityVersion: '6000.4.4f1', features: [] };
  it('accepts the protocol.md example shape', () => expect(validateHello(hello)).toBeNull());
  it('ignores unknown fields', () => expect(validateHello({ ...hello, future: 1 })).toBeNull());
  it('requires features as string[]', () => expect(validateHello({ ...hello, features: [1] })).toMatch(/features/));
  it('requires unityVersion', () => expect(validateHello({ ...hello, unityVersion: undefined })).toMatch(/unityVersion/));
});

describe('intersectFeatures', () => {
  it('keeps only features both sides support', () => {
    expect(intersectFeatures(['player_state', 'dig', 'place'], ['place', 'player_state', 'hud_state'])).toEqual(['player_state', 'place']);
    expect(intersectFeatures([], ['dig'])).toEqual([]);
  });
});

describe('EnvelopeWriter', () => {
  it('numbers messages monotonically and sets re', () => {
    const w = new EnvelopeWriter();
    const a = w.make('ping', { nonce: 'x' });
    const b = w.make('pong', { nonce: 'x' }, 7);
    expect([a.seq, b.seq]).toEqual([1, 2]);
    expect(a).toMatchObject({ v: 0, re: null });
    expect(b.re).toBe(7);
  });
});
