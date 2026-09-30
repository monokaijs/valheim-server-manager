export type Point = { x: number; y: number; z: number }
export const mapPoint = (svgX: number, svgY: number) => ({ x: svgX, z: -svgY })
export function validDestination(x: number, y: number | null, z: number) {
  return Number.isFinite(x) && Number.isFinite(z) && (y === null || (Number.isFinite(y) && y >= -100 && y <= 500)) && x * x + z * z <= 10000 ** 2
}
