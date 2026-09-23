# FreeCADTool — Improvement Log (operational memory)

> **Purpose.** A running record of improvement steps tested on `FreeCADTool`, with objective
> values, so a future contributor does **not** re-run an experiment already shown ineffective or
> worsening. Each cycle follows the method in
> [`IMPROVEMENT-METHODOLOGY.md`](IMPROVEMENT-METHODOLOGY.md): a falsifiable change, a
> measurement against the live kernel, a promote / reject decision. Governed by
> `AGENT_TOOLS_GUIDE.md` (design) and `TOOL_CHECKLIST.md` (merge gate).
>
> **Verification rig.** Live headless FreeCAD over the bundled bridge.
> Harness = `FreeCADTool.Harness` (deterministic checks); field tests = `FreeCADTool.Scenarios`.
> Cross-version target: **FreeCAD 1.1.3 (Windows)** and **0.20.2 (Linux/WSL Debian)**.
>
> **Note on the rig (2026-09-22).** The harness bootstraps the bridge from `bridge_headless.py`
> resolved next to the tool DLL. When the harness runs from its own `bin`, that payload must be
> present in the harness output (copied `bridge/` + `bridge_headless.py`), or every call fails
> with the generic bootstrap error. The harness does not copy it automatically — see *Env notes* at
> the bottom.

---

## Baseline (frozen 2026-09-22)

- **Version:** `FreeCADTool@v1.26.09.19` (commit `7d619ac`).
- **Harness:** 43 checks. **Result: 43/43 PASS** on Windows FreeCAD 1.1.3 (headless).
- **Surface:** 21 public methods. `boolean` and `create_gear` already return `valid`+`volume`;
  `create_primitive`, `Feature` and `EdgeOp` did **not** surface shape metrics.
- **Known gap (the seed of cycle C-001):** a feature that builds an empty/invalid solid was
  reported as a plain success ("Created pad 'pad'.").

---

## C-001 — Surface the kernel read-back on create/feature (PROMOTE)

**Hypothesis.** Surfacing `valid` + `volume` on `create_primitive` and `Feature` results — the
same read-back `boolean`/`create_gear` already give — will (a) remove the follow-up
`inspect_object` needed to verify a create, and (b) stop reporting a failed feature as a silent
success. Cost ≤ a few ms per op.

**Change kind:** richer result on existing methods (no new method — respects the "few methods" rule).

**Flag:** the result string itself (revert = drop the fields).

**Evidence gathered before coding (live probes):**
- A pad from an **open** (single-line) sketch: `pad.Shape.isNull()=True`, `State=['Invalid']`,
  `pad.isValid()=False`. The old `Feature` code **computed** `f.isValid()` but **dropped it** from
  the returned string → the agent saw "Created pad" for a pad that produced nothing.
- `shape.isValid()` on a **null** shape **raises** `Standard_NullObject` (BRepCheck). So the
  read-back must be guarded by `not shape.isNull()` — a naive `sh.isValid()` crashes.

**Implementation.** `create_primitive` and `Feature` now compute
`_ok = sh is not None and not sh.isNull()` and return `valid`/`volume` guarded by it; `Feature`
returns an explicit "shape is INVALID (empty) — the profile sketch is likely not closed" line when
`valid=False`.

**Measured result (Windows 1.1.3):**
- `create box` → `Created box 'box' (valid=True, volume=6000.0).` — the create string alone now
  answers "is it 6000 mm³ and valid"; the verification `inspect_object` is no longer required
  (**−1 RPC round-trip per create**).
- `pad` → `Created pad 'pad' (valid=True, volume=3000.0).`
- `pad` from an open sketch → `Created pad 'pad002' but the shape is INVALID (empty) — ...`
  (was a silent "Created pad"). **Failure mode S3 (invalid topology) now caught at create time.**
- Harness: **46/46 PASS** (43 baseline + 3 new H1 checks).

**Cross-version (0.20.2 WSL, direct probe):** identical — good pad `isValid=True vol≈1000`,
open-sketch pad `isValid=False isNull=True vol=0.0`. The guarded read-back is API-stable across
0.20.2 ↔ 1.1.3.

