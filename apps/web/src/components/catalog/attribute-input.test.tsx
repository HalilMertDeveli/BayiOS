import { useState } from "react";
import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { CustomField } from "@/lib/catalog/api";
import { emptyFormValue, toApiValue, type AttributeFormValue } from "@/lib/catalog/attributes";
import { field } from "@/lib/catalog/test-fixtures";
import { AttributeInput } from "./attribute-input";

/** Renders one input and exposes the API value it would send. */
function Harness({ def, error }: { def: CustomField; error?: string }) {
  const [value, setValue] = useState<AttributeFormValue>(emptyFormValue(def));
  const result = toApiValue(def, value);
  return (
    <>
      <AttributeInput field={def} value={value} onChange={setValue} errorKey={`attributes.${def.key}`} error={error} />
      <output data-testid="api">{JSON.stringify(result)}</output>
    </>
  );
}

const api = () => JSON.parse(screen.getByTestId("api").textContent ?? "{}");

describe("AttributeInput", () => {
  it("number with unit: labelled input with the unit and a decimal value", async () => {
    render(<Harness def={field({ key: "guc", label: "Güç", dataType: "number", unit: "W", required: true })} />);
    const input = screen.getByLabelText(/Güç/);
    expect(input).toHaveProperty("required", true);
    expect(screen.getByText("W")).toBeTruthy();
    await userEvent.type(input, "7,5");
    expect(api()).toEqual({ kind: "value", value: 7.5 });
  });

  it("boolean: a select where 'Hayır' is a value and empty is unset", async () => {
    render(<Harness def={field({ key: "ip65", label: "IP65", dataType: "boolean" })} />);
    expect(api()).toEqual({ kind: "empty" });
    await userEvent.selectOptions(screen.getByLabelText("IP65"), "Hayır");
    expect(api()).toEqual({ kind: "value", value: false });
  });

  it("multi_enum: a labelled checkbox group", async () => {
    render(<Harness def={field({ key: "model", label: "Uyumlu model", dataType: "multi_enum", options: ["CG125", "CB150"] })} />);
    expect(screen.getByRole("group", { name: "Uyumlu model" })).toBeTruthy();
    await userEvent.click(screen.getByLabelText("CB150"));
    await userEvent.click(screen.getByLabelText("CG125"));
    expect(api()).toEqual({ kind: "value", value: ["CG125", "CB150"] });
  });

  it("range: two labelled bounds", async () => {
    render(<Harness def={field({ key: "k", label: "Renk sıcaklığı", dataType: "range", unit: "K" })} />);
    await userEvent.type(screen.getByLabelText("Renk sıcaklığı en az"), "2700");
    await userEvent.type(screen.getByLabelText("Renk sıcaklığı en çok"), "6500");
    expect(api()).toEqual({ kind: "value", value: { min: 2700, max: 6500 } });
  });

  it("enum and date inputs", async () => {
    render(<Harness def={field({ key: "renk", label: "Renk", dataType: "enum", options: ["Sarı", "Beyaz"] })} />);
    await userEvent.selectOptions(screen.getByLabelText("Renk"), "Beyaz");
    expect(api()).toEqual({ kind: "value", value: "Beyaz" });
  });

  it("shows the error on the input with a stable id", () => {
    render(<Harness def={field({ key: "guc", label: "Güç", dataType: "integer" })} error="Tam sayı girin (ör. 4)." />);
    const input = screen.getByLabelText("Güç");
    expect(input.id).toBe("f-attributes-guc");
    expect(input.getAttribute("aria-invalid")).toBe("true");
    expect(input.getAttribute("aria-describedby")).toContain("f-attributes-guc-error");
  });
});
