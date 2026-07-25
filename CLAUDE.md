<!--
Canonical project instructions live in AGENTS.md so that Claude Code, Codex and
GitHub Copilot all read the same content.

  Codex   reads AGENTS.md directly. It does NOT read CLAUDE.md.
  Copilot reads AGENTS.md directly (nearest file in the tree wins).
  Claude Code reads CLAUDE.md only, so it imports AGENTS.md below.
  See https://code.claude.com/docs/en/memory (AGENTS.md section).

Keep AGENTS.md fully self-contained: Codex does not support Claude's import
syntax, so anything pulled in by reference would be invisible to it.

Put shared rules in AGENTS.md. Put Claude-Code-only rules under the import below.

This comment block is stripped before the file enters Claude's context, so it
costs no tokens.
-->

@AGENTS.md
