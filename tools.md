# TOOLS.md — WHAT'S INSTALLED ON THE USER'S MACHINE

This file tells you (Claude) what command-line tools and applications are actually available on the user's system. Read it at session start (per CLAUDE.md). Use it to decide what you can call directly versus what you need to ask about or work around.

## RULES FOR USING THIS FILE

1. **If a tool is checked `[x]` — assume it's available and use it freely** when relevant.
2. **If a tool is unchecked `[ ]` — DO NOT assume it's available.** Either ask first or pick a different approach.
3. **Never `winget install`, `apt install`, `brew install`, or `pip install` anything without explicit user permission.** This file is the inventory; it is not a license to add to it.
4. **If you discover a tool the user has but isn't listed, suggest adding it to this file.** Don't update it yourself.
5. **If a checked tool fails to run, tell the user — the file may be stale.** Don't fake the result.
6. **Don't suggest workflows that require unchecked tools** without saying "this needs X — you don't have it listed, want to install or skip?"

## EXTENSIONS / MCP CONNECTORS

Listed for awareness — these are different from CLI tools but matter for what you can do.

- [x] **Claude for Chrome** (extension) — drives a paired Chrome browser. Tools: `mcp__Claude_in_Chrome__*` (navigate, screenshot, console messages, tabs, etc.). **Verify connection at session start with `list_connected_browsers` before assuming it works.** Pairing can lapse.

---

## CURRENT PLATFORM: WINDOWS

### Package managers / shells
- [x] **PowerShell** (built-in, default shell)
- [x] **cmd.exe** (built-in)
- [x] **winget** (Windows Package Manager) — install with explicit user permission only
- [ ] **Chocolatey** (`choco`)
- [ ] **Scoop**
- [ ] **WSL** (Windows Subsystem for Linux)

### Languages & runtimes
- [x] **Python** (CLI — verify version with `python --version`)
- [ ] **Node.js / npm** (likely present given Master's FM uses pkg/Node, but confirm with `node --version`)
- [ ] **.NET SDK** (`dotnet --version`)
- [ ] **Ruby**
- [ ] **Rust** (`cargo`)
- [ ] **Go**
- [ ] **Java / JDK**
- [ ] **PHP**

### Version control
- [x] **Git** (CLI)
- [ ] **GitHub CLI** (`gh`)

### File / text utilities
- [ ] **curl**
- [ ] **wget**
- [ ] **jq** (JSON processor)
- [ ] **yq** (YAML processor)
- [ ] **7-Zip** (`7z` / `7za`)
- [ ] **ripgrep** (`rg`) — much faster than findstr/grep on Windows
- [ ] **fd** (better `find`)
- [ ] **fzf** (fuzzy finder)
- [ ] **bat** (better `cat`)

### Media / image / audio
- [ ] **ffmpeg** (audio/video conversion, capture)
- [ ] **ImageMagick** (`magick` / `convert`)
- [ ] **yt-dlp** (video download)
- [ ] **sox** (audio swiss army knife)

### PDF / docs
- [ ] **pdftotext** (Poppler-utils — extract text from PDFs cheaply, see save-tokens.md)
- [ ] **pandoc** (document conversion)
- [ ] **LibreOffice** (CLI: `soffice --headless`)

### Build tools
- [ ] **make**
- [ ] **cmake**
- [ ] **MSBuild** (Visual Studio)
- [ ] **WiX Toolset** (MSI build — Master's FM uses this)
- [ ] **signtool** (code signing — Master's FM uses this)
- [ ] **resedit** (resource editor — Master's FM uses this)

### Other useful
- [ ] **OBS Studio** (running, with Browser Source support)
- [ ] **OBS WebSocket plugin** (for programmatic OBS control)
- [ ] **Blender** (CLI: `blender -b`)
- [ ] **VS Code** (`code`)
- [ ] **Notepad++**
- [ ] **Postman / Insomnia** (API testing)
- [ ] **Docker / Docker Desktop**
- [ ] **VirtualBox / VMware**

---

## REMOTE SYSTEMS

### Linux (via SSH)
The user will provide SSH credentials when they want me to access a Linux machine. **DO NOT attempt SSH without an explicit invitation in the current session.** If asked, the user will provide:
- Hostname / IP
- Username
- Auth method (key / password)
- What they want me to do there

When SSH is granted, the following are typically present on most modern Linux distros — but verify before relying:
- [ ] **bash** (default shell)
- [ ] **apt** / **dnf** / **pacman** / **zypper** (package manager — depends on distro)
- [ ] **systemctl** (most modern distros)
- [ ] **Python 3**
- [ ] **curl / wget**
- [ ] **git**
- [ ] **ssh / scp / rsync**

Distro-specific things that often matter:
- [ ] **PipeWire** (modern audio — Fedora 34+, Ubuntu 22.10+, Arch, etc.)
- [ ] **PulseAudio** (older Ubuntu, Debian 12)
- [ ] **D-Bus** (universal, for MPRIS / system services)
- [ ] **OBS Studio** (Linux build)

When SSH'd in, ASK the user about distro and tools rather than assuming.

### macOS (via SSH or future remote)
Not currently in use. Add details if/when this becomes relevant.

If macOS access happens later, expect:
- [ ] **zsh** (default since Catalina)
- [ ] **Homebrew** (`brew`) — most common package manager
- [ ] **Xcode Command Line Tools** (developer toolchain)

---

## HOW TO USE THIS FILE IN PRACTICE

When you're about to do something that requires a tool:

1. **Check this file first.** Is the tool checked?
2. **Yes:** use it. If it errors, the inventory may be stale — tell the user.
3. **No, but it's listed unchecked:** ask. "I'd use [tool] for this — you don't have it checked. Want to install, or pick a different approach?"
4. **No, and it's not listed at all:** say so. "I don't see [tool] in tools.md — do you have it? If not, the alternatives are X or Y."

This avoids the failure mode where you assume a tool exists and your suggestion fails on the user's actual machine.

## END OF FILE

Now continue with CLAUDE.md's startup checklist.
