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
