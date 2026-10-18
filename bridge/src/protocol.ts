// Wire protocol v0, mirroring docs/protocol.md. The plugin's C# DTOs (plugin/src/Bridge/Envelope.cs) mirror these.
// Phase 2 implements the session subset: hello, hello_ack, ping, pong, error.

export const PROTOCOL_VERSION = 0;

export interface Envelope<P = Record<string, unknown>> {
  v: number;
  type: string;
  seq: number;
  ts: number;
  re: number | null;
  payload: P;
}

export interface Transform {
  mcOrigin: [number, number, number];
  scale: number;
  flipZ: boolean;
}

export interface HelloPayload {
  plugin: string;
  pluginVersion: string;
  game: string;
  unityVersion: string;
  transform?: Transform;
  features: string[];
}

export interface HelloAckPayload {
  bridgeVersion: string;
  /** `version` is null while the bridge has no Minecraft session configured (Phase 2). */
  mc: { version: string | null; username: string; spawned: boolean };
  features: string[];
  rates: { player_state_hz: number; hud_state_hz: number };
}

export interface PingPayload {
  nonce: string;
}

export type Vec3Tuple = [number, number, number];

export interface PlayerStatePayload {
  pos: Vec3Tuple;
  yaw: number;
  pitch: number;
  vel?: Vec3Tuple;
  onGround?: boolean;
  camera?: { pos: Vec3Tuple; yaw: number; pitch: number; fov: number };
  flags?: { sprinting?: boolean; sneaking?: boolean; swimming?: boolean; onBoat?: boolean };
}

export type McState = 'connecting' | 'spawned' | 'kicked' | 'disconnected';

export interface McStatusPayload {
  state: McState;
  reason: string | null;
}

export type BlocksNearPayload =
  | { kind: 'section'; section: Vec3Tuple; palette: string[]; data: string | null; light: null }
  | { kind: 'update'; pos: Vec3Tuple; state: string }
  | { kind: 'unload'; section: Vec3Tuple };

export interface BlocksRequestPayload {
  sections: Vec3Tuple[];
}

export type ErrorCode = 'version' | 'busy' | 'bad_message' | 'not_spawned' | 'rejected' | 'internal';

export interface ErrorPayload {
  code: ErrorCode;
  message: string;
  detail: Record<string, unknown>;
}

/** Every feature name protocol.md §4 defines for `hello.features`. */
export const KNOWN_FEATURES = [
  'player_state',
  'blocks_near',
  'dig',
  'place',
  'inventory',
  'hud_state',
  'terrain_upload',
] as const;

/** WebSocket close codes used by the bridge (application range 4000-4999). */
export const CloseCode = {
  busy: 4001,
  version: 4002,
} as const;

export type ParseResult =
  | { ok: true; env: Envelope }
  | { ok: false; code: 'bad_message' | 'version'; message: string };

const isObject = (x: unknown): x is Record<string, unknown> =>
  typeof x === 'object' && x !== null && !Array.isArray(x);
const isInt = (x: unknown): x is number => typeof x === 'number' && Number.isInteger(x);

/** Validates the envelope shape. Unknown types and unknown payload fields are allowed (protocol.md §2). */
export function parseEnvelope(raw: string): ParseResult {
  let data: unknown;
  try {
    data = JSON.parse(raw);
  } catch {
    return { ok: false, code: 'bad_message', message: 'not valid JSON' };
  }
  if (!isObject(data)) return { ok: false, code: 'bad_message', message: 'envelope must be a JSON object' };
  if (!isInt(data.v)) return { ok: false, code: 'bad_message', message: 'v must be an integer' };
  if (data.v !== PROTOCOL_VERSION) {
    return { ok: false, code: 'version', message: `protocol v${data.v} not supported, bridge speaks v${PROTOCOL_VERSION}` };
  }
  if (typeof data.type !== 'string' || !/^[a-z][a-z0-9_]*$/.test(data.type)) {
    return { ok: false, code: 'bad_message', message: 'type must be a snake_case string' };
  }
  if (!isInt(data.seq) || data.seq < 0) return { ok: false, code: 'bad_message', message: 'seq must be a non-negative integer' };
  if (!isInt(data.ts)) return { ok: false, code: 'bad_message', message: 'ts must be an integer (Unix ms)' };
  const re = data.re === undefined ? null : data.re;
  if (re !== null && !isInt(re)) return { ok: false, code: 'bad_message', message: 're must be an integer or null' };
  const payload = data.payload === undefined ? {} : data.payload;
  if (!isObject(payload)) return { ok: false, code: 'bad_message', message: 'payload must be an object' };
  return { ok: true, env: { v: data.v, type: data.type, seq: data.seq, ts: data.ts, re, payload } };
}

