using System.Text.Json.Nodes;
using SignalForge.Application.ExpressionEvaluation;

namespace SignalForge.UnitTests;

public class ConditionExpressionEvaluatorTests
{
    private static readonly JsonNode? Context = JsonNode.Parse("""
        {
          "event": {
            "type": "order.created",
            "externalEventId": "ord-123",
            "payload": {
              "order": { "total": 150.5, "items": [ { "sku": "A1", "qty": 2 }, { "sku": "B2", "qty": 1 } ] },
              "customer": { "vip": true }
            }
          },
          "output": {
            "1": { "status": "ok", "code": 200 },
            "2": { "approved": false }
          }
        }
        """);

    [Theory]
    [InlineData("$.event.type == 'order.created'", true)]
    [InlineData("$.event.type == 'order.cancelled'", false)]
    [InlineData("$.event.type != 'order.cancelled'", true)]
    public void String_equality_evaluates_correctly(string expression, bool expected)
    {
        var result = ConditionExpressionEvaluator.Evaluate(expression, Context);
        Assert.Null(result.Error);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData("$.event.payload.order.total > 100", true)]
    [InlineData("$.event.payload.order.total > 150.5", false)]
    [InlineData("$.event.payload.order.total >= 150.5", true)]
    [InlineData("$.event.payload.order.total < 100", false)]
    [InlineData("$.event.payload.order.total <= 150.5", true)]
    public void Numeric_comparisons_evaluate_correctly(string expression, bool expected)
    {
        var result = ConditionExpressionEvaluator.Evaluate(expression, Context);
        Assert.Null(result.Error);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void Boolean_literal_comparison_evaluates()
    {
        Assert.True(ConditionExpressionEvaluator.Evaluate("$.event.payload.customer.vip == true", Context).Value);
        Assert.True(ConditionExpressionEvaluator.Evaluate("$.output.2.approved == false", Context).Value);
    }

    [Fact]
    public void Null_missing_path_matches_null_literal()
    {
        Assert.True(ConditionExpressionEvaluator.Evaluate("$.event.payload.missing == null", Context).Value);
        Assert.False(ConditionExpressionEvaluator.Evaluate("$.event.payload.missing != null", Context).Value);
        Assert.True(ConditionExpressionEvaluator.Evaluate("($.event.payload.missing != null) == false", Context).Value);
    }

    [Theory]
    [InlineData("$.event.type == 'order.created' and $.event.payload.order.total > 100", true)]
    [InlineData("$.event.type == 'order.created' and $.event.payload.order.total > 999", false)]
    [InlineData("$.event.type == 'order.created' or $.event.payload.order.total > 999", true)]
    public void Logical_and_or_combine_correctly(string expression, bool expected)
    {
        var result = ConditionExpressionEvaluator.Evaluate(expression, Context);
        Assert.Null(result.Error);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void Not_and_parentheses_bind_correctly()
    {
        var result = ConditionExpressionEvaluator.Evaluate(
            "($.event.type == 'order.created' or $.event.type == 'order.cancelled') and not $.event.payload.order.total > 999",
            Context);
        Assert.Null(result.Error);
        Assert.True(result.Value);

        Assert.False(ConditionExpressionEvaluator.Evaluate("not $.event.type == 'order.created'", Context).Value);
        Assert.True(ConditionExpressionEvaluator.Evaluate("not $.event.payload.order.total > 999", Context).Value);
    }

    [Fact]
    public void Array_index_paths_resolve()
    {
        Assert.True(ConditionExpressionEvaluator.Evaluate("$.event.payload.order.items[0].qty == 2", Context).Value);
        Assert.True(ConditionExpressionEvaluator.Evaluate("$.event.payload.order.items[1].sku == 'B2'", Context).Value);
        // Out-of-range index resolves to null -> false
        Assert.False(ConditionExpressionEvaluator.Evaluate("$.event.payload.order.items[9].qty == 2", Context).Value);
    }

    [Fact]
    public void Output_steps_keyed_by_number_resolve()
    {
        Assert.True(ConditionExpressionEvaluator.Evaluate("$.output.1.status == 'ok'", Context).Value);
        Assert.True(ConditionExpressionEvaluator.Evaluate("$.output.1.code >= 200", Context).Value);
        Assert.False(ConditionExpressionEvaluator.Evaluate("$.output.3.exists == true", Context).Value);
    }

    [Fact]
    public void Literals_and_shape_are_strict()
    {
        // Number vs string equality is false, not an error (types must match).
        var result = ConditionExpressionEvaluator.Evaluate("$.event.payload.order.total == '150.5'", Context);
        Assert.Null(result.Error);
        Assert.False(result.Value);

        // Ordering across types is an error -> evaluates false with error.
        var errorResult = ConditionExpressionEvaluator.Evaluate("$.event.type > 5", Context);
        Assert.False(errorResult.Value);
        Assert.NotNull(errorResult.Error);
    }

    [Theory]
    [InlineData("==")]
    [InlineData("$.event.type ==")]
    [InlineData("(1 > 2")]
    [InlineData("foo")]
    [InlineData("$.")]
    [InlineData("")]
    [InlineData("$[a]")]
    public void Malformed_expressions_evaluate_false_with_error(string expression)
    {
        var result = ConditionExpressionEvaluator.Evaluate(expression, Context);
        Assert.False(result.Value);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void No_context_yields_false_for_path_expressions_but_true_for_literals()
    {
        Assert.False(ConditionExpressionEvaluator.Evaluate("$.event.type == 'order.created'", null).Value);
        Assert.True(ConditionExpressionEvaluator.Evaluate("true", null).Value);
        Assert.False(ConditionExpressionEvaluator.Evaluate("not true", null).Value);
    }

    // ---------- resource guards (Decision #26) ----------

    [Fact]
    public void Overlong_expression_fails_fast_with_error()
    {
        var expression = new string('a', ConditionExpressionEvaluator.MaxExpressionLength + 1);

        var result = ConditionExpressionEvaluator.Evaluate(expression, Context);

        Assert.False(result.Value);
        Assert.NotNull(result.Error);
        Assert.Contains("maximum length", result.Error);
    }

    [Fact]
    public void Deeply_nested_parentheses_are_rejected_without_throwing()
    {
        // 1000 nested parens — far beyond MaxParseDepth; must fail fast, not recurse forever.
        var expression = new string('(', 1000) + "true" + new string(')', 1000);

        var result = ConditionExpressionEvaluator.Evaluate(expression, Context);

        Assert.False(result.Value);
        Assert.NotNull(result.Error);
        Assert.Contains("nesting depth", result.Error);
    }

    [Fact]
    public void Deep_not_chains_are_rejected_without_throwing()
    {
        // 300 chained 'not's (~1200 chars, within the length limit) — bounded by MaxParseDepth;
        // must not overflow the stack.
        var expression = string.Concat(Enumerable.Repeat("not ", 300)) + "true";

        var result = ConditionExpressionEvaluator.Evaluate(expression, Context);

        Assert.False(result.Value);
        Assert.NotNull(result.Error);
        Assert.Contains("nesting depth", result.Error);
    }
}