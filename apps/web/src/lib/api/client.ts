import type { components } from "./schema";

export type Meta = components["schemas"]["MetaResponse"];

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

export function apiBaseUrl(): string {
  const url = process.env.NEXT_PUBLIC_API_BASE_URL;
  if (!url) {
    throw new Error("NEXT_PUBLIC_API_BASE_URL is not configured");
  }
  return url.replace(/\/+$/, "");
}

export async function getMeta(signal?: AbortSignal): Promise<Meta> {
  const response = await fetch(`${apiBaseUrl()}/api/v1/meta`, {
    headers: { Accept: "application/json" },
    signal,
  });
  if (!response.ok) {
    throw new ApiError(`GET /api/v1/meta failed with ${response.status}`, response.status);
  }
  return (await response.json()) as Meta;
}
