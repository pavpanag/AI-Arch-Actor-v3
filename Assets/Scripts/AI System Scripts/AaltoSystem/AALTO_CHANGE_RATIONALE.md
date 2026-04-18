# Pre-Aalto to Aalto Change Rationale (Mini)

## Context
- `System v2` is the stable legacy flow and should remain unchanged.
- `AaltoSystem` is the evolving new flow for Aalto rehearsal/research needs.
- Main immediate blocker was duplicate global C# types causing Unity compile failures.

## Key Decisions Made
- Keep `System v2` source files intact.
- Upgrade only `AaltoSystem` scripts in this session.
- Differentiate Aalto runtime types via namespace `AaltoSystemV3`.
- Add Aalto-only observability upgrades without changing existing JSONL schema.

## Pre-Aalto -> Aalto Delta (Mini)
- Pre-Aalto state:
  - Stable `System v2` color-driven flow.
  - Existing OpenAI request/response tracing worked, but inspection + replay workflow was limited.
  - Duplicate global class names between old/new systems caused compile conflicts.
- Aalto direction agreed in this session:
  - Keep legacy untouched.
  - Build a new, separable Aalto runtime with stronger observability and replayability first.
  - Move in strict phases: inspect -> reason dry-run -> execute bridge -> replay -> live inputs.
- Key decisions from work with Tanja:
  - First half of work is systems quality (`observability`, `controllability`, `replayability`), not model tuning.
  - Prefer additive changes and strict contracts over architectural rewrites.
  - Keep a dry-run path available at every phase.

## Notes Data (Structured)
```json
{
  "naming": {
    "legacy": "System v2",
    "new": "AaltoSystem",
    "new_namespace": "AaltoSystemV3"
  },
  "constraints": [
    "Do not modify System v2 behavior",
    "Additive changes only unless unavoidable",
    "Keep JSONL trace compatibility"
  ],
  "priorities": [
    "observability",
    "controllability",
    "replayability"
  ],
  "phase_policy": {
    "incremental": true,
    "checkpoint_gated": true,
    "dry_run_mandatory": true
  }
}
```

## What Changed First (Phase 1.1 baseline)
- Added `AaltoSystem.asmdef` to isolate Aalto code as its own assembly.
- Wrapped Aalto C# scripts in `AaltoSystemV3` namespace to avoid collisions.
- Upgraded Aalto `ChatTraceLogger` with:
  - latest request file: `latest_request.json`
  - latest response file: `latest_response.json`
  - public path/session accessors
  - reveal helper methods
  - optional events for request/response/session-save hooks
- Upgraded Aalto `OpenAIClient` with last-call memory:
  - `LastRawRequestJson`
  - `LastRawResponseJson`
  - `LastAssistantContent`
  - `LastContextTag`
  - `LastResponseCode`
- Updated Aalto `ChatTraceControls` with reveal helpers for folder and latest files.

## New Milestone (Task 2.1 started)
- Added `AaltoChatController` as a separate controller from legacy color flow.
- New controller focuses on Aalto runtime outputs:
  - `updated_objective`
  - `updated_stance`
  - `intended_action`
  - `selected_action_label`
  - `reason`
- No light execution path in controller logic (dry-run-first behavior).
- Added mock JSON support (`UseMockResponse`) for fast turn testing without live model calls.
- Each successful turn logs a scenic event through `ScenicEventLogger`.

## New Milestone (Task 2.2)
- Added strict Aalto JSON parsing/validation in `AaltoChatController`.
- Required fields are enforced:
  - `updated_objective`
  - `updated_stance`
  - `intended_action`
  - `selected_action_label`
  - `reason`
- Malformed or incomplete responses now produce readable runtime errors.
- Added unknown-key handling:
  - optional warning mode
  - optional reject mode (`RejectUnknownJsonKeys`).

## New Milestone (Task 2.3)
- Added bounded stance set support in `AaltoChatController`.
- `updated_stance` is now validated against allowed stance values (case-insensitive).
- If model stance is out-of-range, controller falls back deterministically:
  - first: current valid stance
  - second: configured fallback stance
  - third: first allowed stance
- Runtime warning is printed when fallback is applied.

