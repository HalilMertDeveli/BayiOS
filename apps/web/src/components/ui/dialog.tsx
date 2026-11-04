"use client";

import { useEffect, useId, useRef, type ReactNode, type RefObject } from "react";
import { X } from "lucide-react";
import { cn } from "@/lib/cn";

/**
 * Drives a native <dialog> as a controlled modal. showModal() gives us focus trapping,
 * an inert background, Escape handling and top-layer stacking from the browser.
 * Focus returns to the element that was focused before opening.
 */
export function useModalDialog(ref: RefObject<HTMLDialogElement | null>, open: boolean, onClose: () => void) {
  const onCloseRef = useRef(onClose);
  useEffect(() => {
    onCloseRef.current = onClose;
  });

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;

    if (open && !dialog.open) {
      const previouslyFocused = document.activeElement as HTMLElement | null;
      dialog.showModal();
      return () => {
        if (dialog.open) dialog.close();
        if (previouslyFocused?.isConnected) previouslyFocused.focus();
      };
    }
  }, [ref, open]);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    // Escape fires "cancel": keep the parent in control of the open state.
    const onCancel = (event: Event) => {
      event.preventDefault();
      onCloseRef.current();
    };
    dialog.addEventListener("cancel", onCancel);
    return () => dialog.removeEventListener("cancel", onCancel);
  }, [ref]);
}

export type DialogProps = {
  open: boolean;
  onClose: () => void;
  title: string;
  description?: ReactNode;
  children?: ReactNode;
  /** Action buttons, rendered right-aligned. Put the primary action last. */
  footer?: ReactNode;
  size?: "sm" | "md" | "lg";
  /** Close when clicking the backdrop. Disable for destructive confirmations. */
  dismissOnBackdrop?: boolean;
  /** Use "alertdialog" for confirmations that interrupt the user (e.g. irreversible actions). */
  role?: "dialog" | "alertdialog";
};

const sizeClasses = { sm: "max-w-sm", md: "max-w-lg", lg: "max-w-2xl" } as const;

export function Dialog({
  open,
  onClose,
  title,
  description,
  children,
  footer,
  size = "md",
  dismissOnBackdrop = true,
  role = "dialog",
}: DialogProps) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const descriptionId = useId();
  useModalDialog(ref, open, onClose);

  return (
    <dialog
      ref={ref}
      role={role === "alertdialog" ? "alertdialog" : undefined}
      aria-modal="true"
      aria-labelledby={titleId}
      aria-describedby={description ? descriptionId : undefined}
      onClick={(event) => {
        if (dismissOnBackdrop && event.target === event.currentTarget) onClose();
      }}
      className={cn(
        "m-auto w-[calc(100%-2rem)] rounded-xl border border-line bg-raised p-0 text-fg shadow-e3",
        sizeClasses[size],
      )}
    >
      {open ? (
        <div className="flex flex-col gap-4 p-6">
          <div className="flex items-start justify-between gap-4">
            <div className="flex flex-col gap-1">
              <h2 id={titleId} className="text-h2 text-fg">
                {title}
              </h2>
              {description ? (
                <div id={descriptionId} className="text-body text-fg-muted">
                  {description}
                </div>
              ) : null}
            </div>
            <button
              type="button"
              onClick={onClose}
              aria-label="Kapat"
              className="-mt-1 -mr-2 inline-flex size-9 shrink-0 items-center justify-center rounded-md text-fg-muted hover:bg-sunken hover:text-fg"
            >
              <X className="size-5" />
            </button>
          </div>
          {children ? <div className="text-body text-fg-secondary">{children}</div> : null}
          {footer ? <div className="flex flex-wrap justify-end gap-2 pt-2">{footer}</div> : null}
        </div>
      ) : null}
    </dialog>
  );
}
