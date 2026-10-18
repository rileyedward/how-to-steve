// Angle conventions. The wire uses Minecraft ("Notchian") degrees: yaw 0 = +Z (south), 90 = -X (west);
// pitch -90 = up, +90 = down (protocol.md §3). Mineflayer stores radians with its own convention
// (mineflayer/lib/conversions.js: notchianYaw = deg(PI - yaw), notchianPitch = deg(-pitch)).

const DEG = Math.PI / 180;

export function wrapDegrees(deg: number): number {
  const d = ((deg % 360) + 360) % 360;
  return d >= 180 ? d - 360 : d;
}

export function notchianToMineflayerYaw(yawDeg: number): number {
  const r = Math.PI - yawDeg * DEG;
  return ((r % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
}

export function notchianToMineflayerPitch(pitchDeg: number): number {
  return -pitchDeg * DEG;
}

export function chunkOf(x: number): number {
  return Math.floor(x / 16);
}

export function sectionKey(cx: number, sy: number, cz: number): string {
  return `${cx},${sy},${cz}`;
}
