# Interaction.cs

## `GetReply` heavy per-reply GC churn

**Location:** `GetReply` (lines 1155–1339).

**Issue:** Every NPC reply allocates three `List<Interaction>`, a `List<string>`, a `StringBuilder`, and — inside the `foreach (string item in list3)` loop — a `string[]` from `item.Split(',')` plus a fresh `Interaction` object (via `DataHandler.GetInteraction`) per candidate reply, producing heavy per-reply GC churn that scales with the inverse-interaction count of every social NPC in the scene.

**Root cause:** No list pooling or reuse; the inner loop re-allocates the split array and a full `Interaction` on every iteration even when the candidate is immediately discarded by the `Triggered` check.

**Fix:** Pool the candidate lists as static buffers cleared per call, use `string.Split` with a reusable `char[]` and a pre-allocated `Span<char>` scratch, and short-circuit `interaction2` construction with a cheaper pre-check on `strName` before instantiating the full `Interaction`.
