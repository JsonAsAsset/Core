using CUE4Parse.UE4.Assets.Exports.Animation.CurveExpression;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

using Microsoft.AspNetCore.Mvc;

using System.Globalization;
using System.Text;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Core Cloud Controller: Curve Expressions                                                                                         */
/*                                                                                                                                  */
/* A CurveExpressionsDataAsset drives one curve from others by arithmetic, and what a head does with its face is written here rather */
/* than in any animation. Cooking keeps only the compiled form: a postfix instruction list per target curve, with functions named by */
/* their index into the plugin's builtin table. The source text is editor-only and gone.                                             */
/*                                                                                                                                  */
/* The instructions are walked back into the expression they were compiled from, so the asset reads the way it was written.          */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Controllers;

public partial class CloudApiController
{
    private sealed record CurveExpression(
        string Target,
        string Expression,
        string[] Constants);

    /* What each curve is driven by, written back out as arithmetic */
    [HttpGet("export/expressions")]
    public ActionResult GetCurveExpressions(string? path)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new
        {
            errorCode = "cloud.expressions.no_path",
            errorMessage = "No asset supplied",
            numericErrorCode = 1005
        });

        path = path.SubstringBefore('.');

        var profile = FindBaseProfileForPath(path, found: out var found);
        if (!found) return NotFoundResponse;

        if (LoadExportOfType<UCurveExpressionsDataAsset>(profile.Provider, path) is not { } asset || asset.ExpressionData?.ExpressionMap is not { } map)
        {
            return NotFoundResponse;
        }

        var expressions = new List<CurveExpression>(map.Count);

        foreach (var (target, expression) in map)
        {
            var constants = expression.Expression
                .Select(element => element.TryGet<FName>(out var name) ? name.Text : null)
                .Where(name => name is not null)
                .Distinct()
                .ToArray()!;

            expressions.Add(new CurveExpression(target.Text, CurveExpressionText.Write(expression), constants));
        }

        expressions.Sort((left, right) => string.CompareOrdinal(left.Target, right.Target));

        var namedConstants = asset.NamedConstants?.Select(name => name.Text).ToArray() ?? [];

        return new JsonResult(new { namedConstants, expressions });
    }
}

/* The compiled form, written back out as the arithmetic it came from */
public static class CurveExpressionText
{
    /* Indexed by position, which is what the asset stores. Order follows the plugin's own
     * registration, so anything appended upstream belongs on the end. */
    private static readonly (string Name, int ArgumentCount)[] Functions =
    [
        ("clamp", 3), ("min", 2), ("max", 2),
        ("abs", 1), ("round", 1), ("ceil", 1), ("floor", 1),
        ("sin", 1), ("cos", 1), ("tan", 1), ("asin", 1), ("acos", 1), ("atan", 1),
        ("sqrt", 1), ("isqrt", 1), ("log", 1), ("exp", 1),
        ("pi", 0), ("e", 0), ("undef", 0)
    ];

    /* Precedence as the plugin's own parser table has it, so the brackets written here are the ones
     * it needs to read the expression back the same way. Modulo and floor divide bind tighter than
     * multiply and divide, which is not where most languages put them. */
    private const int TermAtom = 6;
    private const int TermNegate = 5;
    private const int TermPower = 4;
    private const int TermRemainder = 3;
    private const int TermProduct = 2;
    private const int TermSum = 1;

    private readonly record struct ExpressionTerm(string Text, int Precedence);

