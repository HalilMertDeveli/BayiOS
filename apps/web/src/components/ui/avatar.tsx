import { cn } from "@/lib/cn";
import { initials } from "@/lib/initials";

export type AvatarSize = "sm" | "md" | "lg";

const sizeClasses: Record<AvatarSize, string> = {
  sm: "size-8 text-caption",
  md: "size-10 text-body",
  lg: "size-12 text-body-lg",
};

/** Deterministic tone per name, so the same person keeps the same colour across lists. */
const TONES = [
  "bg-brand-bg text-brand-fg",
  "bg-info-bg text-info-fg",
  "bg-purple-bg text-purple-fg",
  "bg-warning-bg text-warning-fg",
  "bg-success-bg text-success-fg",
] as const;

function toneFor(name: string): string {
  let hash = 0;
  for (const ch of name) hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
  return TONES[hash % TONES.length];
}

export type AvatarProps = {
  /** Person or company name; drives the initials and the tone. */
  name: string;
  size?: AvatarSize;
  /**
   * Photo URL. Only for demo/mock data (the UI kit sample avatars); real users always get initials (#222) — the API has
   * no profile photos.
   */
  src?: string;
  className?: string;
};

/** Round avatar: initials on a tone background (kit style), or a demo photo. Decorative — the name is shown next to it. */
export function Avatar({ name, size = "md", src, className }: AvatarProps) {
  const classes = cn("inline-flex shrink-0 items-center justify-center overflow-hidden rounded-full font-semibold ring-2 ring-card", sizeClasses[size], className);
  if (src) {
    // eslint-disable-next-line @next/next/no-img-element -- static demo asset, no optimisation needed
    return <img src={src} alt="" aria-hidden="true" className={cn(classes, "object-cover")} />;
  }
  return (
    <span aria-hidden="true" className={cn(classes, toneFor(name))}>
      {initials(name)}
    </span>
  );
}