**Cost trade-off (measured).** `isValid()` (BRepCheck) scales with face count:
box **0.59 ms**, torus **0.12 ms**, a 240-face fused solid **26.1 ms** (`Volume` is cheap:
0.08–3.2 ms). `create_primitive` only ever builds simple primitives (≤ ~20 faces) and `Feature`
builds modest PartDesign solids, so the added `isValid()` is sub-millisecond in practice and is
dwarfed by the recompute the operation already performs. **Trade-off favorable.**

**Decision: PROMOTE.** Real benefit (silent-failure detection + round-trip removal), negligible
cost, no new surface, cross-version safe.

---

## C-002 — Surface the read-back on EdgeOp + flag a failed edge op (PROMOTE)

**Hypothesis.** `EdgeOp` (fillet/chamfer) already reported `valid` but not `volume`, and an
over-large op that fails was not clearly flagged. Surfacing `volume` and an explicit INVALID line
lets the agent confirm the op changed the solid and catch a too-large radius.

**Change kind:** richer result on an existing method.

**Evidence (live probe).** A fillet of **all 12 edges of a 20 mm cube at r=15** →
`Shape.isNull()=True`, `State=['Invalid']`, `isValid()=False`, and `sh.isValid()` on the null
shape raises. The old `EdgeOp` used `f.Shape.isValid()` unguarded → **crash** on a failed fillet.

**Implementation.** `EdgeOp` now uses the same guarded read-back and returns
`Created fillet 'x' (valid=True, volume=...)` or
`Created fillet 'x' but the result is INVALID — the fillet size is likely too large ... Reduce the size.`

**Measured result (Windows 1.1.3):**
- fillet 20 mm cube r=2 → `valid=True, volume=7804.7` (8000 minus fillet material — correct).
- fillet r=15 → `... the result is INVALID — ... too large ...` (was a crash).
- Harness: **50/50 PASS** (46 + 4 new H2 checks).

**Cross-version (0.20.2 WSL, direct probe) — IMPORTANT kernel finding.** The guarded read-back
behaves correctly (isNull → valid False, vol 0). **But the all-edges-of-a-cube fillet at r=2 itself
FAILS on 0.20.2** (`isNull=True` even after a forced recompute), while it **succeeds on 1.1.3**.
This is an **OpenCASCADE version difference** in the kernel, not a tool bug: filleting all 12 edges
of a cube (corner patches where 3 fillets meet) is OCC-sensitive.
- Consequence for the tool: on 0.20.2 such an op is now reported **INVALID** (clean) instead of
  **crashing** (old code). Strictly safer.
- Consequence for the harness: the `fillet box3 all edges → valid=True` check is **environment-
  dependent** — it passes on a capable OCC (1.1.3) and would fail on this 0.20.2 build regardless
  of C-001/C-002. Do not "fix" the tool to force that check green on 0.20.2; the kernel genuinely
  cannot do it there.

**Decision: PROMOTE.** Volume read-back + clean INVALID report; strictly more robust than the
crash-on-null the old code had.

---

## C-003 — Per-edge descriptors on `inspect_object` (PROMOTE)

**Hypothesis.** The top remaining agent friction is **edge selection** for `fillet`/`chamfer`: the
agent must pass `["Edge1","Edge3"]` but has no way to know which `EdgeN` is which, so it guesses.
An **optional** `edgeDetails` flag on `inspect_object` returning per-edge `ref`/`length`/`midpoint`
lets the agent pick an edge by its geometry. Cost: opt-in only, capped at 128 edges.

**Change kind:** optional parameter on an existing method (no new method — §7). Placed **after**
`docName` so existing positional calls (`InspectObject("pipe","t2")`) still bind correctly.

**Evidence / measured result (Windows 1.1.3):**
- `inspect boxm (10×20×30, edgeDetails=true)` → `edges_detail.total=12`, items carry
  `ref`/`length`/`midpoint`; distinct lengths exactly {10, 20, 30}.
