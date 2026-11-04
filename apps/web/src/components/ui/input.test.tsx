import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Input } from "./input";

describe("Input", () => {
  it("is labelled by its visible label and accepts typing (Turkish characters)", async () => {
    render(<Input label="Müşteri adı" />);
    const input = screen.getByLabelText("Müşteri adı");
    await userEvent.type(input, "Şükrü Çağlıyan İnşaat");
    expect((input as HTMLInputElement).value).toBe("Şükrü Çağlıyan İnşaat");
    expect(input.getAttribute("aria-invalid")).toBeNull();
    expect(input.getAttribute("aria-describedby")).toBeNull();
  });

  it("links hint and error text and flags the field invalid", () => {
    render(<Input label="Miktar" hint="Adet olarak girin" error="Miktar sıfırdan büyük olmalı." />);
    const input = screen.getByRole("textbox", { name: "Miktar" });
    expect(input.getAttribute("aria-invalid")).toBe("true");
    const describedBy = input.getAttribute("aria-describedby")!.split(" ");
    const texts = describedBy.map((id) => document.getElementById(id)?.textContent);
    expect(texts).toEqual(["Miktar sıfırdan büyük olmalı.", "Adet olarak girin"]);
  });

  it("keeps a hidden label accessible and marks required fields", () => {
    render(<Input label="Ara" hideLabel required />);
    const input = screen.getByRole("textbox", { name: /Ara/ });
    expect((input as HTMLInputElement).required).toBe(true);
    expect(screen.getByText("Ara").className).toContain("sr-only");
  });

  it("uses a provided id", () => {
    render(<Input label="SKU" id="sku" />);
    expect(screen.getByLabelText("SKU").id).toBe("sku");
  });
});
