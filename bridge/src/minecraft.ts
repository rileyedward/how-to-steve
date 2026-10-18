// Steve: a Mineflayer bot on the local Paper server, posed by How to Fish (decisions D3).
import { EventEmitter } from 'node:events';
import mineflayer, { type Bot } from 'mineflayer';
import prismarineBlock from 'prismarine-block';
import { Vec3 } from 'vec3';
import type { WorldSource } from './blocks.js';
import { notchianToMineflayerPitch, notchianToMineflayerYaw } from './coords.js';
import { log as defaultLog, type Logger } from './log.js';
import {
  FACES,
  type DigResultPayload,
  type Face,
  type HudStatePayload,
  type InventoryPayload,
  type McState,
  type McStatusPayload,
  type PlaceResultPayload,
  type PlayerStatePayload,
  type Vec3Tuple,
} from './protocol.js';
import type { Rcon } from './rcon.js';

export const MC_VERSION = '1.21.11';
const BACKOFF_S = [1, 2, 4, 8, 15, 30];
/** Moves longer than this also go through RCON /tp, because Mineflayer stops sending position packets
 *  while the chunk under Steve isn't loaded, and the server only loads chunks where it thinks Steve is. */
const TELEPORT_DISTANCE = 32;

export interface MinecraftLinkOptions {
  host?: string;
  port?: number;
  username?: string;
  rcon?: Rcon | null;
  log?: Logger;
}

export interface MinecraftLinkEvents {
  status: [McStatusPayload];
  columnLoad: [number, number];
  columnUnload: [number, number];
  blockUpdate: [number, number, number, number];
  inventory: [InventoryPayload];
  hud: [HudStatePayload];
  window: [WindowEvent];
}

export interface WindowSlot {
  slot: number;
  item: string;
  count: number;
}

export type WindowEvent =
  | { kind: 'open' | 'update'; windowId: number; type: string; title: string; slots: Array<WindowSlot | null>; cursor: WindowSlot | null }
  | { kind: 'close'; windowId: number };

export class MinecraftLink extends EventEmitter<MinecraftLinkEvents> {
  private bot: Bot | null = null;
  private stateValue: McStatusPayload = { state: 'disconnected', reason: null };
  private attempt = 0;
  private stopped = false;
  private reconnectTimer: NodeJS.Timeout | null = null;
  private stateNames = new Map<number, string>();
  private lastTeleport = 0;
  private readonly log: Logger;

  constructor(private readonly opts: MinecraftLinkOptions = {}) {
    super();
    this.log = opts.log ?? defaultLog;
  }

  get status(): McStatusPayload {
    return this.stateValue;
  }

  get username(): string {
    return this.opts.username ?? 'Steve';
  }

  get version(): string {
    return MC_VERSION;
  }

  start(): void {
    this.stopped = false;
    this.connect();
  }

  stop(): void {
    this.stopped = true;
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    this.bot?.quit('bridge stopping');
    this.bot = null;
  }

  /** World view for the BlockStreamer, or null while not spawned. */
  world(): WorldSource | null {
    const bot = this.bot;
    if (!bot || this.stateValue.state !== 'spawned') return null;
    // Mineflayer sets these from the dimension codec but its typings don't declare them.
    const game = bot.game as unknown as { height?: number; minY?: number };
    const height = game.height ?? 384;
    const minY = game.minY ?? -64;
    const Block = prismarineBlock(bot.registry);
    return {
      minSectionY: minY >> 4,
      sectionCount: height >> 4,
      isColumnLoaded: (cx, cz) => bot.world.getColumn(cx, cz) != null,
      sectionStates: (cx, sy, cz) => {
        const column = bot.world.getColumn(cx, cz) as unknown as
          | { minY: number; sections: Array<{ isEmpty(): boolean; get(i: number): number } | null> }
          | undefined;
        const section = column?.sections[sy - (column.minY >> 4)];
        if (!section || section.isEmpty()) return null;
        const out = new Uint32Array(4096);
        for (let i = 0; i < 4096; i++) out[i] = section.get(i);
        return out;
      },
      stateString: (id) => {
        let s = this.stateNames.get(id);
        if (s === undefined) {
          s = describeState(Block, bot, id);
          this.stateNames.set(id, s);
        }
        return s;
      },
    };
  }