    /* Postfix back to infix: operands stack up and an operator takes what it needs, leaving the
     * result behind. A list that does not balance leaves the stack short, and the instructions are
     * handed back as they were read rather than as an expression that would be a guess. */
    public static string Write(FExpressionObject expression)
    {
        var stack = new List<ExpressionTerm>();

        foreach (var element in expression.Expression)
        {
            if (element.TryGet<float>(out var value))
            {
                stack.Add(new ExpressionTerm(WriteNumber(value), TermAtom));
            }
            else if (element.TryGet<FName>(out var constant))
            {
                stack.Add(new ExpressionTerm(WriteConstant(constant.Text), TermAtom));
            }
            else if (element.TryGet<EOperator>(out var op))
            {
                if (op == EOperator.Negate)
                {
                    if (stack.Count < 1) return WriteInstructions(expression);

                    var operand = Pop(stack);

                    stack.Add(new ExpressionTerm($"-{Bracket(operand, TermNegate)}", TermNegate));

                    continue;
                }

                if (stack.Count < 2) return WriteInstructions(expression);

                var right = Pop(stack);
                var left = Pop(stack);

                var (symbol, precedence, rightAssociative) = op switch
                {
                    EOperator.Add => ("+", TermSum, false),
                    EOperator.Subtract => ("-", TermSum, false),
                    EOperator.Multiply => ("*", TermProduct, false),
                    EOperator.Divide => ("/", TermProduct, false),
                    EOperator.Modulo => ("%", TermRemainder, false),
                    EOperator.FloorDivide => ("//", TermRemainder, false),
                    EOperator.Power => ("**", TermPower, true),
                    _ => ("?", TermSum, false)
                };

                /* The side the operator does not associate with is the one that needs bracketing */
                var lhs = rightAssociative ? BracketWithin(left, precedence) : Bracket(left, precedence);
                var rhs = rightAssociative ? Bracket(right, precedence) : BracketWithin(right, precedence);

                stack.Add(new ExpressionTerm($"{lhs} {symbol} {rhs}", precedence));
            }
            else if (element.TryGet<FFunctionRef>(out var function))
            {
                if (function.Index < 0 || function.Index >= Functions.Length) return WriteInstructions(expression);

                var (name, argumentCount) = Functions[function.Index];

                if (stack.Count < argumentCount) return WriteInstructions(expression);

                var arguments = new string[argumentCount];

                for (var index = argumentCount - 1; index >= 0; index--)
                {
                    var argument = Pop(stack);

                    /* The parser only unwinds its operator stack at a closing bracket, never at a
                     * comma, so an argument with an operator left pending is read as an unexpected
                     * comma. Bracketing it makes it unwind in time. The last one is already
                     * followed by the closing bracket, so it needs nothing. */
                    arguments[index] = index < argumentCount - 1 && argument.Precedence < TermAtom
                        ? $"({argument.Text})"
                        : argument.Text;
                }

                stack.Add(new ExpressionTerm($"{name}({string.Join(", ", arguments)})", TermAtom));
            }
        }

        return stack.Count == 1 ? stack[0].Text : WriteInstructions(expression);
    }

    private static ExpressionTerm Pop(List<ExpressionTerm> stack)
    {
        var term = stack[^1];

        stack.RemoveAt(stack.Count - 1);

        return term;
    }

    private static string Bracket(ExpressionTerm term, int precedence) =>
        term.Precedence < precedence ? $"({term.Text})" : term.Text;

    private static string BracketWithin(ExpressionTerm term, int precedence) =>
        term.Precedence <= precedence ? $"({term.Text})" : term.Text;

    private static string WriteNumber(float value)
    {
        if (float.IsNaN(value)) return "undef()";
        if (float.IsPositiveInfinity(value)) return "inf";
        if (float.IsNegativeInfinity(value)) return "-inf";

        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    /* Anything that is not a bare word has to be quoted to read back as one term */
    private static string WriteConstant(string name)
    {
        var bare = name.Length > 0 && !char.IsDigit(name[0]);

        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character) || character == '_') continue;

            bare = false;

            break;
        }

        return bare ? name : $"'{name.Replace("'", "\'")}'";
    }

    /* What the asset holds, when it does not walk back into an expression */
    private static string WriteInstructions(FExpressionObject expression)
    {
        var builder = new StringBuilder();

        foreach (var element in expression.Expression)
        {
            if (builder.Length > 0) builder.Append(' ');

            if (element.TryGet<float>(out var value)) builder.Append($"V[{WriteNumber(value)}]");
            else if (element.TryGet<FName>(out var constant)) builder.Append($"C[{constant.Text}]");
            else if (element.TryGet<EOperator>(out var op)) builder.Append($"Op[{op}]");
            else if (element.TryGet<FFunctionRef>(out var function)) builder.Append($"F[{function.Index}]");
        }

        return builder.ToString();
    }
}
