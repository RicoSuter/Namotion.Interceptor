---
name: dev-planner
description: Quality checklist for development plans. Adds subtasks for tests, docs, code quality, dependencies, and performance to each planned task.
---

# Quality Checklist

Apply to each task. Ask the user if unsure whether a check applies.

## Tests
- Minimal, pragmatic tests covering the change
- AAA syntax (Arrange, Act, Assert), match existing test style
- Readable: a new dev understands at a glance
- Prioritize: critical paths > edge cases > exhaustive coverage

## Documentation
- Update relevant docs (README, API, inline comments, architecture)
- If public API changes: update examples and usage docs
- Flag missing or stale docs

## Code Quality
- No duplication - consolidate similar patterns
- DRY - extract repeated logic into shared utilities
- SOLID - especially Single Responsibility and Dependency Inversion
- Consistent naming and style with existing codebase
- Remove dead code if encountered

## Dependencies
- Inform user explicitly of any additions/removals/updates
- Note version constraints or compatibility concerns
- Flag heavy or security-concerning dependencies

## Performance
- Prefer performant variant when complexity is similar
- Don't over-engineer for marginal gains
- Ask user if significant performance gain requires notably more code
- Note O(n^2) or expensive operations

## Alternatives
- Ask user before assuming a simpler approach
- Present alternative implementations when tradeoffs exist
- Flag over-engineering or unnecessary abstraction

---

# Additional Checks (When Relevant)

## Security
- Input validation on public API boundaries
- Permission and authorization handling
- Auth implications (OAuth, tokens, credentials)
- No secrets in code; use env vars or secret managers
- Secure defaults (fail closed, not open)
- Thread safety for shared state
- Proper disposal of sensitive data

## Error Handling
- Graceful failure modes
- Meaningful error messages (for users and logs)
- Logging at appropriate levels

## Breaking Changes
- Flag API or schema changes
- Note downstream consumers affected
- Suggest deprecation path if applicable

## Migration
- Include migration steps if data migration needed

## Edge Cases
- Empty states, null values, concurrent access
- Rate limits, timeouts, network failures