  /** Writes How to Fish's pose onto Steve. Physics is off, so Mineflayer just reports it to the server. */
  applyPlayerState(ps: PlayerStatePayload): void {
    const bot = this.bot;
    if (!bot?.entity || this.stateValue.state !== 'spawned') return;
    const [x, y, z] = ps.pos;
    const target = new Vec3(x, y, z);
    const far = bot.entity.position.distanceTo(target) > TELEPORT_DISTANCE;
    bot.entity.position = target;
    bot.entity.yaw = notchianToMineflayerYaw(ps.yaw);
    bot.entity.pitch = notchianToMineflayerPitch(ps.pitch);
    bot.entity.onGround = ps.onGround ?? true;
    this.sneaking = ps.flags?.sneaking ?? false;
    if (ps.vel) bot.entity.velocity = new Vec3(0, 0, 0);
    if (far && this.opts.rcon && Date.now() - this.lastTeleport > 500) {
      this.lastTeleport = Date.now();
      const cmd = `tp ${this.username} ${x.toFixed(3)} ${y.toFixed(3)} ${z.toFixed(3)} ${ps.yaw.toFixed(1)} ${ps.pitch.toFixed(1)}`;
      this.opts.rcon.command(cmd).catch((e: Error) => this.log.warn(`rcon tp failed: ${e.message}`));
    }
  }

  // ---------- Phase 4: building, inventory, HUD ----------

  private digging: { pos: string; cancel: () => void } | null = null;
  private sneaking = false;

  /**
   * Starts breaking the block at pos (survival timing is Mineflayer's/the server's). Calls onResult with
   * ok:null progress updates at ~5 Hz, then exactly one final ok:true|false.
   */
  dig(pos: Vec3Tuple, face: Face, onResult: (r: DigResultPayload) => void): void {
    const bot = this.bot;
    const fail = (reason: string) => onResult({ pos, ok: false, reason, progress: 0 });
    if (!bot?.entity || this.stateValue.state !== 'spawned') return fail('not_spawned');
    this.cancelDig();

    const block = bot.blockAt(new Vec3(...pos));
    if (!block) return fail('unloaded');
    if (block.name === 'air' || block.name === 'barrier') return fail('nothing_there');
    if (!bot.canDigBlock(block)) return fail('cannot_dig');

    const state = this.stateString(block.stateId);
    const total = Math.max(1, bot.digTime(block));
    const started = Date.now();
    const timer = setInterval(() => {
      onResult({ pos, ok: null, reason: null, progress: Math.min(0.99, (Date.now() - started) / total), state });
    }, 200);
    let settled = false;
    const finish = (ok: boolean, reason: string | null) => {
      if (settled) return;
      settled = true;
      clearInterval(timer);
      if (this.digging?.pos === pos.join(',')) this.digging = null;
      onResult({ pos, ok, reason, progress: ok ? 1 : 0, state });
    };
    this.digging = {
      pos: pos.join(','),
      cancel: () => {
        try {
          bot.stopDigging();
        } catch {
          /* not digging */
        }
        finish(false, 'cancelled');
      },
    };

    // Steve's look comes from How to Fish, so don't let Mineflayer turn his head; tell it the face instead.
    (bot as unknown as { targetDigFace: number }).targetDigFace = FACES.indexOf(face);
    bot.dig(block, 'ignore').then(
      () => finish(true, null),
      (e: Error) => finish(false, e.message),
    );
  }

  cancelDig(): void {
    this.digging?.cancel();
    this.digging = null;
  }

