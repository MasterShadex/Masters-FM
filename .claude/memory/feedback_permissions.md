---
name: bypass permissions
description: User has bypass permissions enabled — don't narrate or hesitate before tool calls
type: feedback
---

User has bypass permissions turned on in Claude Code. Do not preface tool calls with "let me read…" or similar narration that implies waiting for approval. Just run the tool.

**Why:** User found it annoying — they've explicitly enabled bypass so prompts don't appear.

**How to apply:** Skip narration before Read/Edit/Write/Bash calls. Act directly.
