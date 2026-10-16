# Domain Docs

How the engineering skills should consume this project's domain documentation when exploring the codebase.

## Before exploring, read these

- **The glossary**: `Book Of Holding/Projects/Balamb Garden/Knowledge/Process/Balamb Garden - Glossary.md` in the maintainer's vault (one glossary per project; there is no `GLOSSARY.md` in this repo).
- **The rulings (ADRs)**: a numbered ruling in the live design doc on that topic (`Book Of Holding/Projects/Balamb Garden/<Topic> - Design.md`, Rulings section), or a standalone node `Book Of Holding/Projects/Balamb Garden/Knowledge/Process/Balamb Garden - Ruling - *.md`. There is no `docs/adr/` in this repo. The Process hub (`Knowledge/Balamb Garden - Process Hub.md`) indexes both under "Rulings and vocabulary".

If any of these files don't exist, **proceed silently**. Don't flag their absence; don't suggest creating them upfront. The `/domain-modeling` skill (reached via `/grill-with-docs` and `/improve-codebase-architecture`) creates them lazily when terms or decisions actually get resolved.

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test name), use the term as defined in the glossary. Don't drift to synonyms the glossary explicitly avoids.

If the concept you need isn't in the glossary yet, that's a signal: either you're inventing language the project doesn't use (reconsider) or there's a real gap (note it for `/domain-modeling`).

## Flag ruling conflicts

If your output contradicts an existing ruling, surface it explicitly rather than silently overriding:

> _Contradicts the Household Sync design, ruling 10 (never whose), but worth reopening because…_
