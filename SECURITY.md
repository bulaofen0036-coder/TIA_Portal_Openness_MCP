# Security Policy

## Supported versions

Security fixes land on `master` (TIA Portal V20 / V21) and are shipped in the next
release. The community version branches (`v16`–`v19`) receive fixes only if a
maintainer for that branch picks them up.

## Reporting a vulnerability

**Please do not open a public Issue for a security problem.**

Use GitHub's private reporting instead:
**Security → Advisories → Report a vulnerability** on this repository
(<https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP/security/advisories/new>).

Please include:

- What the issue is and what an attacker gains
- Affected version / commit and TIA Portal version
- Reproduction steps, with the smallest possible test project
- Any log output — with machine names, IP addresses and customer identifiers
  removed

You can expect an acknowledgement within about a week. Once a fix is ready it is
released and the advisory is published with credit to you, unless you prefer to
stay anonymous.

## What is in scope

This is an engineering tool that runs **locally** on a Windows machine and drives
TIA Portal through the Siemens Openness API. Relevant classes of issue:

- Arbitrary code or command execution triggered by a crafted spec file, block
  source, or MCP tool argument
- Path traversal or writes outside the intended workspace during import/export
- The HTTP transport exposing tools beyond its intended local scope, or missing
  isolation between clients
- A read-only / "dry run" path that in fact writes to a project or to a live PLC
- Leaking credentials or project contents into logs

## What is out of scope

- Anything requiring an attacker to already have interactive administrator access
  to the machine
- Vulnerabilities in TIA Portal, Siemens Openness, or Windows itself — report
  those to Siemens ProductCERT
- The bundle's own operating risk: **this tool can compile to and download into a
  real PLC.** Running it against production plant equipment is an operational
  decision, not a vulnerability. Always verify on a test project first.

## Licensing

This server contains **no licence-enforcement code** and requires no licence key.
It does not bypass, emulate, or interfere with Siemens licensing in any way.
Reports claiming otherwise are welcome and will be investigated immediately.
