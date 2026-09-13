; Diagnostics added since the last release.
;
; Kept because the diagnostics are the product here, not a side effect. A rule
; id that changes meaning between versions silently reclassifies somebody's
; build, so the analyzer-release tracker refusing to compile without this file
; is a feature and EnforceExtendedAnalyzerRules stays on.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------
AIN001  | AgentInfer  | Error    | Agent requires a system prompt
AIN002  | AgentInfer  | Error    | Generation method requires [Prompt]
AIN003  | AgentInfer  | Error    | Unsupported return type; must be Task<T>
AIN004  | AgentInfer  | Error    | Code execution is not implemented
AIN005  | AgentInfer  | Error    | Unsupported tool parameter type
AIN006  | AgentInfer  | Warning  | [AgentTool] requires [RequiresPermission]
AIN007  | AgentInfer  | Error    | [Model] requires a non-empty role
AIN008  | AgentInfer  | Error    | PromptFile is not in AdditionalFiles
AIN009  | AgentInfer  | Error    | Both a prompt and a PromptFile were set
AIN010  | AgentInfer  | Error    | PromptFile matches more than one AdditionalFiles entry
AIN011  | AgentInfer  | Error    | [Flags] enum has no JSON schema
AIN012  | AgentInfer  | Warning  | [AgentTools] type has no [AgentTool] methods
AIN013  | AgentInfer  | Error    | [AgentInferJson] target is not a JsonSerializerContext
AIN014  | AgentInfer  | Error    | Return type is not declared in the JSON context
AIN015  | AgentInfer  | Error    | Property carries bounds from both attribute families
AIN016  | AgentInfer  | Error    | Tool result type cannot be rendered without a JSON context
