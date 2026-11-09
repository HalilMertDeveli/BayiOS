import { describe, expect, it } from "vitest";
import { addressChoice, addressLabel, addressLines } from "./addresses";
import { customerAddress as address } from "./test-fixtures";

const book = [
  address("s1", "shipping", true, "Merkez"),
  address("s2", "shipping", false, "Depo"),
  address("b1", "billing", true, "Muhasebe"),
];

describe("addressChoice", () => {
  it("lists the type's addresses with the default first as '' (sent as null = default)", () => {
    const choice = addressChoice(book, "shipping", "");
    expect(choice.options).toEqual([
      { value: "", label: "Merkez — Merkez Cd. 1, Kadıköy / İstanbul (varsayılan)" },
      { value: "s2", label: "Depo — Depo Cd. 1, Kadıköy / İstanbul" },
    ]);
    expect(choice.defaultAddress?.id).toBe("s1");
    expect(choice.missing).toBe(false);
    expect(choice.empty).toBe(false);
  });

  it("keeps a pinned default and flags a chosen address that no longer exists", () => {
    expect(addressChoice(book, "shipping", "s1").options.at(-1)).toEqual({ value: "s1", label: "Merkez — Merkez Cd. 1, Kadıköy / İstanbul (bu adres sabit)" });
    const gone = addressChoice(book, "billing", "deleted");
    expect(gone.missing).toBe(true);
    expect(gone.options.at(-1)).toEqual({ value: "deleted", label: "Seçili adres artık yok" });
  });

  it("a customer without an address of the type", () => {
    const choice = addressChoice([book[2]], "shipping", "");
    expect(choice.empty).toBe(true);
    expect(choice.options).toEqual([{ value: "", label: "Varsayılan adres yok" }]);
  });
});

describe("address text", () => {
  it("one line for pickers, several for the order snapshot", () => {
    expect(addressLabel({ title: null, line1: "Atatürk Cd. 5", district: null, city: "Ankara" })).toBe("Atatürk Cd. 5, Ankara");
    expect(
      addressLines({
        addressId: "s1",
        title: "Merkez",
        line1: "Atatürk Cd. 5",
        line2: "Kat 2",
        district: "Çankaya",
        city: "Ankara",
        postalCode: "06000",
        country: "TR",
        contactName: "Ali",
        contactPhone: "0555 000 00 00",
      }),
    ).toEqual(["Merkez", "Atatürk Cd. 5", "Kat 2", "06000 Çankaya Ankara, TR", "Ali · 0555 000 00 00"]);
  });
});