  /**
   * Right-click with the held item on a face of `against`. If `against` is air (How to Fish ground that
   * Minecraft doesn't know about yet), an invisible barrier is put there first (decisions D18).
   */
  async place(against: Vec3Tuple, face: Face, cursor: Vec3Tuple | undefined): Promise<PlaceResultPayload> {
    const bot = this.bot;
    const d = FACE_VECTORS[face];
    const dest: Vec3Tuple = [against[0] + d[0], against[1] + d[1], against[2] + d[2]];
    const fail = (reason: string): PlaceResultPayload => ({ pos: dest, ok: false, state: null, reason });
    if (!bot?.entity || this.stateValue.state !== 'spawned') return fail('not_spawned');
    let ref = bot.blockAt(new Vec3(...against));
    if (!ref) return fail('unloaded');
    if (ref.name === 'air' || ref.name === 'cave_air') {
      if (!this.opts.rcon) return fail('no_rcon_for_ground');
      await this.opts.rcon.command(`setblock ${against[0]} ${against[1]} ${against[2]} minecraft:barrier keep`);
      for (let i = 0; i < 20 && bot.blockAt(new Vec3(...against))?.name !== 'barrier'; i++) await sleep(50);
      ref = bot.blockAt(new Vec3(...against));
      if (ref?.name !== 'barrier') return fail('ground_anchor_failed');
    }

    // Right-clicking a crafting table, furnace, chest... opens it, like vanilla (sneak to place against it instead).
    if (INTERACTABLE.test(ref.name) && !this.sneaking) {
      try {
        await bot.activateBlock(ref, new Vec3(...d));
        return { pos: against, ok: false, state: null, reason: 'opened' };
      } catch (e) {
        return fail((e as Error).message);
      }
    }

    if (!bot.heldItem) return fail('empty_hand');
    try {
      // Internal Mineflayer API (lib/plugins/place_block.js); the public placeBlock always turns Steve's head.
      const placeWithOptions = (bot as unknown as { _placeBlockWithOptions: PlaceWithOptions })._placeBlockWithOptions;
      await placeWithOptions(ref, new Vec3(...d), {
        forceLook: 'ignore',
        swingArm: 'right',
        ...(cursor ? { delta: new Vec3(...cursor) } : {}),
      });
    } catch (e) {
      return fail((e as Error).message);
    }
    const placed = bot.blockAt(new Vec3(...dest));
    return { pos: dest, ok: true, state: placed ? this.stateString(placed.stateId) : null, reason: null };
  }

  /** A slot click in the open window (or the player inventory, windowId 0). */
  async clickWindow(windowId: number, slot: number, button: number, mode: number): Promise<string | null> {
    const bot = this.bot;
    if (!bot || this.stateValue.state !== 'spawned') return 'not_spawned';
    const window = bot.currentWindow ?? bot.inventory;
    if (window.id !== windowId) return 'stale_window';
    try {
      await bot.clickWindow(slot, button, mode as 0);
      return null;
    } catch (e) {
      return (e as Error).message;
    }
  }

  closeWindow(windowId: number): void {
    const bot = this.bot;
    if (!bot || this.stateValue.state !== 'spawned') return;
    if (bot.currentWindow && bot.currentWindow.id === windowId) bot.closeWindow(bot.currentWindow);
    else if (windowId === 0) bot.closeWindow(bot.inventory); // returns the 2x2 grid and cursor items
  }

  windowSnapshot(): WindowEvent | null {
    const w = this.bot?.currentWindow;
    return w ? windowEvent('open', w) : null;
  }

  selectSlot(hotbar: number): void {
    if (this.bot && this.stateValue.state === 'spawned') this.bot.setQuickBarSlot(hotbar);
  }

  inventorySnapshot(): InventoryPayload | null {
    const bot = this.bot;
    if (!bot?.inventory || this.stateValue.state !== 'spawned') return null;
    const slots = Array.from({ length: 46 }, (_, i) => {
      const it = bot.inventory.slots[i];
      return it ? { slot: i, item: `minecraft:${it.name}`, count: it.count, nbt: null } : null;
    });
    const c = slotOf(-1, (bot.inventory as unknown as AnyWindow).selectedItem);
    return { heldSlot: bot.quickBarSlot, slots, cursor: c ? { ...c, nbt: null } : null };
  }

  hudSnapshot(): HudStatePayload | null {
    const bot = this.bot;
    if (!bot?.entity || this.stateValue.state !== 'spawned') return null;
    const oxygen = (bot as unknown as { oxygenLevel?: number }).oxygenLevel ?? 20;
    return {
      health: bot.health ?? 20,
      food: bot.food ?? 20,
      saturation: bot.foodSaturation ?? 5,
      xpLevel: bot.experience?.level ?? 0,
      xpProgress: bot.experience?.progress ?? 0,
      air: Math.round(oxygen * 15),
      armor: 0,
      gameMode: bot.game?.gameMode ?? 'survival',
      dead: (bot.health ?? 20) <= 0,
      effects: [],
    };
  }

  private stateString(id: number): string {
    return this.world()?.stateString(id) ?? 'minecraft:air';
  }