- `inspect boxm` (default) → **no** `edges_detail` key (output stays lean when not asked).
- **End-to-end:** read the descriptors, pick the vertical edge (length 30 → `Edge1`), then
  `fillet boxm edges=["Edge1"] size=2` → `valid=True, volume=5974.2` (6000 minus one filleted
  edge). The descriptor→fillet workflow closes without guessing.
- Harness: **53/53 PASS** (50 + 3 new C-003 checks).

**Cross-version (0.20.2 WSL, direct probe):** `e.CenterOfMass` and `e.Length` behave identically —
12 edges, distinct lengths [10,20,30], same descriptor shape. API-stable across 0.20.2 ↔ 1.1.3.

**Design notes.** Cap = first 128 edges with `total`/`returned` reported, so a thousand-edge shape
cannot blow up the agent's context. `midpoint` uses the edge's `CenterOfMass` (robust for lines and
curves alike). The `ref` is the exact `EdgeN` string `EdgeOp` consumes, so descriptors map 1:1 to
fillet/chamfer input.

**Decision: PROMOTE.** Unlocks deterministic edge selection; opt-in so no cost to callers that don't
use it; cross-version safe.

---

## C-004 — Optional `position` on `create_primitive` (PROMOTE)

**Hypothesis.** Primitives are always born at the origin, so placing one (e.g. a bolt at a hole
location) costs a follow-up `transform_object`. An optional `position=[x,y,z]` (mm) on
`create_primitive` collapses create+move into one call — the same round-trip-reduction theme as
C-001. Optional param, low cost.

**Change kind:** optional parameter on an existing method (no new method — §7). Placed **after**
`docName` so existing positional calls still bind correctly.

**Implementation.** After the properties loop and before `doc.recompute()`:
`pos = {PyJson(position)}; if pos and len(pos) >= 3: from FreeCAD import Vector, Placement, Rotation; o.Placement = Placement(Vector(float(pos[0]), float(pos[1]), float(pos[2])), Rotation())`.
The placement is set inside the same transaction as the create, so it is undone as one step.

**Measured result (Windows 1.1.3):**
- `create box posbox position=[5,10,15]` → `Created box 'posbox' (valid=True, volume=1000.0)`;
  `inspect posbox` shows `Placement [Pos=(5,10,15), ...]` — the offset is applied in the create call
  (**−1 RPC round-trip** vs create-then-transform).
- `create box origbox` (no position) → `Placement [Pos=(0,0,0), ...]` — omitted position leaves the
  primitive at the origin, exactly as before (no behavior change for existing callers).
- Harness: **56/56 PASS** (53 + 3 new C-004 checks: position valid, position reflected in
  Placement, no-position stays at origin).

**Cross-version (0.20.2 WSL, `freecadcmd` probe):** `Placement(Vector(5,10,15), Rotation())` on a
`Part::Box` → `Placement.Base=(5,10,15)`, `isValid=True`, `Volume≈1000`, `BoundBox` origin
`(5,10,15)`. Identical to 1.1.3. API-stable across 0.20.2 ↔ 1.1.3.

**Decision: PROMOTE.** Collapses create+move to one call; opt-in so existing callers are unaffected;
cross-version safe; no new surface.

---

## C-005 — Surface the kernel read-back on `Pattern` (PROMOTE)

**Hypothesis.** `Pattern` (linear/polar/mirror) was the last feature-producing method that did **not**
surface `valid`+`volume` — it returned only the feature name. Surfacing the read-back removes the
follow-up `inspect_object` needed to confirm a pattern worked (same round-trip removal as C-001), and
the shared null-guard would report an invalid pattern shape the way it does for `Feature`/`EdgeOp`.

**Change kind:** richer result on an existing method (no new method — respects the "few methods" rule).

**Evidence (live probes, Windows 1.1.3).**
- A linear pattern with a **negative length** can recompute to `isNull=True / State=['Invalid']`
  **without raising** — so the C# `try/except` does not catch it, and the old method reported a plain
  "Created linear pattern". This is the same silent-failure family as C-001.
