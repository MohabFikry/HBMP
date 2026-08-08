# 29.6 — `egyptian-drug-list_5.xlsx`: pack-column mapping

> Gate 6 of [phase-29](../../HBMP-Design/claude-code-prompts/phase-29-encounter-and-chronic-prescribing.md):
> "**INSPECT the workbook FIRST and report the column mapping before writing the loader.** Do not assume
> column names." Design: [45 §6](../../HBMP-Design/45-encounter-and-prescription-adjustments.md)

**Status: mapped and loaded.** Design 45 §6 says the sheet "reportedly carries pack size and unit". It does —
in two columns the document does not name, and one of the two is a trap.

## The 33 columns

`Master Lists/egyptian-drug-list_5.xlsx`, sheet `Drug List`, 22,653 rows:

```
A  ID                     L  ATC L2 Name            W  Major Units (per box)
B  Trade Name (EN)        M  ATC L3                 X  Minor Units (total)
C  Price (EGP)            N  ATC L3 Name            Y  Volume / Weight
D  Active Ingredient      O  ATC L4                 Z  Strength
E  Manufacturer           P  ATC L4 Name            AA Dosage Form
F  Drug Class             Q  ATC L5                 AB International Barcode
G  Therapeutic Category   R  ATC L5 Name            AC Local / Imported
H  ATC Code               S  ATC Basis              AD Barcode Country
I  ATC L1                 T  Related ICDs           AE Origin Basis
J  ATC L1 Name            U  ICD Count              AF Price Updated
K  ATC L2                 V  ICD Basis              AG UNHCR
```

Until now the loader "read past" W and X "deliberately rather than carried as dead fields". They stop being
dead the moment a quantity has to be converted into whole packs.

## Mapping

| Target (design 45 §6) | Source | Coverage |
|---|---|---|
| `pack_size` | **X — Minor Units (total)** | **100.0%** (22,653 / 22,653) |
| `prescribing_unit` | derived from **AA — Dosage Form** | **89.0%** (20,158) |
| `is_pack_splittable` | derived from **AA**, overridable per product | **89.0%** (20,158) |
| `pack_unit` | **AA — Dosage Form**, verbatim | 98.7% |
| *(not mapped)* | W — Major Units (per box) | — |

**All three present: 89.0%. The remaining 2,495 rows set `unit_data_incomplete`** and report NotChecked
naming the missing field — never a guessed quantity (invariant 8). Coverage is printed by the loader on every
run, so a drop after a workbook refresh is visible rather than discovered as a NotChecked at a counter.

## The trap: W is not pack size

**W "Major Units (per box)" is strips/blisters per box; X "Minor Units (total)" is prescribing units per box.**
A 20-tablet pack is `W=2, X=20` — two strips of ten. The columns are adjacent, similarly named, and W is the
one whose name sounds more like "pack size".

**Mapping W would make every tablet quantity out by a factor of ten**, in the direction that under-supplies:
a 90-day script needing 270 tablets would resolve to 27 packs of "2". This is recorded in the mapper itself,
not only here.

## Correction to an earlier reading

An initial inspection using a hand-rolled XML parse reported that `Volume / Weight` and `Strength` contained
"bare increasing integers" and concluded the columns were unusable. **That was a defect in the throwaway
parser, not in the workbook** — it failed to resolve shared-string indices on some cell shapes and printed the
index. The loader's own `XlsxReader` resolves them correctly and records the real fill rates (Y 33.3%,
Z 60.4%), which is simply sparse data rather than corrupt data.

The lesson is the one the repo already encodes by having a proper reader: **an ad-hoc parse of a real data
file is evidence about the parse, not about the file.** The claim was retracted before any loader was written
against it.

## Downstream

`ChronicAllocation.Plan` takes `IsPackSplittable` and `PackSize` as nullable and returns `NotChecked` naming
the missing field rather than a number — so the 11% of rows without a derivable form degrade to a stated
refusal. Proved by `Missing_pack_data_on_a_non_splittable_form_yields_NotChecked_naming_the_field` and
`Unknown_splittability_yields_NotChecked_rather_than_assuming_splittable`.

The legacy CSV path carries no pack columns at all and leaves `unit_data_incomplete = true` throughout. That
is deliberate: a fallback source that invented pack sizes would be worse than one that admits it has none.
