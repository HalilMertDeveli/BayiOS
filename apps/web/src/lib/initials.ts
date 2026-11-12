/**
 * Up to two initials for the avatar ("Ayşe Yılmaz" → "AY"; an e-mail → its first letter). Words that do not start with
 * a letter or digit ("(sentetik)", "&") are skipped, so "Örnek Müdür (sentetik)" → "ÖM".
 */
export function initials(name: string): string {
  const parts = name.split(/[\s@._-]+/).filter(Boolean);
  const named = parts.filter((w) => /^[\p{L}\p{N}]/u.test(w));
  const words = named.length > 0 ? named : parts;
  const letters = (words.length > 1 && !name.includes("@") ? [words[0], words[words.length - 1]] : [words[0] ?? "?"]).map((w) => w[0]);
  return letters.join("").toLocaleUpperCase("tr");
}
