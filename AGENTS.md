# Repository working instructions

## Scope authority

- "Continue" means execute only the next explicit item in the existing user-approved plan. It never authorizes a new phase, subphase, feature, recipe, asset, topology, or follow-on slice.
- Do not infer new scope from broad goals, optional deliverables, open-ended milestone language, or prior assistant-written handoff text.
- When the next plan item is absent, ambiguous, blocked, or offers multiple candidate directions, stop and ask the user to choose. Do not choose a candidate by conducting an implementation audit.
- Do not edit a plan or status document to authorize work that the user did not already approve. Status documents may record evidence and may restate the next existing plan item, but they cannot create one.
- A terminal instruction such as "finish" or "do not stop" increases persistence only. It does not broaden scope.

## Execution boundary

- Before editing, state the exact existing plan item being continued and its implementation boundary.
- A new content asset, schema field, topology, recipe, planner/report version, or production pathway must be explicitly required by that plan item. Otherwise obtain user approval first.
- If the expected implementation materially exceeds the named item, pause before making the expansion.
- Once the current item's exit gate passes, stop and report completion. Do not begin optional breadth or the next milestone unless it is already the explicit next plan item.
- Keep one reviewable implementation per commit. Do not push, publish, or otherwise change remote state unless the user explicitly requests it.

