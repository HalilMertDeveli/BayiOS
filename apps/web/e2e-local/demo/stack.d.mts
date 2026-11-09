// Types for stack.mjs (see seeder.d.mts).
export interface StackConfig {
  apiUrl: string;
  supabaseUrl: string;
  mailpitUrl: string;
  readonly publishableKey: string;
}

export const REPO_ROOT: string;
export function stackConfig(env?: Record<string, string | undefined>): StackConfig;
export function ensureUser(cfg: StackConfig, user: { email: string; password: string; fullName: string }): Promise<string>;
export const etagOf: (version: number | string) => string;
export function sleep(ms: number): Promise<void>;

export class ApiError extends Error {
  status: number;
  body: unknown;
}

export interface RawResult<T> {
  status: number;
  body: T;
  etag: string | null;
  headers: Headers;
}

export interface RequestOptions {
  body?: unknown;
  ifMatch?: string;
  contentType?: string;
  form?: FormData;
  expect?: number[];
}

export class Api {
  constructor(cfg: StackConfig, token: string, tenantId?: string | null);
  readonly cfg: StackConfig;
  readonly token: string;
  readonly tenantId: string | null;
  withTenant(tenantId: string): Api;
  readonly session: { authorization: string; tenantId: string };
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  raw<T = any>(method: string, path: string, options?: RequestOptions): Promise<RawResult<T>>;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  get<T = any>(path: string): Promise<T>;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  post<T = any>(path: string, body?: unknown, options?: RequestOptions): Promise<T>;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  put<T = any>(path: string, body?: unknown, options?: RequestOptions): Promise<T>;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  patch<T = any>(path: string, body: unknown, getPath?: string): Promise<T>;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  all<T = any>(path: string, limit?: number): Promise<T[]>;
}