  private hookBuildingEvents(bot: Bot): void {
    let invTimer: NodeJS.Timeout | null = null;
    const pushInventory = () => {
      if (invTimer) return;
      invTimer = setTimeout(() => {
        invTimer = null;
        const inv = this.inventorySnapshot();
        if (inv) this.emit('inventory', inv);
      }, 50);
    };
    let lastHud = 0;
    let hudTimer: NodeJS.Timeout | null = null;
    const pushHud = () => {
      const send = () => {
        hudTimer = null;
        lastHud = Date.now();
        const hud = this.hudSnapshot();
        if (hud) this.emit('hud', hud);
      };
      if (hudTimer) return;
      const wait = Math.max(0, 100 - (Date.now() - lastHud)); // cap ~10 Hz (protocol §5)
      hudTimer = setTimeout(send, wait);
    };
    bot.once('spawn', () => {
      bot.inventory.on('updateSlot', pushInventory);
      pushInventory();
      pushHud();
      void this.giveStarterKit();
    });
    bot.on('heldItemChanged', pushInventory);
    bot.on('windowOpen', (w) => {
      this.log.info(`minecraft: window opened ${w.type} "${String(w.title)}" (id ${w.id})`);
      this.emit('window', windowEvent('open', w));
      let t: NodeJS.Timeout | null = null;
      (w as unknown as NodeJS.EventEmitter).on('updateSlot', () => {
        if (t) return;
        t = setTimeout(() => {
          t = null;
          if (bot.currentWindow === w) this.emit('window', windowEvent('update', w));
        }, 50);
      });
    });
    bot.on('windowClose', (w) => {
      this.emit('window', { kind: 'close', windowId: w.id });
      pushInventory();
    });
    bot.on('health', pushHud);
    bot.on('breath', pushHud);
    bot.on('experience', pushHud);
    bot.on('game', pushHud);
    bot.on('respawn', pushHud);
  }

  /** One-time kit so survival building can start (tracked by a scoreboard tag on Steve). */
  private async giveStarterKit(): Promise<void> {
    const rcon = this.opts.rcon;
    if (!rcon) return;
    try {
      const tags = await rcon.command(`tag ${this.username} list`);
      if (tags.includes(KIT_TAG)) return;
      for (const [item, n] of STARTER_KIT) await rcon.command(`give ${this.username} minecraft:${item} ${n}`);
      await rcon.command(`tag ${this.username} add ${KIT_TAG}`);
      this.log.info('minecraft: gave Steve the starter kit');
    } catch (e) {
      this.log.warn(`starter kit failed: ${(e as Error).message}`);
    }
  }

  private setState(state: McState, reason: string | null = null): void {
    if (this.stateValue.state === state && this.stateValue.reason === reason) return;
    this.stateValue = { state, reason };
    this.log.info(`minecraft: ${state}${reason ? ` (${reason})` : ''}`);
    this.emit('status', this.stateValue);
  }

  private connect(): void {
    const host = this.opts.host ?? '127.0.0.1';
    const port = this.opts.port ?? 25565;
    this.setState('connecting');
    const bot = mineflayer.createBot({
      host,
      port,
      username: this.username,
      auth: 'offline',
      version: MC_VERSION,
      physicsEnabled: false,
      hideErrors: true,
      checkTimeoutInterval: 30_000,
    });
    this.bot = bot;
    this.stateNames.clear();
    this.hookBuildingEvents(bot);

    bot.once('spawn', () => {
      this.attempt = 0;
      bot.physicsEnabled = false;
      this.log.info(`minecraft: Steve spawned at ${bot.entity.position} (${bot.game.gameMode}, ${bot.game.dimension})`);
      this.setState('spawned');
    });
    // bot.world only exists after login; Mineflayer re-emits these world events on the bot itself.
    bot.on('chunkColumnLoad', (p: Vec3) => {
      this.emit('columnLoad', Math.floor(p.x / 16), Math.floor(p.z / 16));
    });
    bot.on('chunkColumnUnload', (p: Vec3) => {
      this.emit('columnUnload', Math.floor(p.x / 16), Math.floor(p.z / 16));
    });
    bot.on('blockUpdate', (_old, now) => {
      if (now) this.emit('blockUpdate', now.position.x, now.position.y, now.position.z, now.stateId);
    });
    bot.on('death', () => {
      this.log.warn('minecraft: Steve died, respawning');
      bot.respawn();
    });
    bot.on('kicked', (reason) => this.setState('kicked', typeof reason === 'string' ? reason : JSON.stringify(reason)));
    bot.on('error', (err) => this.log.warn(`minecraft: ${err.message}`));
    bot.on('end', (reason) => {
      if (this.bot !== bot) return;
      this.bot = null;
      if (this.stateValue.state !== 'kicked') this.setState('disconnected', reason ?? null);
      this.scheduleReconnect();
    });
  }

