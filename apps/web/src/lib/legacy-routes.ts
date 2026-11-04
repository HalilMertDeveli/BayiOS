/**
 * Old URLs that moved. Returns the new path or null.
 * /urunler/duzenle?id=<id> → /urunler/<id> (#87: the product page became a dynamic segment).
 */
export function legacyRedirect(pathname: string, searchParams: URLSearchParams): string | null {
  if (pathname === "/urunler/duzenle" || pathname === "/urunler/duzenle/") {
    const id = searchParams.get("id")?.trim();
    return id ? `/urunler/${encodeURIComponent(id)}` : "/urunler";
  }
  return null;
}
