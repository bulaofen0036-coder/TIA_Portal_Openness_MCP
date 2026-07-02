# Server roadmap — aligned with the low-barrier direction

Status date: 2026-06-17. **This is NOT a "catch up to T-IA Connect / Openness Manager"
roadmap.** Project direction (confirmed 2026-06-17; memory
`feedback-mcp-lower-barrier-not-features`): optimize for **降门槛 / 好用 / 正确率**, not
feature-parity with the commercial suites. Several headline competitor features are
**deliberately declined** below — low real benefit for this tool's job (engineering
automation) and genuinely risky on a live machine (江夏 安全PLC is in scope).

Competitor reference (context only, **not** targets): **T-IA Connect** (REST+MCP, F-safe,
VCI/Git, UMAC, SiVArc) and **TIA Openness Manager** (PLCSIM unit tests, fingerprint diff,
OPC UA browser, encrypted vault, Git UI). Capability boundary table: SKILL.md §18.

---

## Done

### V21 download cast bug — DONE (2026-06-17)
- **What:** `DownloadToPlc` failed on V21 with the `ConnectionConfiguration` → `IConfiguration`
  cast. **Fixed:** navigate to a `ConfigurationTargetInterface` (the real `IConfiguration`)
  via `Modes → PcInterfaces → TargetInterfaces`; also fixed the `StopModules` selection
  (`StopAll`, not the non-existent "StopModule").
- **Verified** end-to-end on a real S7-1200 (江夏 安全PLC) → `state=Success, 0 errors`.
- **Follow-up (only if it ever bites):** the route auto-selects the FIRST PG/PC interface —
  add a `targetInterfaceName` param only if multi-NIC selection becomes a real problem.

---

## Explicitly NOT planned — declined by direction (低收益 + 高风险)

Decided 2026-06-17. Do **not** start these; do **not** market them as "coming". If a
concrete user ever demands one, re-open the discussion first — don't just build it.

- **Safety F-block author / compile / signature.** Both commercial tools lead with this; we
  don't. Authoring/altering F-logic by AI on a live safety CPU is the highest-risk,
  lowest-upside thing this tool could do, and F-CPU compile has no Openness API anyway
  (SKILL.md §4). Reading *non-safety* blocks on a safety PLC already works (§10/§11, verified
  on 安全PLC) — that is sufficient.
- **PLCSIM (Advanced) simulation / unit testing.** Needs a separate license + runtime
  (`Siemens.Simatic.Simulation.Runtime`) and a large integration, while *raising* the barrier
  (a heavy dependency most users won't have). Contradicts the 降门槛 goal.
- **Native Git / VCI.** The text-export workaround (§16, `ExportBlocksAsDocuments`) already
  covers the 80% (diff / review / history) at zero added surface. A native VCI clone is a
  product-suite feature, not engineering automation.
- **UMAC user/rights, SiVArc auto-screens, full Git UI, encrypted credential vault.**
  Product-suite scope; out of scope for an MCP whose job is engineering automation.
- **OPC UA *write* / method-call / subscribe.** OPC UA stays **read-only**
  (`ReadPlcLiveValuesOpcUa`) on purpose — supervised writes to a running crane are not worth
  the failure modes. (Declined under "你决定" 2026-06-17; not in the original reject list, so
  re-open if a real commissioning need appears, behind an explicit safety gate.)

---

## Still open — only if cheap AND aligned (no pressure to do any)

These don't contradict the direction and add little surface. Pick up only on real need:

- **Block know-how protection** (`Protect`/`Unprotect` over `PlcBlockProtectionProvider`;
  call shape proven in `TiaHelper.cs`). Small, and genuinely useful for IP on delivered
  交付包 blocks. Effort: S.
- **Git snapshot helper** over the §16 text export (one call: export to a git dir + `git diff`
  vs last snapshot). Pure glue over existing tools + git CLI; **not** a VCI clone. Effort: S–M.

---

## The real frontier is packaging, not server features

The only axis where the simplest competitor (`AI助手`: one `.exe`, a DeepSeek key, "获取项目",
chat, import) beats this MCP is **onboarding / 门槛** — and that is answered by
**TiaHelperGui** (the thin WinForms front-end for people who can't wire up MCP), not by adding
tools to the server. Eigent is **not** a competitor: it's a generic MCP host (a local Claude
Cowork alternative) that could *mount* this server. Distribution, not features.
