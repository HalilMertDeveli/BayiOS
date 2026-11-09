import { describe, expect, it } from "vitest";
import { buildCustomFieldCreate, buildCustomFieldPatch, customFieldToValues, keyFromLabel, newCustomFieldValues } from "./custom-field-form";
import { IdempotencyKeys } from "./idempotency";
import { field } from "./test-fixtures";

describe("keyFromLabel", () => {
  it("transliterates Turkish and snake-cases", () => {
    expect(keyFromLabel("Güç (W)")).toBe("guc_w");
    expect(keyFromLabel("Işık Rengi")).toBe("isik_rengi");
    expect(keyFromLabel("12 Volt")).toBe("volt");
  });
});

describe("buildCustomFieldCreate", () => {
  it("builds an enum definition with one option per line", () => {
    const values = { ...newCustomFieldValues("variant"), key: "renk", label: "Renk", dataType: "enum" as const, options: "Sarı\n Beyaz \n\n" };
    const { body, errors } = buildCustomFieldCreate(values);
    expect(errors).toEqual({});
    expect(body).toMatchObject({ entityType: "variant", key: "renk", dataType: "enum", options: ["Sarı", "Beyaz"], unit: null });
  });

  it("validates key, options and drops a unit left over from another type", () => {
    const values = { ...newCustomFieldValues(), key: "Renk", label: "", dataType: "multi_enum" as const, options: "A\nA", unit: "W" };
    const { body, errors } = buildCustomFieldCreate(values);
    expect(Object.keys(errors).sort()).toEqual(["key", "label", "options"]);
    expect(body.unit).toBeNull();
  });
});

describe("buildCustomFieldPatch", () => {
  it("only sends changed editable members", () => {
    const original = field({ key: "guc", dataType: "number", unit: "W", label: "Güç" });
    const values = customFieldToValues(original);
    values.label = "Güç (watt)";
    values.filterable = true;
    expect(buildCustomFieldPatch(values, original).patch).toEqual({ label: "Güç (watt)", filterable: true });
  });
});

describe("IdempotencyKeys", () => {
  it("reuses the key for a retry of the same body and changes it for a new body", () => {
    let n = 0;
    const keys = new IdempotencyKeys(() => `k${++n}`);
    expect(keys.keyFor({ a: 1 })).toBe("k1");
    expect(keys.keyFor({ a: 1 })).toBe("k1");
    expect(keys.keyFor({ a: 2 })).toBe("k2");
    keys.reset();
    expect(keys.keyFor({ a: 2 })).toBe("k3");
  });
});

