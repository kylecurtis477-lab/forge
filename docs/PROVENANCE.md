# AI-provenance commit trailers

From the commit that added `tools/provenance.forge` on, every commit says who wrote it. The last paragraph of the
commit message is a trailer block (only `Key: value` lines) with:

- `Provenance: human`: written by a person (tools such as the formatter don't count).
- `Provenance: ai-assisted`: a person wrote it with AI help (suggestions, completions, edits they reviewed).
- `Provenance: ai-generated`: an AI agent wrote it; a person set the task and decides whether it lands.
- `AI-Tool: NAME`: required for both AI kinds, the agent or assistant that produced the change (for example
  `AI-Tool: Grok Bot`). Optional extra trailers such as `Reviewed-by:` are allowed.

Exactly one `Provenance:` line, with one of the three values. Trailers anywhere else in the message (in the
subject, or in a paragraph followed by more prose) do not count.

Example:

    feat(lsp): incremental re-check of changed files

    Why and how, in prose.

    Provenance: ai-generated
    AI-Tool: Grok Bot

Check: `forge provenance` runs tools/provenance.forge over `git log` from the commit that added the tool to HEAD
(or over `forge provenance RANGE`), prints one line per commit without valid trailers and exits 1 if there is
any. `build/provenance msg FILE` checks a single message, so it can serve as a `commit-msg` hook.
tests/provenance/run.sh runs in run_tests.sh.

This is a v1 reservation (docs/BUILD_PLAN.md, "Current execution order"): the record exists from now on, so a
later release can report or audit AI-written code without guessing at history.
