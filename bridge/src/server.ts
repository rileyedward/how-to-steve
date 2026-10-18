import { WebSocketServer, WebSocket } from 'ws';
import type { AddressInfo } from 'node:net';
import { BlockStreamer, type WorldSource } from './blocks.js';
import { log as defaultLog, type Logger } from './log.js';
import { validateTerrainUpload, type TerrainUploadPayload, type TerrainWriter } from './terrain.js';
import type { WindowEvent } from './minecraft.js';
import {
  CloseCode,
  EnvelopeWriter,
  errorPayload,
  intersectFeatures,
  parseEnvelope,
  validateBlocksRequest,
  validateHello,
  validatePing,
  validatePlayerState,
  validateDig,
  validatePlace,
  validateSelectSlot,
  type DigPayload,
  type DigResultPayload,
  type HudStatePayload,
  type InventoryPayload,
  type PlacePayload,
  type PlaceResultPayload,
  type BlocksNearPayload,
  type BlocksRequestPayload,
  type Envelope,
  type ErrorCode,
  type HelloAckPayload,
  type HelloPayload,
  type McStatusPayload,
  type PlayerStatePayload,
} from './protocol.js';

export const BRIDGE_VERSION = '0.1.0'
export const DEFAULT_PORT = 25599;
/** Loopback only, never 0.0.0.0 (protocol.md §1, decisions D1). */
export const HOST = '127.0.0.1';
/** Features this bridge implements (protocol.md §4). */
export const BRIDGE_FEATURES = ['player_state', 'blocks_near', 'dig', 'place', 'inventory', 'hud_state', 'terrain_upload', 'window'] as const;

/** What the server needs from the Minecraft side. MinecraftLink implements it; tests fake it. */
export interface MinecraftSource {
  readonly status: McStatusPayload;
  readonly username: string;
  readonly version: string;
  world(): WorldSource | null;
  applyPlayerState(ps: PlayerStatePayload): void;
  on(event: 'status', fn: (s: McStatusPayload) => void): unknown;
  on(event: 'columnLoad' | 'columnUnload', fn: (cx: number, cz: number) => void): unknown;
  on(event: 'blockUpdate', fn: (x: number, y: number, z: number, stateId: number) => void): unknown;
  on(event: 'inventory', fn: (inv: InventoryPayload) => void): unknown;
  on(event: 'hud', fn: (hud: HudStatePayload) => void): unknown;
  on(event: 'window', fn: (w: WindowEvent) => void): unknown;
  clickWindow?(windowId: number, slot: number, button: number, mode: number): Promise<string | null>;
  closeWindow?(windowId: number): void;
  windowSnapshot?(): WindowEvent | null;
  dig?(pos: [number, number, number], face: DigPayload['face'], onResult: (r: DigResultPayload) => void): void;
  cancelDig?(): void;
  place?(against: [number, number, number], face: PlacePayload['face'], cursor?: [number, number, number]): Promise<PlaceResultPayload>;
  selectSlot?(hotbar: number): void;
  inventorySnapshot?(): InventoryPayload | null;
  hudSnapshot?(): HudStatePayload | null;
}

export interface BridgeServerOptions {
  port?: number;
  features?: readonly string[];
  minecraft?: MinecraftSource | null;
  terrain?: TerrainWriter | null;
  viewRadius?: number;
  log?: Logger;
}

export interface BridgeServer {
  port: number;
  close(): Promise<void>;
}

interface Session {
  ws: WebSocket;
  out: EnvelopeWriter;
  hello: HelloPayload | null;
  features: Set<string>;
  streamer: BlockStreamer | null;
  lastPos: [number, number, number] | null;
  stateCount: number;
}

