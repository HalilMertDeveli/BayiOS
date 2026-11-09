import { describe, expect, it } from "vitest";
import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Activity, useState } from "react";
import { useResetAfterCreate } from "./use-reset-after-create";

function NewRecordForm({ onCreated }: { onCreated: () => void }) {
  const [name, setName] = useState("");
  return (
    <form
      aria-label="Yeni kayıt"
      onSubmit={(e) => {
        e.preventDefault();
        onCreated();
      }}
    >
      <input aria-label="Ad" value={name} onChange={(e) => setName(e.target.value)} />
      <button type="submit">Oluştur</button>
    </form>
  );
}

/** A "yeni" page as Next keeps it: hidden in <Activity> after navigating away, shown again on return. */
function NewPage() {
  const { generation, markCreated } = useResetAfterCreate();
  return <NewRecordForm key={generation} onCreated={markCreated} />;
}

function renderInActivity() {
  const view = render(
    <Activity mode="visible">
      <NewPage />
    </Activity>,
  );
  const setMode = (mode: "visible" | "hidden") =>
    act(() =>
      view.rerender(
        <Activity mode={mode}>
          <NewPage />
        </Activity>,
      ),
    );
  return setMode;
}

describe("useResetAfterCreate (#96)", () => {
  it("after a successful create, returning to the page shows an empty form", async () => {
    const setMode = renderInActivity();
    await userEvent.type(screen.getByLabelText("Ad"), "Panel 18W");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));
    // Still visible right after the create (the navigation hides the page): nothing flashes empty.
    expect(screen.getByLabelText("Ad")).toHaveProperty("value", "Panel 18W");
    setMode("hidden");
    setMode("visible");
    expect(screen.getByLabelText("Ad")).toHaveProperty("value", "");
  });

  it("unsaved input is kept when the user leaves without creating", async () => {
    const setMode = renderInActivity();
    await userEvent.type(screen.getByLabelText("Ad"), "Yarım kalan");
    setMode("hidden");
    setMode("visible");
    expect(screen.getByLabelText("Ad")).toHaveProperty("value", "Yarım kalan");
  });
});