  private scheduleReconnect(): void {
    if (this.stopped) return;
    const delay = BACKOFF_S[Math.min(this.attempt, BACKOFF_S.length - 1)]!;
    this.attempt++;
    this.log.info(`minecraft: reconnecting in ${delay}s`);
    this.reconnectTimer = setTimeout(() => this.connect(), delay * 1000);
  }
}

type BlockClass = ReturnType<typeof prismarineBlock>;

export const FACE_VECTORS: Record<Face, Vec3Tuple> = {
  down: [0, -1, 0],
  up: [0, 1, 0],
  north: [0, 0, -1],
  south: [0, 0, 1],
  west: [-1, 0, 0],
  east: [1, 0, 0],
};

const INTERACTABLE = /^(crafting_table|furnace|blast_furnace|smoker|chest|trapped_chest|barrel|ender_chest|.*shulker_box|anvil|chipped_anvil|damaged_anvil|smithing_table|stonecutter|loom|cartography_table|grindstone|enchanting_table|brewing_stand|hopper|dispenser|dropper)$/;

type AnyWindow = { id: number; type: string | number; title?: unknown; slots: Array<{ name: string; count: number } | null>; selectedItem?: { name: string; count: number } | null };

function slotOf(i: number, it: { name: string; count: number } | null | undefined) {
  return it ? { slot: i, item: `minecraft:${it.name}`, count: it.count } : null;
}

/** Window titles arrive as JSON text or (1.20.3+) NBT-style chat components; dig out text/translate. */
export function titleText(t: unknown): string {
  if (t == null) return '';
  if (typeof t === 'string') {
    try {
      return titleText(JSON.parse(t));
    } catch {
      return t;
    }
  }
  if (typeof t !== 'object') return String(t);
  const o = t as Record<string, unknown>;
  // NBT wrappers: { type: 'compound', value: { translate: { type: 'string', value: '...' } } }
  if ('value' in o && o.type !== undefined) return titleText(o.value);
  for (const key of ['text', 'translate']) {
    const v = o[key];
    if (typeof v === 'string' && v) return v;
    if (v && typeof v === 'object') {
      const inner = titleText(v);
      if (inner) return inner;
    }
  }
  if (typeof (o as { toString?: () => string }).toString === 'function') {
    const s = String(o);
    if (s !== '[object Object]') return s;
  }
  return '';
}

function windowEvent(kind: 'open' | 'update', w: unknown): WindowEvent {
  const win = w as AnyWindow;
  return {
    kind,
    windowId: win.id,
    type: String(win.type),
    title: titleText(win.title),
    slots: win.slots.map((it, i) => slotOf(i, it)),
    cursor: slotOf(-1, win.selectedItem),
  };
}

const KIT_TAG = 'stevefish_kit';
const STARTER_KIT: Array<[string, number]> = [
  ['stone_pickaxe', 1],
  ['stone_axe', 1],
  ['stone_shovel', 1],
  ['cobblestone', 64],
  ['dirt', 64],
  ['oak_planks', 32],
  ['oak_log', 16],
  ['crafting_table', 1],
  ['torch', 16],
  ['glass', 16],
];

type PlaceWithOptions = (ref: unknown, face: Vec3, opts: { forceLook?: boolean | 'ignore'; swingArm?: 'right' | 'left'; delta?: Vec3 }) => Promise<void>;

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

/** "minecraft:name[prop=value,...]" in the block's own property order. */
export function describeState(Block: BlockClass, bot: Bot, stateId: number): string {
  const def = bot.registry.blocksByStateId[stateId];
  if (!def) return 'minecraft:air';
  const states = (def as unknown as { states?: Array<{ name: string }> }).states ?? [];
  if (states.length === 0) return `minecraft:${def.name}`;
  const props = Block.fromStateId(stateId, 0).getProperties() as Record<string, unknown>;
  const parts = states.map((s) => `${s.name}=${String(props[s.name])}`);
  return `minecraft:${def.name}[${parts.join(',')}]`;
}
