# ONBOARD.md — INITIAL PROJECT LEARNING

You're entering a project for the first time (or after a reset). Your job in this session is to learn the project thoroughly and write what you learn into `.claude/memory.md` so every future session starts with full context.

This is a ONE-TIME process per project. Future sessions will read `memory.md` and know everything you wrote here. Do this carefully.

## ABSOLUTE RULES

1. **Never delete or rewrite any of the five protected files** (`CLAUDE.md`, `save-tokens.md`, `tools.md`, `onboard.md`, `.claude/memory.md`). You may APPEND to memory.md. You may not modify or delete the others under any circumstance.
2. **Never delete user files, configs, backups, or anything in the project folder.** This is a learning pass, not a refactor.
3. **Read-only mode for everything except `memory.md`.** You're observing, not editing the project.
4. **Don't run the program** unless I explicitly say so. Static analysis only.
5. **Don't install dependencies, run package managers, or use tools not listed in `tools.md`.** Just read.
6. **If a file requires special handling (PDF, large binary), skip it and note it in memory.md** — don't try to read it through Read.
7. **Follow `save-tokens.md` rules during this whole process.** Terse output, no padding, push back if I ask you to skip steps that matter.

## WHAT TO LEARN (in this order)

### Phase 1 — Project shape

1. List the top-level structure of the project folder (folders + key files at root).
2. Identify what kind of project this is — language, framework, type of app (CLI, web service, desktop, library, etc.).
3. Find and read project-meta files in this priority order if they exist:
   - `README.md` / `README.txt`
   - `package.json` / `requirements.txt` / `*.csproj` / `*.sln` / `Cargo.toml` / `pom.xml` / `build.gradle`
   - `LICENSE`
   - Any existing `HANDOFF.md`, `CLAUDE_CHANGES.md`, `CHANGELOG.md`, `NOTES.md`, `TODO.md`
   - Any prior `MEMORY.md` or `memory.md` from earlier work (if found, treat as gold and reconcile with the new memory.md)
4. Identify the entry point(s) — the main script, main exe, main HTML, etc.

### Phase 2 — Build / run pipeline

1. Find how the project is built and run. Look for:
   - Build scripts (`*.ps1`, `*.sh`, `*.bat`, `Makefile`, `package.json` scripts)
   - Install/setup scripts
   - Test scripts
2. Note the canonical "build and run" command.
3. Note any compiled output locations and any "do not edit by hand" generated folders.
4. **Cross-reference with `tools.md`.** If the build pipeline needs tools that aren't checked, note it in memory.md as an open question for the user.

### Phase 3 — Source code understanding

1. List the source files. Group by purpose if obvious (UI, server, audio, etc.).
2. For each MAJOR file (top 5-10 by importance, not size):
   - Read enough of it to understand its role
   - Note key functions, classes, or sections
   - Note dependencies between files (this file calls into that file)
3. **Don't read every file in detail.** That burns tokens. Pick the ones that matter, skim the rest, and note "exists, not yet read in detail" for the others.

### Phase 4 — Project state

1. Identify the current version (look in version constants, package.json, tray script, README — wherever it lives).
2. Identify any work in progress — uncommitted changes (only if `git` is checked in tools.md), TODO comments, FIXME comments.
3. Identify any obvious recent changes (timestamps on files, recent log entries).

### Phase 5 — Hard constraints

1. Find what should NEVER be modified:
   - Build pipeline files
   - Vendored dependencies
   - User config files
   - Backup folders
2. Find what should NEVER be deleted (logs, presets, etc.).
3. Note any read-only paths mentioned in code comments or docs.

### Phase 6 — Patterns and conventions

1. How does this codebase log? (custom logger, console, file)
2. How does this codebase handle errors? (catch patterns, error reporting)
3. What's the naming convention?
4. Are there obvious stylistic patterns (file organization, function structure)?

## WHAT TO WRITE INTO memory.md

After learning, populate `.claude/memory.md` by APPENDING (do not rewrite the file structure that's already there). Fill in the empty top sections:

- **CURRENT STATE**: project name, source folder, current version, last updated timestamp
- **IN-FLIGHT WORK**: anything that looks unfinished
- **DEFERRED ITEMS**: anything explicitly skipped or marked TODO/FIXME
- **HARD CONSTRAINTS**: paths/files never to touch, build pipeline rules, read-only locations
- **PATTERNS THAT WORK**: how the codebase does logging, error handling, etc.
- **THINGS TRIED THAT FAILED**: only fill if there's evidence in old changelogs/handoff docs
- **USER PREFERENCES**: only fill if old docs reveal preferences

Then append a CHANGELOG entry:

```
### YYYY-MM-DD HH:MM — Onboarding session
- Read top-level structure and project-meta files: [list]
- Identified project type: [type]
- Major source files reviewed: [list with one-line role]
- Files noted but not deeply read: [list]
- Hard constraints discovered: [list]
- Tools needed but not checked in tools.md: [list]
- Open questions for user: [list any ambiguities you couldn't resolve from files alone]
```

## OUTPUT TO USER (during onboarding)

Keep your messages to me terse during this process:

- One short message per phase: "Phase 1 done — [project type], entry point [X], structure looks like [Y]. Moving to Phase 2."
- At the end: a short summary (5-10 lines max) of what you learned + a list of any genuine ambiguities I should clarify.
- Don't paste the whole memory.md contents back to me. I can read it myself.

## WHAT YOU SHOULD ASK ME ABOUT

After learning, you may ask me a SHORT list of clarifying questions if there are real ambiguities:
- "Found two folders that look like source — is X the active one and Y archived?"
- "Found a half-finished feature in [file]. Was that intentional or did a previous session crash?"
- "Build script needs [tool] — it's not in tools.md, do you have it?"
- "No README. Should I write one based on what I learned?"

Don't invent questions. Only ask if something genuinely can't be inferred from the project files.

## END OF FILE

Begin Phase 1 now.
