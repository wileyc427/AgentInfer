; Diagnostics added since the last release.
;
; Kept because the diagnostics are the product here, not a side effect. A rule
; id that changes meaning between versions silently reclassifies somebody's
; build, so the analyzer-release tracker refusing to compile without this file
; is a feature and EnforceExtendedAnalyzerRules stays on.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------
AGT001  | Agentry  | Error    | Agent requires a system prompt
AGT002  | Agentry  | Error    | Generation method requires [Prompt]
AGT003  | Agentry  | Error    | Unsupported return type; must be Task<T>
AGT004  | Agentry  | Error    | CodeAct is not implemented (P3)
