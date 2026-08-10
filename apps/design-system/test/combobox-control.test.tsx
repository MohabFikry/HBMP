import { describe, expect, it } from "vitest";
import { useState } from "react";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderDS } from "./render";
import { Combobox, type ComboboxOption } from "../src/components/Combobox";
import { ComboboxField, SelectField } from "../src/components/Field";
import { Icon } from "../src/components/Icon";

/**
 * The combobox is about to become THE picker in the product — the tables/buttons pass left `SelectField` the
 * most-used control in the SPA precisely because it was the only labelled one, and the scrolls/dropdowns pass
 * is converting all 45 non-searchable pickers onto this component. Everything below is a property that
 * conversion depends on, so a regression here is a regression across ~56 call sites rather than one screen.
 */

const COUNTRIES: ComboboxOption[] = [
  { value: "SY", label: "Syria", hint: "SY", keywords: "SY" },
  { value: "SD", label: "Sudan", hint: "SD", keywords: "SD" },
  { value: "SS", label: "South Sudan", hint: "SS", keywords: "SS" },
];

function Harness(props: Partial<React.ComponentProps<typeof Combobox>> = {}) {
  const [value, setValue] = useState<string | null>(props.value ?? null);
  return (
    <Combobox
      aria-label="Country"
      options={COUNTRIES}
      {...props}
      value={value}
      onChange={(v) => {
        setValue(v);
        props.onChange?.(v);
      }}
    />
  );
}

const input = () => screen.getByRole("combobox", { name: "Country" });

