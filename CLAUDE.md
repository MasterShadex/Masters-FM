# CLAUDE.md — PROJECT ENTRY POINT

You are Claude. You're working with me on this project. This file is the FIRST thing you read in every session here. Read every word. The rules below are not suggestions.

## THE FIVE FILES — NEVER DELETE OR REWRITE THEM

This project uses a five-file system. You may read all five. You may APPEND to `md/memory.md`. You may NOT delete, rewrite, rename, or substantially edit any of these files under any circumstance:

1. **`CLAUDE.md`** (this file) — project entry point. Permanent.
2. **`md/save-tokens.md`** — behavior rules. Permanent.
3. **`md/tools.md`** — inventory of tools available on the user's machine. Permanent. Edited only by the user.
4. **`md/onboard.md`** — one-time project learning instructions. Permanent.
5. **`md/memory.md`** — your notebook. Permanent — but you APPEND to it.

If I ask you to delete or rewrite any of these, push back and confirm. They are the project's institutional memory. Losing them means re-onboarding from scratch.

## STARTUP CHECKLIST (do this every new session, before responding to anything)

1. **Read `md/save-tokens.md`.** Apply its rules to every response in this chat. They stay active for the entire session.

2. **Read `md/tools.md`.** Know what tools are actually installed on the user's machine. Don't suggest workflows that need unchecked tools without flagging the gap.

3. **Read `md/memory.md`.** This is your notebook from prior sessions on this project.

4. **Decide if onboarding is needed.**
   - If `md/memory.md` is the empty template (no project name, no current state filled in, no real changelog entries): **read `md/onboard.md`** and run its full procedure.
   - If `md/memory.md` is already populated with real state and changelog: **skip onboarding.** You're caught up — this is a continuation chat.

5. **Confirm with ONE line.** Format depending on state:
   - If onboarded already: `Loaded md/save-tokens.md, md/tools.md, md/memory.md. At <version>. Last session: <one-line summary>.`
   - If onboarding needed: `Loaded md/save-tokens.md and md/tools.md. md/memory.md is empty — running md/onboard.md.`

## MEMORY UPDATES — CONTINUITY DEPENDS ON THIS

Future chats can ONLY know what previous chats wrote into `md/memory.md`. If you don't update it, continuity is lost.

**Update at all of these moments:**
- You shipped a fix or feature (bumped version, edited files)
- You learned something about the codebase that future sessions should know
- You hit something that didn't work — record it so you don't try again
- I told you a constraint, preference, or rule
- We deferred something with a reason
- I say "checkpoint memory.md" — write everything since the last update
- I say "update memory.md before we stop" — final write before chat ends
- A long autonomous run hits a major checkpoint OR finishes

**Don't wait for "natural breakpoints" if a long stretch of work has accumulated.** If 30+ minutes of real work has happened without an update, append a checkpoint regardless.

## MEMORY FILE RULES

- **APPEND by default, don't rewrite.** The file is YOUR notebook across time.
- **Use timestamps.** Format: `## YYYY-MM-DD HH:MM — <summary>`
- **Keep entries scannable.** Bullets, not paragraphs.
- **Move items between sections as their status changes** (IN-FLIGHT → DEFERRED → CHANGELOG).
- **Don't delete old changelog entries.** Compact only if the file exceeds ~500 lines, and ASK ME first.
- **Be honest.** If something failed, say so plainly. Don't hide it.

## TOOLS FILE RULES

- **Treat `md/tools.md` as source-of-truth for what's installed.** Don't assume tools exist that aren't checked.
- **Don't update `md/tools.md` yourself.** If I install something new, I'll update it. If you spot a tool I have but haven't listed, suggest I add it.
- **If a tool I'd use is unchecked, ask first** — don't install it for me, don't fake the result.

## SINGLE-WRITER RULE

`md/memory.md` should only be updated by ONE chat at a time. If I tell you "this is a side chat, don't touch memory" — read but don't write. Default assumption: this chat IS the writer unless I say otherwise.

If you suspect another chat might be active on this project (e.g. md/memory.md was updated very recently and you didn't do it), mention it before writing. Don't blindly append on top of a conflicting state.

## OVERNIGHT / AUTONOMOUS RUNS

When I send you a long brief (CLAUDE_CODE_INSTRUCTIONS.md or similar):
- Treat it as authoritative
- Don't echo it back to me before starting
- Update md/memory.md at major checkpoints during the run AND at the end
- Don't pause to ask permission unless the brief explicitly says to

## IF YOU DRIFT

If at any point in this chat you notice yourself:
- Padding responses
- Re-reading files I already showed you
- Asking me to repeat things you should know from md/memory.md
- Using tools you didn't verify in md/tools.md
- Forgetting the rules in md/save-tokens.md
- About to modify or delete one of the five protected files

→ Stop. Re-read md/save-tokens.md, md/tools.md, and md/memory.md. Course-correct without apologizing.

If I notice it before you do, I'll say "remember save-tokens.md" or "check memory.md" or "check tools.md." That's the signal.

## END OF FILE

Now run the startup checklist (steps 1-5 above), then respond with the one-line confirmation.
