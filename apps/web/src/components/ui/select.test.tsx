import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Select } from "./select";

const options = [
  { value: "merkez", label: "Merkez depo" },
  { value: "sube", label: "Şube depo" },
  { value: "kapali", label: "Kapalı depo", disabled: true },
];

describe("Select", () => {
  it("is a labelled native combobox with a placeholder option", async () => {
    const onChange = vi.fn();
    render(<Select label="Depo" placeholder="Seçiniz" options={options} onChange={onChange} defaultValue="" />);
    const select = screen.getByRole("combobox", { name: "Depo" }) as HTMLSelectElement;
    expect(screen.getAllByRole("option").map((o) => o.textContent)).toEqual([
      "Seçiniz",
      "Merkez depo",
      "Şube depo",
      "Kapalı depo",
    ]);
    expect((screen.getByRole("option", { name: "Kapalı depo" }) as HTMLOptionElement).disabled).toBe(true);

    await userEvent.selectOptions(select, "sube");
    expect(select.value).toBe("sube");
    expect(onChange).toHaveBeenCalledTimes(1);
  });

  it("announces errors", () => {
    render(<Select label="Depo" options={options} error="Bir depo seçin." />);
    const select = screen.getByRole("combobox", { name: "Depo" });
    expect(select.getAttribute("aria-invalid")).toBe("true");
    expect(document.getElementById(select.getAttribute("aria-describedby")!)?.textContent).toBe("Bir depo seçin.");
  });
});
