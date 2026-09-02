# Conditional Step Expression Grammar

Step type: `Conditional`. Configuration:
```json
{
  "expression": "$.event.type == 'order.created' and $.event.payload.order.total > 100",
  "trueStep": 5,
  "falseStep": 7
}
```

`expression` is evaluated against the **execution context**; `trueStep` / `falseStep` are the
absolute step numbers the workflow routes to, based on the result.

## Execution context ($)

The context root is a JSON object built by `WorkflowExecutionOrchestratorService` for every step
execution. A `$`-rooted JSON path reads into it.

```json
{
  "event": {
    "type": "order.created",
    "externalEventId": "ord-123",
    "occurredAt": "2026-09-12T00:00:00Z",
    "receivedAt": "2026-09-12T00:00:01Z",
    "payload": { ...parsed event payload... }
  },
  "output": {
    "1": { "status": "ok", "code": 200 },
    "2": { "approved": false }
  }
}
```

- `$` — the whole context object.
- `$.event.type` — the triggering event's type (e.g. `order.created`).
- `$.event.payload.<field>` — a field of the parsed event payload.
- `$.output.<stepNumber>.<field>` — the stored output of a previously succeeded step.
- `$...items[0]` — array indexing into a JSON array.

A path that does not resolve evaluates to `null`.

## Literals

| Literal | Example |
|---------|---------|
| String | `'order.created'`, `"order.created"` (backslash escapes: `\'`, `\"`, `\\`, `\n`, `\t`, `\r`) |
| Number | `5`, `150.5`, `-3` |
| Boolean | `true`, `false` |
| Null | `null` |

## Operators (lowest → highest precedence)

| Precedence | Operator | Kind |
|-----------|----------|------|
| 1 | `or` | logical |
| 2 | `and` | logical |
| 3 | `not` | unary |
| 4 | `==` `!=` `>` `<` `>=` `<=` | comparison |

Parentheses group: `( ... )`.

## Comparison semantics

- `==` / `!=` — operands must be the same kind (number vs number, string vs string, boolean vs
  boolean, null vs null). Strings compare **case-sensitively** (ordinal). A missing path (`null`)
  equals only `null`. Type mismatches are `false` for `==` (not an error).
- `>` `<` `>=` `<=` — both operands must be numbers (numeric ordering) **or** both strings (ordinal
  ordering). Cross-type ordering is an error (evaluates `false`).
- Bare operands are coerced to boolean for `not` / `and` / `or`: `null` → `false`, number `0` →
  `false`, empty string → `false`, `false` → `false`, everything else → `true`.

## Examples

```
$.event.type == 'order.created'
$.event.payload.order.total >= 1500 and not $.event.payload.customer.vip
($.event.type == 'order.created' or $.event.type == 'order.paid') and $.output.1.code == 200
$.event.payload.items[0].qty > 0
$.output.2.approved == false
```

## Excluded (sandbox)

No function calls, no method calls, no arithmetic (`+`/`-` on expressions), no arithmetic/logical
mixing beyond the table above, no environment access, no loops. The evaluator is a hand-rolled
tokenizer + recursive-descent parser + evaluator in
`src/SignalForge.Application/ExpressionEvaluation/ConditionExpressionEvaluator.cs`.

## Error handling

- Malformed expressions, tokenizer/parser/evaluator errors never throw out of the step.
  `ConditionExpressionEvaluator.Evaluate` returns `Value = false` + the error message; the
  conditional step logs a warning and routes to the **else** (`falseStep`) branch.
- **Resource guards (Decision #26):** expressions are capped at 2048 characters
  (`MaxExpressionLength`) and 64 levels of nesting for parentheses/`not` (`MaxParseDepth`). An
  over-limit expression fails fast the same way (false + error) instead of exhausting the stack or
  spending unbounded time tokenizing.
- Broken step configuration (missing `expression` / `trueStep` / `falseStep` or invalid JSON) fails
  the step via the processor's catch path (step → retry → dead-letter after max attempts).

## Routing

On success the processor records the target step on the step execution
(`WorkflowStepExecution.RouteToStepNumber`). The orchestrator then jumps the execution to that
step number; if unset (non-conditional steps) it advances sequentially.