## New Milestone (Task 2.4)
- Added configurable action-label set enforcement in `AaltoChatController`.
- `selected_action_label` is validated against configured labels (case-insensitive, canonicalized output).
- If out-of-range, a deterministic fallback label is applied with a runtime warning.
- Added inspector context-menu batch runner (`Aalto/Run Mock Scenario Batch`) using multiline mock input lines.
- This supports fast dry-run study of selected labels and intended actions without light execution.

## New Milestone (Task 3.1)
- Added simplified `AaltoInterviewController` for Aalto mode (separate from legacy interview flow).
- Focused inputs: backstory + motive.
- Output: one short runtime `character_summary` (strictly validated and length-bounded).
- Added persistence to `latest_alto_character_profile.json` and load/apply flow to `AaltoChatController`.
- Supports mock-response mode for rapid dry-run iteration.

## New Milestone (Task 3.2)
- Kept existing `DirectingNotesStore` infrastructure.
- Added optional Aalto override parsing:
  - `objective_override`
  - `stance_override`
- `AaltoChatController` now applies overrides at runtime (with stance bounds/fallback safety).

## New Milestone (Task 5.2)
- Added prompt inspection helpers to `AaltoChatController`.
- Captures latest:
  - system prompt
  - user context
  - assistant JSON response
- Added context-menu actions:
  - copy system prompt
  - copy user context
  - copy assistant JSON
  - export latest prompt packet to file
  - reveal exported packet path
- Added optional on-screen prompt inspection text target (`PromptInspectionText`).

## New Milestone (Task 5.1)
- Upgraded Aalto system prompt design to define core semantics explicitly:
  - `objective` definition
  - `stance` definition
- Added strict bounded-action behavior language:
  - do not invent new scenic actions/labels
  - choose nearest matching allowed label when no perfect fit exists
- Preserved strict output schema and bounded stance/action constraints.

## New Milestone (Task 6.1)
- Added `AaltoScenarioTestHarness` with predefined canned scenarios:
  - fear
  - leaving
  - surrender
  - confusion
  - outside-world insistence
- Each scenario includes:
  - character summary
  - initial objective
  - initial stance
  - dialogue seed/latest move
  - expected reasonable labels
- Harness logs selected labels plus objective/stance outcomes and emits pass/warn report.

## New Milestone (Task 7.1)
- Added `ScenicReplayRunner` to read scenic JSONL logs and replay event sequence by turn order.
- Replay resolves trigger using:
  - `resolvedMemoryLabel` (preferred)
  - `resolvedMemoryId` -> `memory N`
  - fallback `selectedActionLabel` for analytical dry replay
- Supports dry-run mode and optional trigger dispatch via `SendMessage` bridge.

## New Milestone (Task 7.2)
- Replay runner now displays textual trace per replayed turn:
  - `latest_move`
  - `objective`
  - `stance`
  - `intended_action`
  - `selected_action_label`
  - `reason`
- Added optional TMP outputs for replay status and cumulative replay trace text.

## New Milestone (Task 4.1)
- Added `AaltoActionMemoryRegistry` to map `selected_action_label` -> memory trigger string.
- Added default mapping table for initial action-label set.
- `AaltoChatController` now resolves memory trigger from selected label each turn.

## New Milestone (Task 4.2)
- Added `AaltoOscSender` UDP bridge for Python light-controller triggering.
- `AaltoChatController` can now send resolved memory triggers through OSC bridge when enabled.
- Sender now emits proper OSC packets (string arg) and defaults to port `4444` to match `lights controller v15.py`.

## New Milestone (Task 4.3)
- `AaltoChatController.DryRunOnly` now gates execution bridge behavior:
  - `true`: full reasoning + logging, no OSC send
  - `false`: resolved trigger is sent via `AaltoOscSender`
- Scenic event logs now include resolved memory id/label and execution success/result.

## Notes Captured (from work with Tanja)
- Prioritize observability, controllability, replayability before model complexity.
- Phase order should be incremental and testable per checkpoint.
- Do not rewrite networking/logging architecture; prefer additive hooks.
- Keep dry-run capability while evolving toward scenic action execution.

## Critical Checkpoints
- A: Exact prompt/response is easy to inspect.
- B: Aalto controller can dry-run and log without lights.
- C: Output schema is valid and bounded.
- D: Label resolves to memory trigger reliably.
- E: Replay with text is possible and interpretable.
