---
title: Testing the AI Agents
navTitle: AI Agent Tests
position: 7
---

# Testing the AI Agents

The tests of the [AI agents](../administration/ai-agents.md) live in `HomeBlaze.AI.Tests`, and the agent page is covered end to end in `HomeBlaze.E2E.Tests`. They run both backends against fakes: a fake `claude` script that replays recorded stream-json output, and a fake chat client or HTTP handler in place of the Anthropic API. The fake CLI is a shell script, so the tests that start it run only on Linux and macOS.

Two opt-in integration tests run one real turn each and are skipped otherwise, because they spend the Claude subscription or API credits. Both are in the `Integration` category:

| Test | Runs when |
|---|---|
| Claude CLI | `HOMEBLAZE_RUN_CLAUDE_CLI_TESTS` is `1` and `claude` is on the `PATH` and logged in |
| Anthropic API | `HOMEBLAZE_RUN_ANTHROPIC_API_TESTS` is `1` and `ANTHROPIC_API_KEY` holds an API key |

```bash
HOMEBLAZE_RUN_CLAUDE_CLI_TESTS=1 dotnet test src/HomeBlaze/HomeBlaze.AI.Tests --filter "Category=Integration"
```