export async function startBridgeServer(opts: BridgeServerOptions = {}): Promise<BridgeServer> {
  const log = opts.log ?? defaultLog;
  const features = opts.features ?? BRIDGE_FEATURES;
  const mc = opts.minecraft ?? null;
  const wss = new WebSocketServer({ host: HOST, port: opts.port ?? DEFAULT_PORT });
  let current: Session | null = null;

  await new Promise<void>((resolve, reject) => {
    wss.once('listening', resolve);
    wss.once('error', reject);
  });
  const port = (wss.address() as AddressInfo).port;
  log.info(`listening on ${HOST}:${port}`);

  const send = (s: Session, env: Envelope<unknown>) => {
    if (s.ws.readyState === WebSocket.OPEN) s.ws.send(JSON.stringify(env));
  };
  const sendError = (s: Session, code: ErrorCode, message: string, re: number | null = null) => {
    log.warn(`-> error ${code}: ${message}`);
    send(s, s.out.make('error', errorPayload(code, message), re));
  };
  const sendBlocks = (s: Session, msgs: BlocksNearPayload[]) => {
    for (const m of msgs) send(s, s.out.make('blocks_near', m));
    const sections = msgs.filter((m) => m.kind === 'section').length;
    if (sections > 0) log.info(`-> blocks_near: ${sections} sections (${s.streamer?.sentCount ?? 0} live)`);
  };

  const ready = (s: Session) => s.hello !== null && s.features.has('blocks_near');

  /** (Re)creates the block streamer when Minecraft is spawned, or tears it down. */
  const syncStreamer = (s: Session) => {
    const world = mc?.world() ?? null;
    if (world && ready(s)) {
      if (!s.streamer) {
        s.streamer = new BlockStreamer(world, { viewRadius: opts.viewRadius });
        if (s.lastPos) sendBlocks(s, s.streamer.moveTo(...s.lastPos));
      }
    } else if (s.streamer) {
      sendBlocks(s, s.streamer.reset());
      s.streamer = null;
    }
  };

  if (mc) {
    mc.on('status', (status) => {
      const s = current;
      if (!s?.hello) return;
      send(s, s.out.make('mc_status', status));
      syncStreamer(s);
    });
    mc.on('columnLoad', (cx, cz) => {
      const s = current;
      if (s?.streamer) sendBlocks(s, s.streamer.columnLoaded(cx, cz));
    });
    mc.on('columnUnload', (cx, cz) => {
      const s = current;
      if (s?.streamer) sendBlocks(s, s.streamer.columnUnloaded(cx, cz));
    });
    mc.on('blockUpdate', (x, y, z, id) => {
      const s = current;
      if (s?.streamer) sendBlocks(s, s.streamer.blockChanged(x, y, z, id));
    });
    mc.on('inventory', (inv) => {
      const s = current;
      if (s?.hello && s.features.has('inventory')) send(s, s.out.make('inventory', inv));
    });
    mc.on('window', (w) => {
      const s = current;
      if (!s?.hello || !s.features.has('window')) return;
      if (w.kind === 'close') send(s, s.out.make('window_close', { windowId: w.windowId }));
      else {
        const { kind, ...payload } = w;
        send(s, s.out.make(kind === 'open' ? 'window_open' : 'window_update', payload));
      }
    });
    mc.on('hud', (hud) => {
      const s = current;
      if (s?.hello && s.features.has('hud_state')) send(s, s.out.make('hud_state', hud));
    });
  }

  wss.on('connection', (ws, req) => {
    const peer = `${req.socket.remoteAddress}:${req.socket.remotePort}`;
    const session: Session = {
      ws,
      out: new EnvelopeWriter(),
      hello: null,
      features: new Set(),
      streamer: null,
      lastPos: null,
      stateCount: 0,
    };

    if (current) {
      log.warn(`rejecting second client ${peer}: busy`);
      sendError(session, 'busy', 'another plugin is already connected');
      ws.close(CloseCode.busy, 'busy');
      return;
    }
    current = session;
    log.info(`plugin connected from ${peer}`);

    ws.on('message', (data, isBinary) => {
      if (isBinary) {
        sendError(session, 'bad_message', 'binary frames are not supported; send JSON text');
        return;
      }
      const parsed = parseEnvelope(data.toString());
      if (!parsed.ok) {
        sendError(session, parsed.code, parsed.message);
        if (parsed.code === 'version') ws.close(CloseCode.version, 'version');
        return;
      }
      try {
        handle(session, parsed.env);
      } catch (e) {
        log.error(`handling ${parsed.env.type} failed`, String((e as Error).stack ?? e));
        sendError(session, 'internal', (e as Error).message, parsed.env.seq);
      }
    });

    ws.on('close', (code) => {
      if (current === session) current = null;
      session.streamer = null;
      log.info(`plugin disconnected (${code})`);
    });
    ws.on('error', (err) => log.warn(`socket error: ${err.message}`));
  });

  function handle(s: Session, env: Envelope) {
    if (env.type !== 'hello' && s.hello === null) {
      sendError(s, 'bad_message', `expected hello first, got ${env.type}`, env.seq);
      return;
    }

    switch (env.type) {
      case 'hello': {
        const problem = validateHello(env.payload);
        if (problem) return sendError(s, 'bad_message', problem, env.seq);
        const hello = env.payload as unknown as HelloPayload;
        s.hello = hello;
        s.features = new Set(intersectFeatures(features, hello.features));
        log.info(
          `<- hello ${hello.plugin} ${hello.pluginVersion} game="${hello.game}" unity=${hello.unityVersion} features=[${hello.features.join(',')}]`,
          hello.transform ? { transform: hello.transform } : undefined,
        );
        const ack: HelloAckPayload = {
          bridgeVersion: BRIDGE_VERSION,
          mc: mc
            ? { version: mc.version, username: mc.username, spawned: mc.status.state === 'spawned' }
            : { version: null, username: 'Steve', spawned: false },
          features: [...s.features],
          rates: { player_state_hz: 20, hud_state_hz: 10 },
        };
        send(s, s.out.make('hello_ack', ack, env.seq));
        if (mc) send(s, s.out.make('mc_status', mc.status));
        syncStreamer(s);
        const inv = s.features.has('inventory') ? mc?.inventorySnapshot?.() : null;
        if (inv) send(s, s.out.make('inventory', inv));
        const hud = s.features.has('hud_state') ? mc?.hudSnapshot?.() : null;
        if (hud) send(s, s.out.make('hud_state', hud));
        const win = s.features.has('window') ? mc?.windowSnapshot?.() : null;
        if (win && win.kind !== 'close') {
          const { kind: _k, ...payload } = win;
          send(s, s.out.make('window_open', payload));
        }
        return;
      }
      case 'ping': {
        const problem = validatePing(env.payload);
        if (problem) return sendError(s, 'bad_message', problem, env.seq);
        log.debug(`<- ping ${String(env.payload.nonce)}`);
        send(s, s.out.make('pong', { nonce: env.payload.nonce }, env.seq));
        return;
      }
      case 'player_state': {
        if (!s.features.has('player_state')) return;
        const problem = validatePlayerState(env.payload);
        if (problem) return sendError(s, 'bad_message', problem, env.seq);
        const ps = env.payload as unknown as PlayerStatePayload;
        s.lastPos = ps.pos;
        if (++s.stateCount % 100 === 1) {
          log.info(`<- player_state #${s.stateCount} pos=[${ps.pos.map((n) => n.toFixed(2)).join(', ')}] yaw=${ps.yaw.toFixed(1)} pitch=${ps.pitch.toFixed(1)}`);
        }
        mc?.applyPlayerState(ps);
        if (s.streamer) sendBlocks(s, s.streamer.moveTo(...ps.pos));
        return;
      }
      case 'dig': {
        if (!s.features.has('dig')) return;
        const problem = validateDig(env.payload);
        if (problem) return sendError(s, 'bad_message', problem, env.seq);
        const dig = env.payload as unknown as DigPayload;
        if (!mc?.dig) return sendError(s, 'not_spawned', 'no Minecraft session', env.seq);
        if (dig.action === 'cancel') {
          mc.cancelDig?.();
          return;
        }
        log.info(`<- dig ${dig.action} [${dig.pos.join(', ')}] ${dig.face}`);
        mc.dig(dig.pos, dig.face, (r) => {
          if (r.ok !== null) log.info(`-> dig_result [${r.pos.join(', ')}] ok=${r.ok}${r.reason ? ` (${r.reason})` : ''}`);
          send(s, s.out.make('dig_result', r, env.seq));
        });
        return;
      }
      case 'place': {
        if (!s.features.has('place')) return;
        const problem = validatePlace(env.payload);
        if (problem) return sendError(s, 'bad_message', problem, env.seq);
        const pl = env.payload as unknown as PlacePayload;
        if (!mc?.place) return sendError(s, 'not_spawned', 'no Minecraft session', env.seq);
        log.info(`<- place against [${pl.against.join(', ')}] ${pl.face}`);
        void mc.place(pl.against, pl.face, pl.cursor).then((r) => {
          log.info(`-> place_result [${r.pos.join(', ')}] ok=${r.ok}${r.state ? ` ${r.state}` : ''}${r.reason ? ` (${r.reason})` : ''}`);
          send(s, s.out.make('place_result', r, env.seq));
        });
        return;
      }
      case 'terrain_upload': {
        if (!s.features.has('terrain_upload')) return;
        const problem = validateTerrainUpload(env.payload);
        if (problem) return sendError(s, 'bad_message', problem, env.seq);
        const tu = env.payload as unknown as TerrainUploadPayload;
        if (!opts.terrain) return sendError(s, 'rejected', 'terrain sync is not configured', env.seq);
        log.info(`<- terrain_upload ${tu.region} rev ${tu.revision} origin=[${tu.origin.join(', ')}] size=[${tu.size.join(', ')}]${tu.final ? '' : ' (part)'}`);
        const done = opts.terrain.accept(tu);
        done?.then(
          (ack) => send(s, s.out.make('terrain_ack', ack, env.seq)),
          (e: Error) => sendError(s, 'internal', `terrain: ${e.message}`, env.seq),
        );
        return;
      }
      case 'window_click': {
        const p = env.payload;
        const ints = [p.windowId, p.slot, p.button, p.mode].every((n) => Number.isInteger(n));
        if (!ints || (p.mode as number) < 0 || (p.mode as number) > 6) return sendError(s, 'bad_message', 'window_click needs integer windowId, slot, button, mode 0..6', env.seq);
        void mc?.clickWindow?.(p.windowId as number, p.slot as number, p.button as number, p.mode as number).then((err) => {
          if (err) log.warn(`window_click slot ${String(p.slot)}: ${err}`);
        });
        return;
      }
      case 'window_close': {
        if (!Number.isInteger(env.payload.windowId)) return sendError(s, 'bad_message', 'window_close needs windowId', env.seq);
        mc?.closeWindow?.(env.payload.windowId as number);
        return;
      }
      case 'select_slot': {
        const problem = validateSelectSlot(env.payload);
        if (problem) return sendError(s, 'bad_message', problem, env.seq);
        mc?.selectSlot?.(env.payload.hotbar as number);
        return;
      }
      case 'blocks_request': {
        const problem = validateBlocksRequest(env.payload);
        if (problem) return sendError(s, 'bad_message', problem, env.seq);
        if (s.streamer) sendBlocks(s, s.streamer.resend((env.payload as unknown as BlocksRequestPayload).sections));
        return;
      }
      case 'pong':
        log.debug(`<- pong re=${env.re}`);
        return;
      case 'error':
        log.warn(`<- error from plugin`, env.payload);
        return;
      default:
        // Unknown types are logged and ignored, never fatal (protocol.md §2).
        log.info(`<- ignoring unknown type '${env.type}'`);
    }
  }

  return {
    port,
    close: () =>
      new Promise<void>((resolve) => {
        for (const client of wss.clients) client.terminate();
        wss.close(() => resolve());
      }),
  };
}