describe("the option list is portalled out of the control", () => {
  /**
   * The reason is in `Popup.tsx`: an ancestor that scrolls clips its descendants whatever their z-index, and
   * `.mrs-modal` is `overflow: auto`. Asserting the DOM position is the only part of that a layout-free
   * environment can check — but it is the part that actually changed, and if the list ever moves back inside
   * the control the clipping returns silently.
   */
  it("renders the listbox outside the control's own subtree", async () => {
    const { container } = renderDS(<Harness />);
    await userEvent.click(input());

    const list = screen.getByRole("listbox");
    const root = container.querySelector(".mrs-combo")!;
    expect(root).not.toBeNull();
    expect(root.contains(list), "the list must not be a descendant of the control").toBe(false);
    expect(list.parentElement, "the list is portalled to <body>").toBe(document.body);
  });

  /**
   * Radix's modal dialog sets `pointer-events: none` on <body> and re-enables it on the dialog. A popup
   * portalled to the body inherits the `none` and swallows every click on an option — the control looks
   * right and does nothing. This is declared inline rather than in the stylesheet so it cannot be separated
   * from the portalling, and so it holds here, where no stylesheet is loaded at all.
   */
  it("re-enables pointer events on the popup itself", async () => {
    renderDS(<Harness />);
    await userEvent.click(input());
    expect(screen.getByRole("listbox").style.pointerEvents).toBe("auto");
  });

  /** A click on an option is an OUTSIDE click now, by DOM position. If the close handler only checked the
   *  control's ref, the popup would close before the click could commit and nothing would ever be picked. */
  it("still commits a click on an option, which is now outside the control", async () => {
    renderDS(<Harness />);
    await userEvent.click(input());
    await userEvent.click(screen.getByRole("option", { name: /South Sudan/ }));
    expect(input()).toHaveValue("South Sudan");
  });

  it("still closes on a click that is genuinely outside both", async () => {
    renderDS(<><Harness /><button type="button">Elsewhere</button></>);
    await userEvent.click(input());
    expect(screen.getByRole("listbox")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Elsewhere" }));
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
  });
});

describe("a control's icon and a value's icon are different things", () => {
  /**
   * `leadingIcon` describes the CONTROL — the branch switcher's glyph says "this picks a branch" — so it
   * survives typing. An option's `leading` describes the VALUE — a country's flag — and a flag beside a
   * half-typed query is describing something the operator has already left behind.
   */
  it("keeps the control's icon visible while the list is open", async () => {
    const { container } = renderDS(<Harness leadingIcon={<Icon name="branch" aria-hidden />} />);
    expect(container.querySelector(".mrs-combo-leading svg")).not.toBeNull();
    await userEvent.click(input());
    expect(container.querySelector(".mrs-combo-leading svg")).not.toBeNull();
  });

  it("hides the selected option's own glyph while the list is open", async () => {
    const withFlags = COUNTRIES.map((o) => ({ ...o, leading: <span data-testid="flag">*</span> }));
    const { container } = renderDS(<Harness options={withFlags} value="SY" />);
    expect(container.querySelector(".mrs-combo-leading")).not.toBeNull();
    await userEvent.click(input());
    expect(container.querySelector(".mrs-combo-control > .mrs-combo-leading")).toBeNull();
  });
});

describe("the hint reaches the closed control only when asked", () => {
  /**
   * `hint` is doing two jobs. "SY" on a nationality is a search aid and reads as noise in the box; "Home" on
   * a branch qualifies the value and an operator who cannot see it has lost which of six clinics is theirs.
   * Default off, so converting a screen is the moment someone decides which kind theirs is.
   */
  it("shows the label alone by default", async () => {
    renderDS(<Harness />);
    await userEvent.click(input());
    await userEvent.click(screen.getByRole("option", { name: /Syria/ }));
    expect(input()).toHaveValue("Syria");
  });

  it("carries the hint when the call site asks for it", async () => {
    renderDS(<Harness hintWhenClosed />);
    await userEvent.click(input());
    await userEvent.click(screen.getByRole("option", { name: /Syria/ }));
    expect(input()).toHaveValue("Syria · SY");
  });

  it("shows the hint in the list either way — that is what makes the code searchable", async () => {
    renderDS(<Harness />);
    await userEvent.click(input());
    expect(within(screen.getByRole("listbox")).getByText("SY")).toBeInTheDocument();
  });
});

describe("the app-bar silhouette", () => {
  it("marks the pill shape so the app bar reads as a filter, not a form field", () => {
    const { container } = renderDS(<Harness shape="pill" />);
    expect(container.querySelector(".mrs-combo--pill")).not.toBeNull();
  });

  it("defaults to the field shape", () => {
    const { container } = renderDS(<Harness />);
    expect(container.querySelector(".mrs-combo--field")).not.toBeNull();
    expect(container.querySelector(".mrs-combo--pill")).toBeNull();
  });
});

/**
 * The audit's central finding was not that 45 pickers are unsearchable — it was WHY. `Field.tsx` exported a
 * labelled non-searchable picker and no labelled searchable one, so the shortest thing to type was the wrong
 * control, and 19 screens reasonably typed it. These tests hold the properties that make the new default
 * genuinely the better default, so nobody has a reason to reach past it.
 */
describe("ComboboxField is a first-class field, not a Combobox with a label stuck on", () => {
  function FieldHarness(props: Partial<React.ComponentProps<typeof ComboboxField>> = {}) {
    const [value, setValue] = useState<string | null>(null);
    return (
      <ComboboxField label="Country" options={COUNTRIES} {...props} value={value} onChange={setValue} />
    );
  }

  /** The behaviour every other field in the system has, and the one `SelectField` cannot have: `Combobox`
   *  is built on an <input>, so `<label for>` names it directly instead of via `aria-labelledby`. */
  it("focuses and opens the control when its label is clicked", async () => {
    renderDS(<FieldHarness />);
    await userEvent.click(screen.getByText("Country"));
    expect(screen.getByRole("combobox", { name: "Country" })).toHaveFocus();
    expect(screen.getByRole("listbox")).toBeInTheDocument();
  });

  it("ties help text and errors into the accessible description", () => {
    renderDS(<FieldHarness help="Where the beneficiary was born" error="Pick a country" />);
    const control = screen.getByRole("combobox", { name: "Country" });
    const described = (control.getAttribute("aria-describedby") ?? "")
      .split(" ")
      .map((id) => document.getElementById(id)?.textContent);
    expect(described).toContain("Where the beneficiary was born");
    expect(described).toContain("Pick a country");
    expect(control).toHaveAttribute("aria-invalid", "true");
  });

  /**
   * Not a swipe at `SelectField` — a record of the gap that justified building this. `Select` accepts no
   * `aria-describedby`, so a helper line under a SelectField is on screen and absent from the accessible
   * description. If that is ever fixed, this test should be deleted along with the claim.
   */
  it("does what SelectField cannot: SelectField's help text reaches no description", () => {
    renderDS(
      <SelectField
        label="Country" help="Where the beneficiary was born"
        options={COUNTRIES} value={null} onChange={() => {}}
      />,
    );
    expect(screen.getByRole("combobox", { name: "Country" })).not.toHaveAttribute("aria-describedby");
  });

  it("searches — the whole reason for the conversion", async () => {
    renderDS(<FieldHarness />);
    await userEvent.type(screen.getByRole("combobox", { name: "Country" }), "south");
    const options = within(screen.getByRole("listbox")).getAllByRole("option");
    expect(options.map((o) => o.textContent)).toEqual(["South SudanSS"]);
  });
});

describe("what the combobox refuses to do", () => {
  /** The contract the component's own header states: the input is a QUERY, not the value. A half-typed
   *  "Sud" that survived blur would be a text field wearing a droplist's clothes. */
  it("reverts a half-typed query on Escape rather than keeping it", async () => {
    renderDS(<Harness value="SY" />);
    await userEvent.click(input());
    await userEvent.type(input(), "Sud");
    await userEvent.keyboard("{Escape}");
    expect(input()).toHaveValue("Syria");
  });
});
