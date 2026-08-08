namespace Mersal.MasterData.Domain;

// Master-data entities per 22-data-dictionary.md §10.5 and 15-database-erd.md §13.
// Reference tables (icd/cpt/loinc/atc) key by natural code; drug/interaction/allergen use uuid v7.
// A source_release/version column set makes loads versioned + trackable + reversible.

/// <summary>ICD-10 diagnosis code. PK = code (dotted format, e.g. "E11.9").</summary>
public sealed class IcdCode
{
    public string Code { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string? Chapter { get; set; }
    public bool IsBillable { get; set; }
    public string? Icd11Map { get; set; }

    /// <summary>Immediate parent in the ICD-10 tree (28.7). NULL on a chapter, and on pre-phase-28 rows.</summary>
    public string? ParentCode { get; set; }

    /// <summary>Chapter | Block | Category | Subcategory, from the source's own Type column.</summary>
    public string? NodeKind { get; set; }        // ICD-11 ready (nullable)
    public string? SourceRelease { get; set; }
}

/// <summary>CPT procedure code. PK = code.</summary>
public sealed class CptCode
{
    public string Code { get; set; } = default!;
    public string Description { get; set; } = default!;
    public string? Category { get; set; }
    public string? SourceRelease { get; set; }
}

/// <summary>
/// 29.2 — an OP-Procedure KIND, as master data rather than an enum (design 45 §2). Administered like
/// refill_frequency: adding "Hydrotherapy" is an INSERT, not a release.
/// </summary>
public sealed class ProcedureType
{
    public string Code { get; set; } = default!;
    public string NameEn { get; set; } = default!;
    public string NameAr { get; set; } = default!;

    /// <summary>Drives the composer's "number of sessions" field. The UI follows THIS, never the type's name —
    /// dialysis and rehabilitation are session-based too, and hard-coding the name guarantees that
    /// conversation twice more.</summary>
    public bool IsSessionBased { get; set; }

    public int? DefaultSessions { get; set; }
    public int? MaxSessions { get; set; }

    /// <summary>The CPT sections this type may accompany, as a JSON array. Enforced by
    /// <c>ProcedureTypeRules.Validate</c> — an unvalidated type field is decorative, and every report built on
    /// it is quietly wrong.</summary>
    public string AllowedCptScopes { get; set; } = "[]";

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>The declared sections, parsed. Fails CLOSED to an empty list: a type whose scopes cannot be
    /// read must accompany nothing, rather than everything.</summary>
    public IReadOnlyList<string> Scopes()
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(AllowedCptScopes) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}

/// <summary>LOINC lab observation code. PK = code.</summary>
public sealed class LoincCode
{
    public string Code { get; set; } = default!;
    public string LongName { get; set; } = default!;
    public string? Component { get; set; }
    public string? Property { get; set; }
    public string? SourceRelease { get; set; }
}

/// <summary>ATC classification node. PK = atc_code; level 1..5 (Anatomical→Chemical Substance).</summary>
public sealed class AtcClass
{
    public string AtcCode { get; set; } = default!;
    public string Title { get; set; } = default!;
    public int Level { get; set; }
    public string? SourceRelease { get; set; }
}

/// <summary>A marketed drug (Egyptian drug master). Surrogate uuid v7 PK; drug_code UK.</summary>
public sealed class Drug
{
    public Guid DrugId { get; set; }
    public string DrugCode { get; set; } = default!;   // stable natural key (normalized commercial name)
    public string Name { get; set; } = default!;
    public string? NameAr { get; set; }
    public string? ScientificName { get; set; }
    public string? Manufacturer { get; set; }
    public string? Form { get; set; }                   // route/form
    public string? Strength { get; set; }
    public string? AtcCode { get; set; }                // FK → atc_class (nullable if unmatched)
    public decimal? PriceEgp { get; set; }
    public string? SourceRelease { get; set; }
    public string? SourceRowId { get; set; }            // the source file's own id; makes DrugId derivable

