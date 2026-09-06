# ADR 0001: Machine cycle timing

A machine's `Tick()` checks for a possible start *before* advancing an
in-progress cycle, and both happen within the same tick call when a start
occurs. This makes a cycle take exactly `duration_ticks` ticks end-to-end
(start and finish are duration_ticks apart), rather than `duration_ticks + 1`.
It also means a machine that finishes a cycle on tick N does not attempt to
start the next cycle until tick N+1, even if inputs are available — this
keeps throughput tests exact and easy to reason about (`ticks / duration`
completed cycles, no off-by-one) instead of depending on same-tick chaining.
