# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| 1.0.x   | Final release (no further updates planned) |
| 0.3.x   | No        |
| 0.2.x   | No        |
| 0.1.x   | No        |

## Reporting a Vulnerability

If you discover a security vulnerability in UniClaude, please report it responsibly. **Do not open a public issue.**

### How to Report

Report vulnerabilities through [GitHub Security Advisories](https://github.com/TheArcForge/UniClaude/security/advisories/new). Include:

- A description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if you have one)

### What to Expect

- **Acknowledgment** within 48 hours of your report.
- **Assessment** within 7 days. We will confirm whether the issue is accepted and share our planned timeline.
- **Fix and disclosure** coordinated with you. We aim to release a patch within 30 days of confirming a vulnerability.
- **Credit** in the release notes (unless you prefer to remain anonymous).

We will not take legal action against researchers who report vulnerabilities in good faith and follow this policy.

## Security Model

UniClaude's security boundaries are documented in [ARCHITECTURE.md](docs/ARCHITECTURE.md#security). Key design decisions:

- **Localhost-only HTTP servers** — both the Unity MCP server and the Node.js sidecar bind exclusively to `127.0.0.1`. Remote endpoints and non-loopback `Host` headers are rejected (defence-in-depth against DNS-rebinding from a malicious page).
- **Per-session shared-secret auth** — on every Editor launch the MCP server generates a 256-bit random token, persists it via `SessionState`, and forwards it to the sidecar through the `UNICLAUDE_AUTH_TOKEN` env var. Every HTTP request in either direction must present that token (`Authorization: Bearer …` header, or `?token=…` query for the MCP transport). Compares are constant-time. Servers refuse to serve requests until a token is configured.
- **Path sandboxing** — all file operations performed through MCP file tools are validated by `PathSandbox`. The sandbox forbids absolute paths, NUL/control bytes, and any traversal that resolves outside the project root, and refuses to write to `.git/`, `Library/`, `Logs/`, `Temp/`, `obj/`, `Build*/`, `Packages/manifest.json`, `Packages/packages-lock.json`, or `Packages/com.arcforge.uniclaude/**`. Symbolic links inside the project are followed and rejected when their target lies outside the project.
- **No credential storage** — authentication with Claude Code is handled via OAuth by the Agent SDK; UniClaude does not persist API keys, tokens, or other credentials in project files or editor preferences.
- **Permission system** — UniClaude MCP tools default to *prompt-on-use*. Users can opt in to auto-approval from the Settings panel after auditing the tool surface.
- **Bounded external input** — release notes fetched from GitHub are stripped of control bytes, length-capped, and have `javascript:` / `data:` / `vbscript:` / `file:` URI schemes neutralized before being persisted or rendered. Release URLs are rejected unless they are HTTPS on `github.com` (or a subdomain). UPM dependency URLs are restricted to `https://`, `http://`, `ssh://`, `git://`, `file://`, or SSH shorthand; refs containing whitespace or leading `-` are refused so they cannot become extra git options.
- **Hardened install scripts** — the Sidecar `postinstall` script only clears the macOS quarantine bit on `node_modules/@anthropic-ai/` (where the bundled SDK ships unsigned binaries) and only on Darwin. The Ninja-mode installer refuses to write `filter.uniclaude.clean`/`smudge` configs when the resolved Node.js or installer paths contain shell metacharacters.

## Design Boundaries (Important)

- **Scripting RCE by design** - `file_create_script` can write arbitrary C# under `Assets/`. If the user then triggers `project_refresh_assets` or `project_recompile_scripts`, Unity may load and execute static initializers (for example `[InitializeOnLoadMethod]`) in the Editor process with OS-level privileges. This is not a vulnerability; it is an intentional capability of a Unity editor scripting assistant. The primary mitigations are the per-session auth token and UniClaude's default prompt-on-use permissions.

- **Indirect prompt injection via project data** - many tools (for example `inspector_inspect`, `scene_get_hierarchy`, `project_get_console_log`, `file_read`, and `project_search`) return user-controlled strings from the project (names, logs, and file contents). When those strings are fed into an LLM, they can attempt to influence the LLM's behavior as "instructions". UniClaude's auth + permission prompts are the mitigation; when enabling auto-approval, users should audit the project data they are willing to expose to an LLM.
- **Localhost-only MCP server** — the HTTP transport binds to `127.0.0.1` and is not network-accessible.
- **Path sandboxing** — all file operations are validated to stay within the project root via `PathSandbox`.
- **No credential storage** — authentication is handled by the Claude Code Agent SDK via OAuth. No API keys, tokens, or credentials are stored in project files or preferences.
- **Permission system** — every MCP tool call requires explicit user approval before execution.
