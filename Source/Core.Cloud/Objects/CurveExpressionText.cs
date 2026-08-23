using CUE4Parse.UE4.Assets.Exports.Animation.CurveExpression;
using CUE4Parse.UE4.Objects.UObject;

using System.Globalization;
using System.Text;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Curve Expressions                                                                                                                */
/*                                                                                                                                  */
/* A cooked CurveExpressionsDataAsset keeps only the compiled form of what drives each of its curves: a postfix instruction list with */
/* functions named by their index into the plugin's builtin table. The source text is editor-only and does not survive.              */
/*                                                                                                                                  */
/* Both directions live here. Written out, the instructions read as the arithmetic they were compiled from. Run, they answer what a  */
/* curve comes to for a given set of constants, which is how a consumer wanting the amounts rather than the sentence gets them.      */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Core.Cloud.Objects;

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

    /* The compiled form run rather than written out, against the same table the writer names its
     * functions from, so the two cannot disagree about what an instruction is.
     *
     * Anything the instructions do not add up to -- an operator with nothing under it, a function
     * index off the end of the table, a list that leaves more than one value behind -- comes back
     * as NaN rather than throwing: this runs over whatever a cook happened to leave in the asset,
     * and one malformed expression should cost that expression, not the request. */
    public static float Evaluate(FExpressionObject expression, Func<string, float> constant)
    {
        var stack = new List<float>();

        foreach (var element in expression.Expression)
        {
            if (element.TryGet<float>(out var literal))
            {
                stack.Add(literal);
            }
            else if (element.TryGet<FName>(out var name))
            {
                stack.Add(constant(name.Text));
            }
            else if (element.TryGet<EOperator>(out var op))
            {
                if (op == EOperator.Negate)
                {
                    if (stack.Count < 1) return float.NaN;

                    stack[^1] = -stack[^1];

                    continue;
                }

                if (stack.Count < 2) return float.NaN;

                var right = Pop(stack);
                var left = Pop(stack);

                stack.Add(op switch
                {
                    EOperator.Add => left + right,
                    EOperator.Subtract => left - right,
                    EOperator.Multiply => left * right,
                    /* Dividing by nothing is how the plugin's own evaluator reads it too */
                    EOperator.Divide => right == 0f ? 0f : left / right,
                    EOperator.Modulo => right == 0f ? 0f : left % right,
                    EOperator.FloorDivide => right == 0f ? 0f : MathF.Floor(left / right),
                    EOperator.Power => MathF.Pow(left, right),
                    _ => float.NaN
                });
            }
            else if (element.TryGet<FFunctionRef>(out var function))
            {
                if (function.Index < 0 || function.Index >= Functions.Length) return float.NaN;

                var (functionName, argumentCount) = Functions[function.Index];

                if (stack.Count < argumentCount) return float.NaN;

                /* Arguments went on left to right, so taking them off walks backwards */
                var arguments = new float[argumentCount];

                for (var index = argumentCount - 1; index >= 0; index--)
                {
                    arguments[index] = Pop(stack);
                }

                stack.Add(Apply(functionName, arguments));
            }
        }

        return stack.Count == 1 ? stack[0] : float.NaN;
    }

    private static float Apply(string name, float[] arguments) => name switch
    {
        "clamp" => Math.Clamp(arguments[0], arguments[1], arguments[2]),
        "min" => MathF.Min(arguments[0], arguments[1]),
        "max" => MathF.Max(arguments[0], arguments[1]),
        "abs" => MathF.Abs(arguments[0]),
        "round" => MathF.Round(arguments[0]),
        "ceil" => MathF.Ceiling(arguments[0]),
        "floor" => MathF.Floor(arguments[0]),
        "sin" => MathF.Sin(arguments[0]),
        "cos" => MathF.Cos(arguments[0]),
        "tan" => MathF.Tan(arguments[0]),
        "asin" => MathF.Asin(arguments[0]),
        "acos" => MathF.Acos(arguments[0]),
        "atan" => MathF.Atan(arguments[0]),
        "sqrt" => MathF.Sqrt(arguments[0]),
        "isqrt" => arguments[0] <= 0f ? 0f : 1f / MathF.Sqrt(arguments[0]),
        "log" => arguments[0] <= 0f ? 0f : MathF.Log(arguments[0]),
        "exp" => MathF.Exp(arguments[0]),
        "pi" => MathF.PI,
        "e" => MathF.E,
        _ => float.NaN
    };

    private static float Pop(List<float> stack)
    {
        var value = stack[^1];

        stack.RemoveAt(stack.Count - 1);

        return value;
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