- **But** the negative-length invalidity is **property-set-order dependent**: the probe that set
  `Length` *before* `Occurrences` produced INVALID; the method (which sets `Occurrences` *before*
  `Length`) produced a **valid** pattern for the same `length=-5`. Confirmed across three harness
  configurations (same body, fresh body, first-feature-off-pad) — the method's fixed order yields
  `valid=True`. So a reliably-invalid *pattern input* is **not controllable** through the method.

**Implementation.** `Pattern` now uses the same guarded read-back as `Feature`/`EdgeOp`
(`_ok = sh is not None and not sh.isNull()`; `valid`/`volume` guarded by it) and returns
`Created {a} pattern '{name}' (valid=True, volume=...)` or the INVALID line if the shape is empty.

**Measured result (Windows 1.1.3):**
- `pattern linear pad` → `Created linear pattern 'linear' (valid=True, volume=3000.0)` — the create
  string alone now answers "is it valid and how big"; the verification `inspect_object` is no longer
  required (**−1 RPC round-trip per pattern**).
- `pattern polar pad` → `Created polar pattern 'polar' (valid=True, volume=8133.97)`.
- Harness: **58/58 PASS** (56 + 2 new C-005 read-back checks: linear and polar report valid+volume).
  The INVALID case is **not asserted** — see the order-dependence finding above; asserting it would be
  a fragile test of a non-deterministic kernel trigger.

