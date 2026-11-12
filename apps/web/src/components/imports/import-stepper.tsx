import { Check } from "@/components/ui/icons";
import { cn } from "@/lib/cn";
import { WIZARD_STEPS, stepLabels, stepState, type WizardStep } from "@/lib/imports/steps";

/**
 * Wizard progress (design-system-plan "Stepper / Wizard"). Desktop: six labelled segments; mobile: numbered segments
 * plus the current step's name as text, so nothing has to fit side by side.
 */
export function ImportStepper({ current }: { current: WizardStep }) {
  const index = WIZARD_STEPS.indexOf(current);
  return (
    <div className="flex flex-col gap-2">
      <p className="text-caption text-fg-muted sm:hidden" aria-hidden="true">
        Adım {index + 1}/{WIZARD_STEPS.length}: <span className="font-semibold text-fg">{stepLabels[current]}</span>
      </p>
      <ol aria-label="İçe aktarma adımları" className="grid grid-cols-6 gap-1.5 sm:gap-2">
        {WIZARD_STEPS.map((step, i) => {
          const state = stepState(step, current);
          return (
            <li
              key={step}
              aria-current={state === "current" ? "step" : undefined}
              className={cn(
                "flex flex-col gap-1 border-t-4 pt-2 text-caption",
                state === "done" ? "border-brand text-fg" : state === "current" ? "border-brand font-semibold text-fg" : "border-line text-fg-muted",
              )}
            >
              <span className="flex items-center gap-1">
                {state === "done" ? <Check className="size-3.5 shrink-0" aria-hidden="true" /> : null}
                <span className="sr-only">
                  Adım {i + 1}
                  {state === "done" ? " (tamamlandı)" : ""}:{" "}
                </span>
                <span className="hidden truncate sm:inline">{stepLabels[step]}</span>
                <span className="sr-only sm:hidden">{stepLabels[step]}</span>
                <span aria-hidden="true" className="sm:hidden">
                  {i + 1}
                </span>
              </span>
            </li>
          );
        })}
      </ol>
    </div>
  );
}
