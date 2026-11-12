import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { AboutSection } from "./about-section";

describe("AboutSection", () => {
  it("shows the UI kit credit line required by CC BY 4.0 (#222)", () => {
    render(<AboutSection />);
    expect(screen.getByRole("heading", { name: "Hakkında" })).toBeTruthy();
    expect(
      screen.getByText(
        /UI tasarımı: Lukáš Marek, 'AI Inventory Management Web App UI Kit' \(Figma Community\), CC BY 4\.0\. Değiştirilerek kullanılmıştır\./,
      ),
    ).toBeTruthy();
    expect(screen.getByRole("link", { name: "Lisans" }).getAttribute("href")).toContain("creativecommons.org/licenses/by/4.0");
  });
});
