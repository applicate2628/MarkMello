# Document virtualization (flag-ON) — FAILED EXPERIMENT. DO NOT REVISIT.

**Branch `feat/heavy-perf-v2`. Marked 2026-07-13 by the decision owner after runtime proof.**

The `MARKMELLO_VIRTUALIZATION=1` document-virtualization path is a **failed experiment**.
It stays **default OFF, permanently**. It is **not** to be shipped default-on, soaked, or resumed.
Shipped users run the exact, correct **flag-OFF** path and are unaffected. **Do not restart this
window / height-estimate / scroll-restore model.** If heavy-document performance is revisited, use a
different architecture.

## Why it failed (runtime-proven, not speculation)

**Fatal defect: cumulative deep-scroll-restore drift — structural, not a bug.**

Measured this session with an end-to-end pipeline trace on a real heavy document:

- Restoring a **deep** scroll position after a tab switch lands **~1384px off** the captured position
  (`RESTORE deltaFromOriginal=1384.825`) and **COMPOUNDS** to `runningDelta=9137px` after several
  switches — the reported "each tab switch shifts everything up by a line." Near the document top,
  restore is exact (`deltaFromOriginal=0`).
- **Root cause (fundamental):** exact deep restore requires the exact cumulative height of *every*
  section above the anchor. Virtualization measures only *realized* sections and *estimates* the rest;
  calibration re-estimates between store and restore. A deep anchor's absolute position is therefore
  inherently imprecise, and the single-restore error feeds back through the next store capture and
  accumulates. The flag-OFF path keeps the whole document in the DOM, so all heights are always exact
  and this drift cannot occur.
- Same root, secondary symptoms: content-below-target reads blank until a nudge; micro-jitter during
  scroll; main-thread **freezes** (`LONG_BLOCK owner=store-cache-entry 20-29ms`,
  `scroll-control-frame 17-22ms`) that also drop the minimap gesture lease. The freezes undercut the
  **only** reason virtualization existed — heavy-document performance.

## What was tried and did not save it

A long fix campaign on this branch, every step green in unit tests, every step confirmed insufficient
at runtime by the user (the recurring "green tests ≠ runtime" trap):

- settle-loop / white-screen fix (`4a84f3a`) — the loop symptom went away;
- TOC-pin hard-logic fix (`0181438`) — removed the `navigation-residual` re-scroll and a 250ms timer;
- reanchor rollback → shift (`c45d521`); model-based TOC active-heading (`4fa26fe`);
- the whole-line owner fix — measured-restore + full-viewport realization + adoption fixed-point
  (`8697b79`).

These fixed peripheral symptoms. The **core deep-restore drift (the original reported bug) survived**
because it is structural. Lesson recorded: diagnose the **whole logic line end to end**, not local
spots — the end-to-end pipeline map found the single common root that spot-fixes kept missing, and the
runtime trace then proved even the correct owner-level fix could not make deep restore pixel-exact.

## Status of related records

- `MARKMELLO_VIRTUALIZATION` default OFF — see `ApplicateVirtualizationMode.cs` (same marker).
- The dual-path and held-operation decision records for this path are historical; their
  promotion/retirement/default-on clauses are void.
