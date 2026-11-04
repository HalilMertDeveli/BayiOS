/**
 * One Idempotency-Key per create attempt: a retry of the same request body (after a timeout, a network error or
 * a double click) reuses the key, so the API returns the first result instead of creating a duplicate. A changed
 * body is a new attempt and gets a new key (reusing a key with a different body is rejected by the API).
 */
export class IdempotencyKeys {
  private last: { body: string; key: string } | null = null;

  constructor(private readonly generate: () => string = () => crypto.randomUUID()) {}

  keyFor(body: unknown): string {
    const serialized = JSON.stringify(body);
    if (this.last?.body === serialized) return this.last.key;
    this.last = { body: serialized, key: this.generate() };
    return this.last.key;
  }

  /** Call after a successful create so the next record starts a fresh attempt. */
  reset(): void {
    this.last = null;
  }
}
