/** Joins class names, dropping falsy values. Intentionally tiny — no class-merging magic. */
export function cn(...classes: Array<string | false | null | undefined>): string {
  return classes.filter(Boolean).join(" ");
}
