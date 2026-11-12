import type { ComponentProps, ReactNode } from "react";
import { LoaderCircle } from "@/components/ui/icons";
import { cn } from "@/lib/cn";

/** UI kit buttons: primary (green, raised), secondary (outlined), tertiary (grey fill), ghost, danger. */
export type ButtonVariant = "primary" | "secondary" | "tertiary" | "ghost" | "danger";
export type ButtonSize = "sm" | "md" | "lg";

const variantClasses: Record<ButtonVariant, string> = {
  primary: "bg-linear-to-b from-accent to-brand text-on-brand shadow-button hover:from-brand hover:to-brand-hover",
  secondary: "border border-line bg-card text-fg shadow-e1 hover:bg-sunken",
  tertiary: "bg-sunken text-fg hover:bg-neutral-bg",
  ghost: "text-fg hover:bg-sunken",
  danger: "bg-danger-solid text-white shadow-e1 hover:bg-danger-solid-hover",
};

// size.control.*: sm 32 px, md 40 px (web default, kit), lg 44 px.
const sizeClasses: Record<ButtonSize, string> = {
  sm: "h-8 gap-1.5 px-3 text-body",
  md: "h-10 gap-2 px-4 text-body",
  lg: "h-11 gap-2 px-5 text-body-lg",
};

export const buttonClasses = (variant: ButtonVariant = "primary", size: ButtonSize = "md") =>
  cn(
    "inline-flex shrink-0 items-center justify-center rounded-md font-medium whitespace-nowrap transition-colors",
    "disabled:cursor-not-allowed disabled:opacity-50 disabled:shadow-none aria-busy:cursor-progress",
    variantClasses[variant],
    sizeClasses[size],
  );

export type ButtonProps = ComponentProps<"button"> & {
  variant?: ButtonVariant;
  size?: ButtonSize;
  /** Shows a spinner, disables the button and sets aria-busy. The label stays visible. */
  loading?: boolean;
  /** Decorative icon rendered before the label. */
  icon?: ReactNode;
};

export function Button({
  variant = "primary",
  size = "md",
  loading = false,
  icon,
  disabled,
  type = "button",
  className,
  children,
  ...rest
}: ButtonProps) {
  return (
    <button
      type={type}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      className={cn(buttonClasses(variant, size), className)}
      {...rest}
    >
      {loading ? <LoaderCircle className="size-4 animate-spin" /> : icon}
      {children}
    </button>
  );
}
