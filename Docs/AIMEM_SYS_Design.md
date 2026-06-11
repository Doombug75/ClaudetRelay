# AIMEM.SYS — Persistent AI Memory System
**Status:** Design / Pre-implementation
**Author:** Robert + Claude

---

## Overview

AIMEM.SYS is a planned persistent memory layer for AI participants in ClaudetRelay.
The goal is to allow participants to carry meaningful knowledge across sessions and
project boundaries — without relying on context-window history alone.

---

## Core Concept

Each participant gets a personal memory file (e.g. `AIMEM_Gemma.sys`) that is
injected into their system prompt at session start. The participant can read and
write entries via special tags, similar to the existing `<output>` / `<readfile>` system.

---

## Emoji Handling — Critical Design Rule

Chat output (the primary source of memory content) is rich with emoji.
**Do NOT strip emoji from memory entries** — stripping loses semantic content.

Instead, normalize emoji to descriptive text labels at write-time:

| Emoji | Normalized form |
|-------|----------------|
| ⚠ | `[warning]` |
| ✓ / ✔ | `[done]` |
| 🗜 | `[compression]` |
| ℹ | `[info]` |
| ✕ / ❌ | `[error]` |
| 🔄 | `[update]` |
| 📂 | `[file]` |
| Decorative header emoji (🏞️ 🌳 🚀 etc.) | *(strip — no semantic value)* |

**Why this matters:** Weak local models (e.g. Gemma) can loop or hang silently when
reading emoji back from injected context. This was demonstrated concretely: a
Gemma-authored `.md` file decorated with emoji caused a 20+ minute silent stream hang
when Gemma read it back. The fix was stripping emoji in `ContentFilter.cs` on read —
but memory entries need the *meaning* preserved, not just the character removed.

The distinction:
- *Status / meaning emoji* → normalize to `[label]`
- *Decorative / structural emoji* → strip entirely

---

## Related Systems

- `Services/ContentFilter.cs` — strips emoji from project files on read (injected
  context). Files are kept intact on disk; stripping is only for model injection.
- `MainWindow.Compression.cs` — context compression pipeline. Memory entries should
  survive compression passes; consider tagging them with a sentinel sender so the
  compression engine treats them as system context rather than compressible conversation.
- Context window injection follows the same pattern as `BuildInputFilesContext` —
  memory is appended to the participant's system prompt string.

---

## Thematic Memory Segmentation

Memory entries must be split by topic, not just by time. When the conversation
shifts to a new theme, that becomes its own memory segment — not appended to the
previous one. Segments carry a timestamp so the participant knows the temporal
relationship between them, but are kept thematically isolated so unrelated knowledge
doesn't bleed together.

Example — two separate segments, not one merged blob:
```
[2026-06-11] Markfleck World Bible
  - Town located behind southern mountain range ...
  - Yorrick is a 1000-year-old mage disguised as a bard ...

[2026-06-11] ClaudetRelay Compression System
  - Rolling chunk compression, 25% of manager context per chunk ...
  - EMA-calibrated chars-per-token ratio per participant ...
```

### Topic Change Detection

The compression engine (or a lightweight pre-pass) needs to detect theme boundaries.
Candidate signals:
- **Explicit user pivot** — "now let's talk about X", new project loaded, session restart
- **Semantic distance** — embedding similarity drop between adjacent message clusters
  (heavier, requires an embedding model or cloud call)
- **Keyword/entity shift** — named entities in the last N messages differ significantly
  from the prior N (lighter, no model call needed)
- **Time gap** — long pause between messages can signal a topic shift

For V1, explicit pivots + time gaps are probably sufficient. Semantic detection can
come later as an optional enhancement.

### Compression Engine Reuse

The existing `CompressionEngine` rolling-chunk pipeline is a natural fit for
condensing each thematic segment into a memory entry. The manager participant
compresses the segment the same way it compresses context — same 20% ratio,
same rolling context, same natural-boundary splitting. The result is stored as
a dated, titled memory block rather than back into `_sharedHistory`.

## Direct Access vs. Text Tag Parsing

Relying on models to write memory via text output tags (like `<aimem>`) parsed by
ClaudetRelay has a fundamental reliability problem: the model output goes through
the Ollama/Cloud HTTP stream, which can hang, time out, or be corrupted. A weak
model may also forget the tag syntax, hallucinate the format, or produce malformed
output that fails to parse.

A more robust approach for V2+: expose memory read/write as **MCP tools** or a
dedicated local API endpoint that the model calls directly, rather than embedding
commands in free-text output. This is similar to how function calling works in
OpenAI-compatible APIs — the model signals intent structurally, not textually.

For V1, text tags are acceptable as a starting point (same pattern as `<output>`,
`<readfile>` etc. which already work). But the architecture should be designed
with tool-based access as the upgrade path.

---

## Open Design Questions

1. **Storage format:** One file per participant, or one shared file with per-participant
   sections? Per-participant is simpler and avoids merge conflicts in parallel work.
2. **Write tags:** `<aimem>remember: ...</aimem>` ? Or structured key/value?
3. **Read injection:** Full file auto-injected (like small INPUT files), or summarized
   above a size threshold?
4. **Memory lifespan:** Should old entries expire, or accumulate indefinitely?
   Could reuse the compression engine to periodically condense memory files.
5. **Scope:** Per-project memory vs. global participant memory vs. both?
6. **User visibility:** Should the user be able to read/edit/delete memory entries
   via a UI panel?
