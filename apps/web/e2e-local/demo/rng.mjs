/**
 * Deterministic pseudo-random numbers for the synthetic demo data (issues #56/#57): the same seed always produces the
 * same catalog, prices, stock and orders, so two runs (and CI) see identical businesses. mulberry32 — tiny, fast, and
 * good enough for test data; never use it for anything security related (passwords come from node:crypto).
 */

/** @param {number} seed 32-bit integer seed */
export function createRng(seed) {
  let state = seed >>> 0;
  const next = () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
  return {
    /** [0, 1) */
    next,
    /** Integer in [min, max] (inclusive). */
    int: (min, max) => min + Math.floor(next() * (max - min + 1)),
    /** One element of a non-empty array. */
    pick: (items) => items[Math.floor(next() * items.length)],
    /** Price in [min, max] rounded to 0.50 steps (looks like a list price, stays exact in decimal). */
    price: (min, max) => Math.round((min + next() * (max - min)) * 2) / 2,
  };
}

/** FNV-1a hash of a string → 32-bit seed (so each demo has its own stable seed derived from its code). */
export function seedOf(text) {
  let hash = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash >>> 0;
}

/**
 * EAN-13 in the GS1 "restricted circulation" range 200–299 (in-store numbers, never assigned to real products), so
 * the demo barcodes are valid for scanners yet can never collide with a real item.
 * @param {number} prefix 200–299
 * @param {number} serial up to 9 digits
 */
export function syntheticEan13(prefix, serial) {
  const body = `${prefix}${String(serial).padStart(9, "0")}`;
  let sum = 0;
  for (let i = 0; i < 12; i++) sum += Number(body[i]) * (i % 2 === 0 ? 1 : 3);
  return `${body}${(10 - (sum % 10)) % 10}`;
}
