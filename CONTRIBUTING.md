# Contributing

Thanks for taking the time. This project is maintained by a working commissioning
engineer, so the bar for a change is simple: **does it help someone drive a real
TIA Portal project without clicking through the UI?**

Issues, bug reports and PRs are all welcome, in **English or Chinese** — both are
read. 中英文皆可，随便哪种都行。

---

## Before you open an issue

Most problems on Windows + Openness are environment problems, and the bundle can
tell you which one:

```bat
tia.cmd doctor          :: TIA V21
tia-v20.cmd doctor      :: TIA V20
```

It checks the TIA installation, the exe/version match, the local
`Siemens TIA Openness` group and the host registration, and prints the exact fix
for each. Paste its output into the issue — that alone usually settles it.

Please include:

- TIA Portal version (V16 … V21) and Windows version
- Which exe / branch you are on (`tia.cmd version`)
- The MCP client (Cursor, VS Code, Claude Desktop, own HTTP client) or the CLI
  command you ran
- What you expected, what happened, and the full error text

**Do not attach real customer projects.** If a project is needed to reproduce,
strip it down to the smallest block that still fails.

Issue templates live in [`.github/ISSUE_TEMPLATE/`](.github/ISSUE_TEMPLATE).

---

## Which branch does my change go to?

`master` is the stable line for **TIA Portal V20 / V21**.

| Branch | Target version | Maintenance |
|--------|----------------|-------------|
| `master` | TIA Portal V20 / V21 | Official main line — day-to-day work lands here |
| `v21` | TIA Portal V21 / Openness V21 | Official; V21-only adaptations |
| `v20` | TIA Portal V20 / Openness V20 | Official; V20-only adaptations |
| `v19` / `v18` / `v17` / `v16` | the matching TIA Portal / Openness release | Community-maintained |

Rules of thumb:

- A fix that applies to **both** V20 and V21 goes to `master`, so the shared code
  stays in one place.
- Use a version branch only when the change would alter the other version's
  behaviour, or depends on an API that exists in one version only.
- A fix for an older release (V16–V19) goes to **that release's branch**, not to
  `master`. Older Openness APIs and block XML differ enough that mixing them
  destabilises main-line users.
- If a version-branch fix turns out to be general, a small follow-up PR back to
  `master` is very welcome.

Want to adopt one of the community branches as its maintainer? Say so in an
Issue — that offer is taken seriously and answered.

---

## Pull requests

1. Keep it focused. One problem per PR; a 40-line PR gets merged, a 4000-line one
   waits for a weekend that may not come.
2. Say **how you verified it**. For anything touching the Openness layer, the only
   verification that counts is a real run against TIA Portal: import the block,
   run `CompileSoftware`, and report **0 errors / 0 warnings**. "It builds" is not
   verification — Openness accepts plenty of input that only explodes at compile
   time.
3. If you could not test on real hardware or a real TIA install, say so plainly in
   the PR. An honest "untested on V19" is far more useful than silence.
4. Match the surrounding style. This is a mixed C# / PowerShell / docs repo; each
   part already has a convention.
5. Update the docs you invalidate — `docs/`, `手册/`, and
   `tools/tiaportal-mcp/skill/SKILL.md` (the tool spec) are part of the product,
   not an afterthought.
6. Add a `CHANGELOG.md` entry for user-visible changes.

### Please do not commit

TIA project files (`.ap16`…`.ap21`), `bin/` or `obj/`, logs, screenshots,
backups, machine-specific absolute paths, scratch/verification projects, or any
customer data.

---

## Encoding traps (this bites everyone once)

This repo is edited on Chinese Windows, and text encoding is the most common cause
of a "mysteriously broken" file:

- `.s7dcl` and Openness XML must be saved as **UTF-8 *with* BOM**.
- `.scl` must be **UTF-8 *without* BOM**.
- `.ps1` scripts that contain Chinese text must be **UTF-8 with BOM**, otherwise
  Windows PowerShell 5.1 silently swallows a line ending and eats the next line
  into a comment.
- Do not "fix" mojibake by rewriting the text — check the encoding first.

---

## Scope

This project drives TIA Portal through the official **Siemens Openness** API. It
does not ship, unlock, or work around any Siemens licensing, and it does not
bundle Siemens installation media. Contributions must stay on that side of the
line.

## Licence

By contributing you agree that your contribution is licensed under the
[MIT Licence](LICENSE), the same as the rest of the project.