    // ---- 29.6 — prescribing unit, pack size, splittability (design 45 §6) ------------------------------
    /// <summary>The unit a doctor prescribes in — Tablet, mL, Puff, IU… NULL where the sheet did not say.</summary>
    public string? PrescribingUnit { get; set; }

    /// <summary>How many prescribing units are in one pack. NULL where unknown — and the quantity check then
    /// reports NotChecked NAMING this field rather than guessing (invariant 8).</summary>
    public decimal? PackSize { get; set; }
    public string? PackUnit { get; set; }

    /// <summary>
    /// 31.3 — how many PRESCRIBING units one box holds, which is what every quantity is divided by.
    /// </summary>
    /// <remarks>
    /// <para>Equal to <see cref="PackSize"/> for the countable forms and different for every measured one:
    /// a 120 ml bottle of syrup is <c>pack_size = 1</c> and <c>pack_content = 120</c>, and dividing a 210 ml
    /// course by the first produced 210 bottles. Derived from "Volume / Weight" and "Strength" — see
    /// <c>PackUnitRules.Resolve</c>.</para>
    ///
    /// <para>NULL where the workbook records nothing to derive it from, which is a real answer: the quantity
    /// check then reports NotChecked naming this column, rather than a box count computed from a guess.</para>
    /// </remarks>
    public decimal? PackContent { get; set; }

    /// <summary>Whether a pack can be broken. NULL is NOT "yes": assuming splittable is the dangerous default
    /// because it silently permits a fractional inhaler. Defaults from the dosage form but is overridable per
    /// product — "the form is a good heuristic and a poor law".</summary>
    public bool? IsPackSplittable { get; set; }

    /// <summary>True until the loader has populated the three above. Defaults TRUE, so an unloaded row reports
    /// NotChecked rather than a confident answer computed from nothing.</summary>
    public bool UnitDataIncomplete { get; set; } = true;

    // ---- 29.7 — availability and the lowest-price label (design 45 §7) ---------------------------------
    /// <summary>Available / Unavailable / Unknown. THREE states, not a boolean: a boolean defaulting to false
    /// would render the entire catalogue as out of stock on day one, and prescribers would learn to ignore the
    /// indicator before it ever carried real data.</summary>
    public string Availability { get; set; } = "Unknown";

    /// <summary>DERIVED, never authored — recomputed whenever prices load.</summary>
    public bool IsLowestPrice { get; set; }

    /// <summary>price_egp ÷ pack_size. NULL where either is unknown, and a NULL is never labelled.</summary>
    public decimal? PricePerUnit { get; set; }
    public string? LowestPriceGroupKey { get; set; }