export function validateHello(p: Record<string, unknown>): string | null {
  for (const key of ['plugin', 'pluginVersion', 'game', 'unityVersion'] as const) {
    if (typeof p[key] !== 'string') return `hello.${key} must be a string`;
  }
  if (!Array.isArray(p.features) || !p.features.every((f) => typeof f === 'string')) {
    return 'hello.features must be an array of strings';
  }
  return null;
}

export function validatePing(p: Record<string, unknown>): string | null {
  return typeof p.nonce === 'string' ? null : 'ping.nonce must be a string';
}

const isFiniteNum = (x: unknown): x is number => typeof x === 'number' && Number.isFinite(x);
const isVec3 = (x: unknown): x is Vec3Tuple => Array.isArray(x) && x.length === 3 && x.every(isFiniteNum);
const isIntVec3 = (x: unknown): x is Vec3Tuple => isVec3(x) && x.every((n) => Number.isInteger(n));

export function validatePlayerState(p: Record<string, unknown>): string | null {
  if (!isVec3(p.pos)) return 'player_state.pos must be [x, y, z] finite numbers';
  if (!isFiniteNum(p.yaw)) return 'player_state.yaw must be a finite number';
  if (!isFiniteNum(p.pitch) || p.pitch < -90 || p.pitch > 90) return 'player_state.pitch must be in [-90, 90]';
  if (p.vel !== undefined && !isVec3(p.vel)) return 'player_state.vel must be [x, y, z]';
  return null;
}

export function validateBlocksRequest(p: Record<string, unknown>): string | null {
  if (!Array.isArray(p.sections) || !p.sections.every(isIntVec3)) return 'blocks_request.sections must be [[cx, sy, cz], ...]';
  return null;
}

export const FACES = ['down', 'up', 'north', 'south', 'west', 'east'] as const;
export type Face = (typeof FACES)[number];

export interface DigPayload {
  action: 'start' | 'cancel' | 'finish';
  pos: Vec3Tuple;
  face: Face;
}

export interface DigResultPayload {
  pos: Vec3Tuple;
  ok: boolean | null;
  /** The block being broken (for sounds / particles); optional. */
  state?: string | null;
  reason: string | null;
  progress: number;
}

export interface PlacePayload {
  against: Vec3Tuple;
  face: Face;
  cursor?: Vec3Tuple;
  hand?: 'main' | 'off';
}

export interface PlaceResultPayload {
  pos: Vec3Tuple;
  ok: boolean;
  state: string | null;
  reason: string | null;
}

export interface InventorySlot {
  slot: number;
  item: string;
  count: number;
  nbt: null;
}

export interface InventoryPayload {
  heldSlot: number;
  slots: Array<InventorySlot | null>;
  /** Item on the mouse cursor (slot -1) while an inventory screen is open. */
  cursor: InventorySlot | null;
}

export interface HudStatePayload {
  health: number;
  food: number;
  saturation: number;
  xpLevel: number;
  xpProgress: number;
  air: number;
  armor: number;
  gameMode: string;
  dead: boolean;
  effects: Array<{ id: string; amplifier: number; ticks: number }>;
}

export function validateDig(p: Record<string, unknown>): string | null {
  if (p.action !== 'start' && p.action !== 'cancel' && p.action !== 'finish') return 'dig.action must be start|cancel|finish';
  if (!isIntVec3(p.pos)) return 'dig.pos must be integer [x, y, z]';
  if (!FACES.includes(p.face as Face)) return 'dig.face must be one of down|up|north|south|west|east';
  return null;
}

export function validatePlace(p: Record<string, unknown>): string | null {
  if (!isIntVec3(p.against)) return 'place.against must be integer [x, y, z]';
  if (!FACES.includes(p.face as Face)) return 'place.face must be one of down|up|north|south|west|east';
  if (p.cursor !== undefined && !(isVec3(p.cursor) && p.cursor.every((n) => n >= 0 && n <= 1))) return 'place.cursor must be [0..1, 0..1, 0..1]';
  return null;
}

export function validateSelectSlot(p: Record<string, unknown>): string | null {
  return Number.isInteger(p.hotbar) && (p.hotbar as number) >= 0 && (p.hotbar as number) <= 8 ? null : 'select_slot.hotbar must be 0..8';
}

/** `hello_ack.features` is the intersection of what both sides support, in the bridge's order. */
export function intersectFeatures(bridge: readonly string[], plugin: readonly string[]): string[] {
  const wanted = new Set(plugin);
  return bridge.filter((f) => wanted.has(f));
}

/** Per-sender monotonically increasing envelope builder. */
export class EnvelopeWriter {
  private seq = 0;

  make<P>(type: string, payload: P, re: number | null = null): Envelope<P> {
    this.seq += 1;
    return { v: PROTOCOL_VERSION, type, seq: this.seq, ts: Date.now(), re, payload };
  }
}

export function errorPayload(code: ErrorCode, message: string, detail: Record<string, unknown> = {}): ErrorPayload {
  return { code, message, detail };
}
