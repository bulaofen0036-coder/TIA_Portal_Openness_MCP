# Unified HMI Action Recipes

Document id: `hmi-unified-actions`

This document is the stable contract for WinCC Unified button action recipes in the TIA MCP server.
It is intentionally conservative: only deterministic bit commands are directly applicable by the high-level safe tool.

## Tool Selection

| Tool | Use | Writes Project |
|---|---|---|
| `BuildUnifiedHmiButtonActionScript` | Offline recipe generation and linting | No |
| `RunHmiActionScriptRecipeSafetySelfTest` | Offline proof of action safety boundaries | No |
| `EnsureUnifiedHmiButtonAction` | Apply a deterministic safe bit recipe to a real HMI button event | Yes |
| `SetUnifiedHmiButtonEventScriptCode` | Low-level ScriptCode setter with TIA SyntaxCheck | Yes |
| `BindUnifiedHmiTagDynamization` | Bind an HMI item property to an HMI tag | Yes |

## Directly Applicable Recipes

These are the only action kinds that `EnsureUnifiedHmiButtonAction` may apply:

| `actionKind` | Requirement | Generated script |
|---|---|---|
| `set-bit` | Exactly one verified HMI tag | `HMIRuntime.Tags.SysFct.SetBitInTag("<tag>", 0);` |
| `reset-bit` | Exactly one verified HMI tag | `HMIRuntime.Tags.SysFct.ResetBitInTag("<tag>", 0);` |
| `toggle-bit` | Exactly one verified HMI tag | `HMIRuntime.Tags.SysFct.ToggleBitInTag("<tag>", 0);` |

Before applying:

1. Run `GetProjectTree` and resolve the actual HMI software path.
2. Run `GetHmiScreens`, `GetHmiTagTables`, and HMI tag readback.
3. Verify the target HMI tag exists.
4. Verify the HMI tag maps to a real PLC tag or DB member; never bind to guessed M bits.
5. Apply through `EnsureUnifiedHmiButtonAction`.
6. Check `SetUnifiedHmiButtonEventScriptCode` SyntaxCheck/readback metadata.

## Blocked Or Placeholder Recipes

The following action kinds may be analyzed or represented, but must not be directly applied by the safe high-level tool:

| `actionKind` | Status | Reason |
|---|---|---|
| `set-value` | Blocked | Needs range validation, permissions, operator confirmation, SyntaxCheck, and readback. |
| `confirm-write` | Blocked | Same as `set-value`; project-specific UI confirmation is required. |
| `goto-screen` | Blocked until discovered | WinCC Unified navigation API must be discovered and read back in a temporary project. |
| `open-popup` | Blocked until discovered | Popup API must be discovered and read back in a temporary project. |
| `script` | Not deterministic | Generic scripts cannot be proven safe from action metadata alone. |
| `project-binding-placeholder` | Structural only | No executable ScriptCode is generated or applied. |

## Safety Red Lines

- Generated scripts must not contain `Force`.
- Generated scripts must not reference `WatchTable` or `ForceTable`.
- Online watch/monitor table modification is forbidden.
- HMI write actions must not be promoted from blocked to applicable without explicit range checks, operator confirmation, permissions, TIA SyntaxCheck, and readback evidence.
- Navigation and popup recipes are project-specific until a temporary TIA V21 project proves the exact API and ScriptCode shape.

## Offline Recipe Example

Input:

```text
BuildUnifiedHmiButtonActionScript(
  actionKind: "set-bit",
  eventType: "Tapped",
  targetTag: "Cmd_Start"
)
```

Expected safe recipe fields:

```json
{
  "recipeKind": "set-bit",
  "event": "Tapped",
  "targetTags": ["Cmd_Start"],
  "script": "HMIRuntime.Tags.SysFct.SetBitInTag(\"Cmd_Start\", 0);",
  "safetyLevel": "command",
  "requiresApiDiscovery": false,
  "requiresSafetyPolicy": false,
  "applyBlocked": false,
  "requiresSyntaxCheckInTia": true,
  "requiresReadback": true
}
```

## Real Apply Checklist

Use this checklist before calling `EnsureUnifiedHmiButtonAction`:

1. `Connect`
2. `GetProjectTree`
3. `ValidateAutomationContext`
4. `GetHmiScreens`
5. `GetHmiTagTables`
6. Verify the target HMI tag and PLC symbol mapping.
7. Run `BuildUnifiedHmiButtonActionScript` and inspect `Meta.errors`, `Meta.warnings`, `Meta.applyBlocked`.
8. Call `EnsureUnifiedHmiButtonAction` only for `set-bit`, `reset-bit`, or `toggle-bit`.
9. Confirm SyntaxCheck/readback metadata from `SetUnifiedHmiButtonEventScriptCode`.
10. Save only after readback succeeds.

## Temporary Project Validation

For recipes that are currently blocked by API discovery:

1. Create or open a disposable TIA V21 project.
2. Add one minimal Unified screen and one button.
3. Configure the navigation/popup event manually or through a verified Openness path.
4. Read back event ScriptCode and API shape.
5. Reapply it to a disposable screen through `SetUnifiedHmiButtonEventScriptCode`.
6. Run SyntaxCheck and read back ScriptCode.
7. Only then update this contract and the safety self-test.