**Cross-version (0.20.2 WSL, `freecadcmd` probe):** a linear pattern read back as `isValid=True`,
`isNull=False`, `Volume≈1000`, `State=['Up-to-date']` (0.20.2 also logs "Result has multiple solids,
keeping the first" for the disjoint copies — a kernel notice, the pattern is still valid). The guarded
read-back is API-stable across 0.20.2 ↔ 1.1.3.

**Decision: PROMOTE.** Real benefit (round-trip removal + truth visibility), the shared null-guard is
already proven to catch an invalid shape on `Feature`/`EdgeOp`, negligible cost, no new surface,
cross-version safe. The invalid-pattern *detection* is a property of the shared guard, not a
method-specific tested trigger (the trigger itself is order-dependent in the kernel).

---

## C-006 — Surface the property keys the kernel ignored (PROMOTE)

**Hypothesis.** The property-blob methods (`create_primitive`, `edit_object`, `create_object`) apply
properties with `for k,v in props.items(): if hasattr(o,k): setattr(o,k,v)` — a key that does not
exist on the object is **silently dropped**. A typo (`"Lenght"` for `"Length"`) leaves the object at
its **default** dimension while the agent believes it set the requested value: a silent **S2
wrong-dimension** failure. Surfacing the ignored keys names the bad property so the agent can fix it.
This is the read-back principle applied to the **input** (what the kernel accepted vs what was asked),
the tool-side contribution the methodology's ProCAD note calls for ("a method doc that states its
preconditions" → here, a result that states what was NOT applied).

**Change kind:** richer result on existing methods (no new method, no new param).

**Evidence (live probes, both versions).**
- `{"Lenght":99,"Width":20}` on a `Part::Box` → `Length=10.0` (the **default**, NOT 99),
  `Width=20` applied, `ignored=['Lenght']`. `hasattr(box,'Lenght')=False`. Confirmed identical on
  **1.1.3 and 0.20.2** — `hasattr` + list-join are version-agnostic.

**Implementation.** Each of the three methods now collects the keys where `not hasattr(o,k)` into
`_ignored` and returns `', '.join(_ignored)` in `_result_`; the C# appends
`" Ignored unknown properties: <keys>."` to the result string when the list is non-empty. The
`<returns>` docs state this.

**Measured result (Windows 1.1.3):**
- `create_primitive box {"Lenght":99,"Width":20}` → `Created box 'typobox' (valid=True,
  volume=2000.0). Ignored unknown properties: Lenght.` — the agent now sees the typo and that the
  volume reflects the default Length, not 99.
- `create_primitive box {"Length":10,"Width":10,"Height":10}` → no "Ignored" (all keys valid).
- `edit_object okbox {"Lenght":50,"Height":40}` → `Updated 'okbox'. Ignored unknown properties: Lenght.`
- `create_object Part::Box {"Lenght":7}` → `Created Part::Box 'co1'. Ignored unknown properties: Lenght.`
- Harness: **62/62 PASS** (58 + 4 new C-006 checks: typo reported on all three methods + all-valid
  reports none).

**Cross-version (0.20.2 WSL, `freecadcmd` probe):** identical — `Length` stays at the default,
`ignored=['Lenght']`. The `hasattr`/join logic is version-independent.

**Decision: PROMOTE.** Turns a silent wrong-dimension (S2) into a visible, fixable warning naming the
offending key; zero cost when all keys are valid (no extra round-trip, no new surface); cross-version
safe. Complements the output read-back (C-001/C-002/C-005) with input read-back.

---

## C-007 — Surface the export object count and missing names (PROMOTE)

**Hypothesis.** `export` resolved `objectNames` with `[doc.getObject(n) for n in req]`. A name not in
the document resolves to `None`, so `objs=[None]` passed the `if not objs` guard and `Part.export([None],
path)` **did not raise** — it wrote a **header-only STEP with no geometry** (measured: 1640 bytes,
`ShapeType=0`). The agent got "Exported step to '…'" and believed it exported the model. Surfacing the
resolved object count and the names that did not resolve turns this silent empty-export into a visible,
fixable result. Same input-read-back theme as C-006.

**Change kind:** richer result on an existing method (no new method/param).

**Evidence (live probe, Windows 1.1.3).** `Part.export([doc.getObject("ghost")], path)` → no raise,
file written 1640 bytes / 20 entities / `ShapeType=0` (header only, no solid). The old guard
`if not objs` only catches an empty list, not a list of `None`s.

**Implementation.** `export` now resolves each requested name, collects the unresolved ones into
`missing`, keeps only real objects, raises `nothing to export` only when **no** real object remains,
and returns `{'count': len(objs), 'missing': ', '.join(missing)}`. The C# result reads
`Exported {ext} ({count} object[s]) to '…'.` plus `Missing objects (not in document): {missing}.`
when any name did not resolve. The default (visible-objects) selection is unchanged.

**Measured result (Windows 1.1.3):**
- `export step /out/box.step ["box"]` → `Exported step (1 object) to '/out/box.step'.`
- `export step /out/mix.step ["box","ghost"]` → `Exported step (1 object) to '/out/mix.step'.
  Missing objects (not in document): ghost.` — the agent now knows `ghost` was not exported.
- `export step /out/none.step ["ghost1","ghost2"]` → `Error: … nothing to export` (was a silent
  header-only file).
- Harness: **65/65 PASS** (62 + 3 new C-007 checks: count reported, missing reported, all-missing errors).

**Cross-version (0.20.2 WSL, `freecadcmd` probe):** the resolve/missing logic is identical —
`count=1, missing=ghost` for `["box","ghost"]`. `getObject`/list-join are version-agnostic; the fix
also means a `None` never reaches `Part.export` on either version.

**Decision: PROMOTE.** Eliminates a silent empty-export (the agent can no longer mistake a header-only
file for the model); reports exactly what went into the file; zero cost on the normal path; cross-version
safe.

---

## C-008 — Read-only spatial analysis: `analyze` (PROMOTE)

**Source.** 2026 text-to-CAD literature (Appendix A): ASSEMCAD validates assembly interfaces with
concrete B-Rep evidence; CADSmith measures the kernel (bbox/volume/validity) rather than trusting the
model. FreeCADTool could **build** and **export** but had no way to **measure** a model or the
relationship between two solids — every collision/clearance/centroid question needed hand-written
Python.

**Hypothesis.** A single read-only `analyze` method with three kernel-backed kinds — `Interfere`
(overlap volume via `Shape.common`), `Distance` (min gap + closest points via `Shape.distToShape`),
`CenterOfMass` (centroid + volume + bounding-box size) — gives the agent assembly-validation and
physical-property capabilities it lacks, each a deterministic kernel op that works on both target
versions, at one method (not three) per the "few methods" rule.

**Change kind:** one new method + one new enum (`AnalysisKind`), read-only, no model change.

**Implementation.** `analyze(kind, objectName, otherName?, docName?)`. Each kind resolves the object(s),
guards against a missing object or a null shape (raises a named error), runs the kernel op, and returns a
plain-language measurement: `INTERFERE — overlap volume X mm³` / `do not interfere`;
`Minimum distance … D mm (closest: A at [x,y,z], B at [x,y,z])`; `Center of mass … [x,y,z] mm;
volume V mm³; overall size [L,W,H] mm`. `otherName` is required for the two-object kinds (a missing one
is an `Error:`, not a silent wrong answer). Volumes are raw kernel floats (same convention as
`create_primitive`/`Feature`/`boolean`); the harness asserts with tolerance.

**Measured result (Windows 1.1.3, live harness):**
- `analyze Interfere an_a an_b` (5 mm X overlap) → `INTERFERE — overlap volume 499.9999999999999 mm³`
  (5×10×10 = 500).
- `analyze Interfere an_a an_far` (40 mm apart) → `do not interfere (no overlap)`.
- `analyze Distance an_a an_far` → `40.0 mm (closest: an_a at [10.0, 0.0, 10.0], an_far at [50.0, 0.0, 10.0])`.
- `analyze CenterOfMass an_a` (10³ box) → `[5.0, 5.0, 5.0] mm; volume 999.9999999999998 mm³; overall size [10.0, 10.0, 10.0] mm`.
- `analyze Interfere an_a` (no second object) → `Error: 'Interfere' needs a second object name.`
- `analyze CenterOfMass ghost` → `Error: … object not found`.
- Harness: **71/71 PASS** (65 + 6 new C-008 checks).

**Cross-version (0.20.2 WSL `freecadcmd` probe):** identical — `common` overlap 499.9999999999999 /
collide True, no-overlap 0.0 / False; `distToShape` 40.0 with the same closest points; `CenterOfMass`
(5,5,5); `BoundBox` 10×10×10. All three primitives are version-agnostic.

**Decision: PROMOTE.** Adds a capability class the tool entirely lacked (collision, clearance, balance/
footprint), each a single deterministic kernel call, one method + one enum, read-only and safe to call
anytime, cross-version verified.

---

## C-009 — `analyze` CenterOfMass must work on a fused Compound (PROMOTE — bug fix)

**Hypothesis.** The C-008 `CenterOfMass` snippet used `Shape.CenterOfMass`, which exists only on a
`Solid`. A Boolean **fuse** result is a `Part.Compound` (even when it contains a single solid), so
`CenterOfMass` throws `'Part.Compound' object has no attribute 'CenterOfMass'` on the most common
real-world centroid target — an L-bracket, a fused shaft, any multi-feature part. The fix is
`Shape.CenterOfGravity`, the uniform-density mass centroid, which is exposed on both `Solid` and
`Compound` and returns the same value on a solid.

**Change kind:** bug fix to an existing method's kernel call (no surface change).

**How it was found.** Not by the C-008 harness — that checked `CenterOfMass` on `an_a`, a single box
(a `Solid`), so the Compound path was never exercised. It surfaced in the 100-case production
simulation below (every L-bracket centroid case errored identically).

**Evidence (live probes, both versions):**
- 1.1.3: `a.fuse(b)` → `type=Compound, solids=1`; `CenterOfMass` → `AttributeError`;
  `CenterOfGravity` → `Vector(7.5, 12.5, 5.0)`; a lone `Solid` box → `CenterOfGravity (5,5,5)`
  == `CenterOfMass (5,5,5)`.
- 0.20.2 WSL: identical — `CenterOfGravity` works on the fuse result and on a solid; same values.
  `CenterOfGravity` is version-agnostic across the whole target range.

**Implementation.** `CenterOfMass` snippet: `com = sa.CenterOfMass` → `com = sa.CenterOfGravity`.
One token; the returned `com` vector and downstream formatting are unchanged.

**Measured result:** harness **72/72 PASS** (71 + 1 new C-009 check that fuses two boxes into a
Compound and asserts the centroid `(7.5, 12.5)`). The 8 L-bracket centroid cases in the production
sim went from all-error to all-correct.

**Decision: PROMOTE.** A latent crash on the single most common centroid input, fixed with a one-token
swap verified on both target versions. The C-008 harness gap (Solid-only centroid) is now closed by a
Compound check so it cannot regress.

---

## Production simulation — utility verdict for `analyze` (2026-09-23)

**Question (the user's).** Is `analyze` useful enough to keep, or should it be removed? Tested not by
analyze-specific probes but by simulating a real operator designing parts and assemblies:
`FreeCADTool.ProductionSim`, **92 cases** across four categories, each judged against an independent
expected value.

**Result: 92/92 correct.** Category breakdown:

| Category | Cases | Measurement needed? | `analyze` role |
|---|---|---|---|
| SINGLE (single-part modeling: brackets, pipes, shafts, flanges, enclosures, primitives) | 37 | **No** | not used |
| FIT (pin-in-bore, bolt-through-hole, lid-in-frame, shaft-in-bearing) | 25 | **Yes** | `Interfere` = the fit checkpoint |
| POSITION (L-bracket centroid, alignment gap) | 15 | **Yes** | `CenterOfMass` / `Distance` |
| VERIFY (explicit collision / clearance query) | 15 | **Yes** | `analyze` *is* the task |

- **measurement needed: 55/92 (60%)** — every assembly/fit/verification case needs a spatial truth the
  operator cannot eyeball (does this pin seat? is there 0.5 mm clearance? where is the balance point?).
- **`analyze` called: 55/92 (60%)** and **actionable: 55/92 (60%)** — it fires exactly where needed and
  its answer changes the next design decision (resize the bore, shift the part, accept the fit).
- **0/37 single-part cases** needed it — modeling a lone part is verified by volume/validity (C-001), not
  by inter-part measurement.

**Verdict: KEEP, scoped to assembly / fit / verification.** `analyze` is dead weight for single-part
modeling but load-bearing for the fit/position/verify work that dominates real assembly design. It is
not a general-purpose measurement grab-bag and should not be advertised for single-part tasks; its value
is precisely the 60% of cases where two or more solids must be related. This matches the C-008 promote
rationale and is now backed by a realistic-use distribution rather than a single-feature probe.

**Harness bug found while building the sim (not a tool bug).** The sim interpolated dimensions into JSON
with C# string interpolation, which uses the **thread culture**. On the Italian-locale host, `3.3`
rendered as `"3,3"`, producing malformed JSON so `setattr(o,"Radius",…)` silently failed and the
cylinder kept its default radius 2 — corrupting every decimal-valued case (pin ⌀10.2 read as r=2, bolt
⌀6 in a ⌀6.6 hole read as a r=2 hole, etc.). Fixed by forcing `CurrentCulture = InvariantCulture` in
the sim. The tool itself was correct throughout (confirmed by direct `freecadcmd` probes on the exact
nominal geometry). Recorded because any future harness that builds JSON by interpolation must do the
same.

---

## Rejected / do-NOT-re-test

These were considered and rejected **before** implementation (or are anti-patterns). Recorded so
they are not re-explored.

### R-001 — Report sketch "closed" status on `Sketch` geometry-add — REJECTED (redundant)
Idea: after adding a rectangle/circle, report whether the sketch is closed (a precondition for a
valid pad). **Why rejected:** C-001 already surfaces the downstream truth — an open profile makes
the `Feature` result report `INVALID`. A separate sketch-closure flag adds a second, partial
signal (closure of arbitrary mixed geometry is itself non-trivial) for a failure the feature
read-back already catches at the point it matters. No new information where the agent acts.

### R-002 — `Feature` returns `Error:` when the shape is invalid — REJECTED (anti-pattern)
Idea: turn the invalid-feature warning into a hard `Error:`. **Why rejected:** the feature object
**is created** and is **fixable** (close the sketch, recompute). A hard `Error:` implies nothing
was made and pushes the agent to recreate, when the right move is to repair the profile. The
warning-string approach keeps the created object addressable while making the failure loud. This
matches the tool's existing pattern (`boolean` reports `valid=False` in a success string, not an
error).

### R-003 — Surface `volume` on `Transform` — REJECTED (noise in the common case)
Idea: report the new volume after a transform. **Why rejected:** `Transform` is dominated by
pure move/rotate, where volume is unchanged and irrelevant; the scale case is the minority. Adding
volume to every transform result is noise for the common case. (If a scale-confirmation is ever
needed, `inspect_object` already gives it.)

### R-004 — Overhang / DFM printability check (face normals vs build direction) — REJECTED (not reliably measurable from a thin client)
Idea (AgentsCAD, 2026): flag faces whose outward normal points against the build direction beyond a
45° threshold as FDM overhangs. **Why rejected — two independent blockers, both confirmed by
`freecadcmd` probes on 1.1.3:**
1. **The outward normal of a non-planar face cannot be determined reliably.** `face.normalAt(u,v)`
   returns the surface's natural normal; the `Orientation` flag does not fix the sign consistently
   (a box's x=0 face and its top face are both `Reversed` yet their raw normals have opposite
   outward-ness). The robust fallback — offset the face CoM by ±ε·n and test `Shape.isInside` — works
   for planar faces but returns **inside on both sides** for a conical slant (the ε step lands within
   tolerance on both), so the sign is arbitrary there.
2. **Even with a correct outward normal, the normal direction does not track FDM overhang.** A part's
   top face is up-facing yet fully supported; a widening cone's slant is up-facing yet genuinely
   overhangs; a traffic cone's slant is down-facing yet self-supporting. Overhang is a property of
   per-layer *support*, not of the surface normal. A correct check needs layer-wise support analysis,
   which is out of scope for a pure-RPC client with no mesh/slice pipeline.
A normal-only heuristic would mis-flag the build-plate face and invert on cones — worse than not having
it. If DFM is ever needed, it belongs in a dedicated slicer-backed tool, not here.

---

## Open hypotheses (not yet tested — queue)

- **H-next: face descriptors on `inspect_object` (optional).** The C-003 edge-descriptor idea applied
  to faces (ref/area/center/normal), to support face selection and any future "up to face" feature.
  Same opt-in + cap pattern. Not started.
- **H-next: `get_complex_part` rating.** The library exposes no rating; ranking falls back to size.
  If the library ever ships one, prefer it inside the tier. Blocked on upstream data.

---

## Env notes (rig setup, not tool behavior)

- **Harness bridge payload.** Running `FreeCADTool.Harness` from its own `bin` needs
  `bridge/` + `bridge_headless.py` in that output (the bootstrap resolves them next to the DLL).
  They are packed into the nupkg for production but are **not** copied into the harness output by
  the build. Copied manually for these runs. A future harness improvement: add a
  `CopyToOutputDirectory` for the bridge payload so the harness self-bootstraps.
- **WSL background teardown.** A `freecadcmd` bridge started in WSL via `wsl -e bash -c "... &"`
  is killed when the `wsl.exe` invocation exits (WSL session teardown); `setsid`/`nohup`/`disown`
  do not prevent it without a systemd unit. The full harness was therefore **not** run end-to-end
  against 0.20.2 in this environment; cross-version behavior of C-001/C-002 was established with
  targeted `freecadcmd` probes that exercise the exact kernel primitives the C# changes surface.
- **Probes used** (throwaway, removed after the cycle): open-pad, safe-read-back, cross-version pad,
  cross-version fillet, big-fillet, isValid-cost, and a `0.0.0.0`-bound WSL bridge launcher. The
  findings they produced are recorded above; the scripts themselves are not kept.