    /// <summary>When the label was last computed, so a stale one is detectable.</summary>
    public DateTimeOffset? LowestPriceComputedAt { get; set; }
}

/// <summary>
/// A drug's listed indication: one ICD-10 <b>category</b> this drug is recorded as treating.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IcdCode"/> is a 3-character category ("E11"), not a specific code ("E11.9") — every code
/// in the source file is a category. Compare with <see cref="MasterDataNormalize.IcdCategory"/>, never
/// by equality against a recorded diagnosis.
/// </para>
/// <para>
/// <see cref="Source"/> is not decoration. The mapping is generated at ATC level 4 and is, in the source
/// author's own words, clinical judgement rather than a published dataset — so an indication mismatch is
/// a warning that a prescriber may override, never a block (doc 43 §1).
/// </para>
/// </remarks>
public sealed class DrugIndication
{
    public Guid IndicationId { get; set; }
    public Guid DrugId { get; set; }
    public string IcdCode { get; set; } = default!;
    public bool IsPrimary { get; set; }
    public string Source { get; set; } = default!;
    public string? SourceRelease { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public enum InteractionSeverity { Minor, Moderate, Major, Contraindicated }

/// <summary>A drug-drug interaction (order-insensitive pair).</summary>
public sealed class DrugInteraction
{
    public Guid InteractionId { get; set; }
    public Guid DrugAId { get; set; }
    public Guid DrugBId { get; set; }
    public InteractionSeverity Severity { get; set; }
    public string? Description { get; set; }
    public string? SourceRelease { get; set; }
}

public enum AllergenCategory { Drug, Food, Environmental }

/// <summary>An allergen catalog entry.</summary>
public sealed class Allergen
{
    public Guid AllergenId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public AllergenCategory Category { get; set; }
    public string? SourceRelease { get; set; }

    // ---- 28.1 the mapping that makes this allergen matchable against a medicine (migration 0009) ----

    /// <summary>ATC classes this allergen covers — "all penicillins" as one durable statement, which an
    /// enumeration of molecules expresses badly and survives new products entering the market worse.</summary>
    public string[] AtcScopes { get; set; } = [];

    /// <summary>
    /// False for food and environmental allergens.
    /// </summary>
    /// <remarks>
    /// NOT the same as "unmapped", and the difference is the whole point. An unmapped DRUG allergen is a gap
    /// in our catalogue that a pharmacist must close; a peanut allergy is simply not a question about a
    /// medicine. Conflating them would make every patient with a dust-mite allergy read as a coverage
    /// failure, and the noise would bury the gaps that matter.
    /// </remarks>
    public bool IsDrugMappable { get; set; } = true;

    public string? MappingSource { get; set; }

    /// <summary>The pharmacist who reviewed this mapping. An unreviewed mapping produces confident findings
    /// from unattributable judgement, which is worse than no mapping — enforced by a CHECK in 0009.</summary>
    public string? MappingReviewedBy { get; set; }

    public DateTimeOffset? MappingReviewedAt { get; set; }
}

// --- 14.6 examination type + sensitivity classification (design 37 §5) --------------------------
public enum ExamCategory { Lab, Imaging, Procedure, Consultation, Assessment }

/// <summary>Sensitivity ladder (design 37 §5). Standard = ordinary min-necessary rules; Sensitive/
/// HighlySensitive are content-restricted with a justified release request (enforced in 14.7).</summary>
public enum SensitivityLevel { Standard, Sensitive, HighlySensitive }

/// <summary>Special-category class (design 37 §5). MentalHealth is the confirmed requirement; the rest are
/// configuration for the Medical Director + DPO to ratify — not policy hard-coded in code.</summary>
public enum SensitiveCategory { MentalHealth, HivSti, Genetic, SubstanceUse, ReproductiveHealth, GbvForensic, Other }

/// <summary>A classified examination type — reference data whose sensitivity is denormalized onto orders/
/// results so read-time gating never needs a cross-service join (design 37 §5).</summary>
public sealed class ExaminationType
{
    public Guid ExaminationTypeId { get; set; }
    public string Code { get; set; } = default!;                 // UK
    public string NameEn { get; set; } = default!;
    public string NameAr { get; set; } = default!;
    public ExamCategory Category { get; set; }
    public string DefaultCodeSystem { get; set; } = "CPT";       // CPT | LOINC | LOCAL
    public string? DefaultCode { get; set; }
    public SensitivityLevel SensitivityLevel { get; set; } = SensitivityLevel.Standard;
    public SensitiveCategory? SensitiveCategory { get; set; }
    public string Status { get; set; } = "Active";
    /// <summary>List price in EGP, or NULL when unknown (ADR-0034). NULL is NOT zero — a caller that cannot
    /// establish a price must refuse to quote rather than show 0.00, which at a counter reads as "free".</summary>
    public decimal? PriceEgp { get; set; }
}

// --- 28.1 the ingredient model (design 44 §1.2) -------------------------------------------------
//
// Products are what a pharmacy stocks; MOLECULES are what a clinical rule is about. An allergy, an
// interaction and a duplicate-therapy check are all questions about molecules, and the Egyptian
// catalogue holds tens of thousands of products — so keying any of them on a product id makes the
// rule table unpopulatable by construction, which is why masterdata.drug_interaction has zero rows.

/// <summary>One active molecule. <see cref="IngredientKey"/> is the business key rules point at.</summary>
public sealed class Ingredient
{
    public Guid IngredientId { get; set; }

    /// <summary>
    /// The normalised INN name — lower-case, whitespace collapsed, trailing salt or hydrate form removed.
    /// </summary>
    /// <remarks>
    /// A NAME rather than a uuid because clinical governance requires a named pharmacist to review every
    /// rule before it goes active, and nobody proofreads a uuid. Normalisation comes from
    /// <c>Mersal.Ingredients.IngredientTokens</c>, which is the platform's only implementation of it.
    /// </remarks>
    public string IngredientKey { get; set; } = default!;

    public string NameEn { get; set; } = default!;
    public string? NameAr { get; set; }

    /// <summary>Substance-level ATC where the catalogue supplies one. Often null: 14.8% of products carry
    /// no ATC, and a combination product's ATC describes the compound rather than this molecule.</summary>
    public string? AtcCode { get; set; }

    public string? Rxcui { get; set; }
    public bool IsActive { get; set; } = true;
    public string Source { get; set; } = default!;
    public string? SourceRelease { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>What a product is made of. A COMBINATION PRODUCT HAS SEVERAL ROWS — that is the point.</summary>
public sealed class DrugIngredient
{
    public Guid DrugId { get; set; }
    public string IngredientKey { get; set; } = default!;
    public int Ordinal { get; set; }
    public string? Strength { get; set; }
    public string? SourceRelease { get; set; }
}

/// <summary>Confidence in a cross-reactivity relationship. Stated in the finding, never implied.</summary>
/// <remarks>
/// The historically quoted ~10% penicillin/cephalosporin figure is not supported by current evidence: risk
/// tracks R1 side-chain similarity rather than the shared beta-lactam ring. Blanket avoidance after a
/// penicillin label causes real harm through inferior antibiotic choice, so a prescriber has to be told how
/// good the evidence is — "possible, low confidence, side chains differ" is actionable where "allergy
/// conflict" is not.
/// </remarks>
public enum CrossReactivityConfidence { High, Moderate, Low, Theoretical }

/// <summary>A curated cross-reactivity relationship, with its confidence and citation.</summary>
public sealed class CrossReactivityGroup
{
    public string GroupCode { get; set; } = default!;
    public string NameEn { get; set; } = default!;
    public string NameAr { get; set; } = default!;
    public CrossReactivityConfidence Confidence { get; set; }
    public string StatementEn { get; set; } = default!;
    public string StatementAr { get; set; } = default!;
    public string Citation { get; set; } = default!;
    public string Source { get; set; } = default!;
    public string ReviewedBy { get; set; } = default!;
    public DateTimeOffset ReviewedAt { get; set; }
}

/// <summary>A molecule or an ATC class inside a cross-reactivity group. Exactly one of the two.</summary>
public sealed class CrossReactivityMember
{
    public string GroupCode { get; set; } = default!;
    public string? IngredientKey { get; set; }
    public string? AtcScope { get; set; }
}

/// <summary>An exact molecule a recorded allergen means. Reviewed by construction — the columns are NOT NULL.</summary>
public sealed class AllergenIngredient
{
    public Guid AllergenId { get; set; }
    public string IngredientKey { get; set; } = default!;
    public string Source { get; set; } = default!;
    public string ReviewedBy { get; set; } = default!;
    public DateTimeOffset ReviewedAt { get; set; }
}

/// <summary>An allergen's cross-reactivity groups. More than one, at different confidences, is normal.</summary>
public sealed class AllergenCrossReactivity
{
    public Guid AllergenId { get; set; }
    public string GroupCode { get; set; } = default!;
}

/// <summary>One (code, ancestor) pair from the materialised ICD-10 closure (28.7).</summary>
/// <remarks>
/// Materialised rather than walked per query: the indication check runs on every keystroke-triggered
/// validation against every diagnosis and every indication, while the tree itself changes once a year when
/// the catalogue is reloaded. A recursive CTE per comparison would put a tree walk inside a loop inside a
/// consultation.
/// </remarks>
public sealed class IcdAncestor
{
    public string Code { get; set; } = default!;
    public string AncestorCode { get; set; } = default!;

    /// <summary>1 = parent, 2 = grandparent. Lets a caller ask for "the category" without knowing the shape.</summary>
    public int Depth { get; set; }
}

// --- 28.3 ingredient-level interactions (design 44 §1.2) ----------------------------------------

/// <summary>Which vocabulary a rule's side is written in.</summary>
public enum RuleSubjectKind
{
    /// <summary>An <see cref="Ingredient.IngredientKey"/> — one molecule.</summary>
    Ingredient,

    /// <summary>An ATC class at any level. 'M01A' says "all NSAIDs" in one row, and keeps saying it.</summary>
    AtcClass,
}

public enum InteractionOnset { Rapid, Delayed, Unknown }

public enum EvidenceLevel { Established, Probable, Theoretical }

/// <summary>
/// One curated drug–drug interaction, keyed on molecules and classes rather than products.
/// </summary>
/// <remarks>
/// <para>
/// Replaces <see cref="DrugInteraction"/>, which keyed a pair on two product uuids. With 22,653 products in
/// the catalogue that model needed one row per pair of BRANDS — so it held zero rows and would have stayed
/// empty. It was never a data backlog; it was an unpopulatable model.
/// </para>
/// <para>
/// One row here — warfarin × M01A — covers every brand of warfarin against every NSAID on the market, in
/// both directions, and keeps covering them as products come and go.
/// </para>
/// </remarks>
public sealed class InteractionRule
{
    public Guid RuleId { get; set; }

    public RuleSubjectKind SubjectKind { get; set; }
    public string SubjectValue { get; set; } = default!;
    public RuleSubjectKind ObjectKind { get; set; }
    public string ObjectValue { get; set; } = default!;

    public InteractionSeverity Severity { get; set; }

    // The three fields that make an alert actionable rather than merely alarming (design 44 §3).
    public string MechanismEn { get; set; } = default!;
    public string MechanismAr { get; set; } = default!;
    public string ClinicalEffectEn { get; set; } = default!;
    public string ClinicalEffectAr { get; set; } = default!;

    /// <summary>What to do instead — the field most likely to change the prescription.</summary>
    public string ManagementEn { get; set; } = default!;
    public string ManagementAr { get; set; } = default!;

    public InteractionOnset Onset { get; set; } = InteractionOnset.Unknown;
    public EvidenceLevel EvidenceLevel { get; set; }

    public string Citation { get; set; } = default!;
    public string Source { get; set; } = default!;
    public string? SourceRelease { get; set; }

    /// <summary>Enforced by a CHECK: a rule cannot be active without a named reviewer.</summary>
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A drug that is HAZARDOUS IN a condition — the check design 44 §5 says the request actually wanted.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="DrugIndication"/>, and the distinction is why one of them is noise. Indication
/// asks "is this drug USED FOR this condition" — a mismatch means off-label, which is legitimate and common,
/// so the warning fires constantly and is dismissed constantly. This asks "is this drug DANGEROUS IN this
/// condition", where a hit means potential harm.
/// </para>
/// <para>
/// <see cref="IcdScope"/> is a node in the ICD-10 hierarchy (28.7), matched descendant-or-self, so a rule
/// written at a category catches every specific code underneath it without enumerating them.
/// </para>
/// </remarks>
public sealed class DrugDiseaseContraindication
{
    /// <summary>
    /// The sentinel <see cref="IcdScope"/> for a rule that depends on pregnancy STATUS rather than a coded
    /// diagnosis.
    /// </summary>
    /// <remarks>
    /// Pregnancy is deliberately not modelled as an ICD scope. A rule keyed on O00-O9A would fire only for a
    /// patient somebody had already coded as pregnant on this visit — which is precisely the patient nobody
    /// needs reminding about. The status comes from <c>emr.pregnancy_status</c> and is carried as patient
    /// context, so an ACE inhibitor is caught for a woman whose pregnancy is recorded anywhere in her record.
    /// </remarks>
    public const string PregnancyScope = "PREGNANCY";

    public Guid RuleId { get; set; }

    public RuleSubjectKind SubjectKind { get; set; }
    public string SubjectValue { get; set; } = default!;

    /// <summary>An ICD-10 node, or <see cref="PregnancyScope"/>.</summary>
    public string IcdScope { get; set; } = default!;

    public InteractionSeverity Severity { get; set; }

    public string MechanismEn { get; set; } = default!;
    public string MechanismAr { get; set; } = default!;
    public string ClinicalEffectEn { get; set; } = default!;
    public string ClinicalEffectAr { get; set; } = default!;

    /// <summary>
    /// What to give instead. NOT NULL in the schema on purpose: a contraindication that says only "avoid"
    /// leaves the prescriber with a patient still in pain and no alternative, which is how a safety rule
    /// becomes something to click past.
    /// </summary>
    public string ManagementEn { get; set; } = default!;
    public string ManagementAr { get; set; } = default!;

    public EvidenceLevel EvidenceLevel { get; set; }
    public string Citation { get; set; } = default!;
    public string Source { get; set; } = default!;
    public string? SourceRelease { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The age band a dosing rule applies to. Bands, not ages, because that is how the sources write them.</summary>
public enum DosingPopulation { Neonate, Infant, Child, Adolescent, Adult, Geriatric }

/// <summary>
/// An indication- and population-keyed dosing rule (28.10, design 44 §4).
/// </summary>
/// <remarks>
/// <para>
/// Replaces a per-drug maximum with no indication and no population. Three dimensions were missing and each
/// changes the number: the same molecule is dosed differently for different conditions, mg/kg is the only
/// correct paediatric calculation in a population that skews paediatric, and oral and intravenous ceilings
/// differ.
/// </para>
/// <para>
/// Keyed on the molecule like every other clinical rule since 28.1 — a rule per PRODUCT would need one row
/// per brand of paracetamol in a 22,653-product catalogue.
/// </para>
/// </remarks>
public sealed class DosingRule
{
    public Guid RuleId { get; set; }

    public RuleSubjectKind SubjectKind { get; set; }
    public string SubjectValue { get; set; } = default!;

    /// <summary>NULL means "any indication" — the general ceiling. A scoped rule is more specific and wins.</summary>
    public string? IndicationIcdScope { get; set; }

    public DosingPopulation Population { get; set; }
    public string? Route { get; set; }

    public string DoseUnit { get; set; } = default!;
    public decimal? MinSingle { get; set; }
    public decimal? MaxSingle { get; set; }
    public decimal? TypicalDaily { get; set; }
    public decimal? MaxDaily { get; set; }
    public int? MaxDurationDays { get; set; }

    public bool IsWeightBased { get; set; }
    public decimal? MgPerKgMin { get; set; }
    public decimal? MgPerKgMax { get; set; }

    /// <summary>
    /// Whether a mg/kg calculation is capped at the adult maximum.
    /// </summary>
    /// <remarks>
    /// Matters more than it looks. A 60kg twelve-year-old on a mg/kg rule computes past the adult ceiling,
    /// and a check reporting that as within-range would be endorsing an overdose it had calculated itself.
    /// </remarks>
    public bool WeightCappedAtAdultDose { get; set; } = true;

    public bool RequiresRenalFunction { get; set; }
    public string? RenalAdjustmentNote { get; set; }
    public string? HepaticNote { get; set; }

    public string Citation { get; set; } = default!;
    public string Source { get; set; } = default!;
    public string? SourceRelease { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
