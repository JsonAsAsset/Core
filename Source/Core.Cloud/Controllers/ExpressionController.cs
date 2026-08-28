using CUE4Parse.UE4.Assets.Exports.Animation.CurveExpression;

using Core.Cloud.Objects;
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
        string Form,
        string[] Constants,
        Dictionary<string, float> Weights);

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

            var weights = WeighConstants(expression, constants);

            expressions.Add(new CurveExpression(
                target.Text,
                CurveExpressionText.Write(expression),
                DescribeForm(expression, constants, weights),
                constants,
                weights));
        }

        expressions.Sort((left, right) => string.CompareOrdinal(left.Target, right.Target));

        var namedConstants = asset.NamedConstants?.Select(name => name.Text).ToArray() ?? [];

        return new JsonResult(new { namedConstants, expressions });
    }

    /* What each constant is worth to the curve it drives.
     *
     * These mappings are how a head says "this one curve of mine is that handful of the rig's, in
     * these amounts", and a consumer wanting to work the other way round needs the amounts rather
     * than the sentence. Read off the compiled expression by running it, one constant at a time,
     * with that constant at one and the rest at nothing: what comes back out is what that constant
     * contributes on its own.
     *
     * Run rather than read because the compiled form is postfix with functions in it, and the
     * arithmetic that gets a coefficient out of that is the arithmetic the evaluator already is.
     * A curve driven by something other than a weighted sum has no such thing as a coefficient,
     * and for one of those this reports what each constant does alone, which is all that can be
     * said without inventing the rest. */
    /* The curve mapping as a table, said once so nobody else has to know where it is.
     *
     * Each entry is one of the newer rig's controls and how much of each of the older head's curves
     * went into it. That is the mapping itself, weighed out rather than interpreted: read forward it
     * says what an older animation becomes, and read backwards it says what a newer one was made
     * of. Which of those is wanted is the caller's business.
     *
     * The mapping is the one the game ships unless another is named, because which one it is, is a
     * fact about the game rather than a choice anybody makes. */
    [HttpGet("export/curvemapping")]
    public ActionResult GetCurveMapping(string? mapping)
    {
        if (!IsBaseProfileReady || MainProfile is null) return NotInitializedResponse;

        if (string.IsNullOrWhiteSpace(mapping)) mapping = DefaultCurveMapping;

        var path = mapping.SubstringBefore('.');
        var profile = FindBaseProfileForPath(path, found: out var found);

        if (!found) return NotFoundResponse;

        if (LoadExportOfType<UCurveExpressionsDataAsset>(profile.Provider, path) is not { } asset ||
            asset.ExpressionData?.ExpressionMap is not { } map)
        {
            return NotFoundResponse;
        }

        var entries = new List<object>(map.Count);

        foreach (var (target, expression) in map)
        {
            var constants = expression.Expression
                .Select(element => element.TryGet<FName>(out var name) ? name.Text : null)
                .Where(name => name is not null)
                .Distinct()
                .ToArray()!;

            var weighed = WeighConstants(expression, constants)
                .Select(pair => new { name = pair.Key, weight = pair.Value })
                .ToArray();

            if (weighed.Length == 0) continue;

            entries.Add(new { target = target.Text, constants = weighed });
        }

        if (entries.Count == 0) return NotFoundResponse;

        return new JsonResult(new
        {
            mapping = path,
            entries
        });
    }

    /* Whether the weights above are the whole of what the expression does.
     *
     * A weight is only worth having if the expression is the sum those weights describe, and most
     * of these are: a handful of the rig's controls, each worth so much, added up and held between
     * nothing and one. Some are not, and a consumer that rebuilt one of those out of its weights
     * would be building something the head never asked for.
     *
     * Settled by running the thing rather than by reading it. The expression is evaluated at a
     * spread of constant values and compared against the sum, and against the sum clamped, and it
     * is called whichever of those it matches everywhere. Matching nowhere is "other", which is a
     * consumer's cue to leave it alone.
     *
     * The spread is fixed rather than random, so the same asset answers the same way every time. */
    private static string DescribeForm(FExpressionObject expression, string[] constants, Dictionary<string, float> weights)
    {
        if (constants.Length == 0) return "other";

        var linear = true;
        var clamped = true;

        /* Enough of the corners and the middle to catch a clamp, and to catch a product or a
         * minimum pretending to be a sum while every constant is set to the same thing */
        float[][] spreads =
        [
            constants.Select(_ => 1f).ToArray(),
            constants.Select(_ => 0.5f).ToArray(),
            constants.Select((_, index) => index % 2 == 0 ? 1f : 0f).ToArray(),
            constants.Select((_, index) => index % 3 == 0 ? 0.8f : 0.25f).ToArray(),
            constants.Select((_, index) => (index + 1) / (float)(constants.Length + 1)).ToArray()
        ];

        foreach (var spread in spreads)
        {
            var at = new Dictionary<string, float>(constants.Length);

            for (var index = 0; index < constants.Length; index++)
            {
                at[constants[index]] = spread[index];
            }

            var actual = CurveExpressionText.Evaluate(expression, name => at.GetValueOrDefault(name, 0f));

            var sum = constants.Sum(name => weights.GetValueOrDefault(name, 0f) * at[name]);

            if (!float.IsFinite(actual)) return "other";

            if (Math.Abs(actual - sum) > 0.0001f) linear = false;
            if (Math.Abs(actual - Math.Clamp(sum, 0f, 1f)) > 0.0001f) clamped = false;

            if (!linear && !clamped) return "other";
        }

        /* Linear first: an expression that never leaves nought to one satisfies both, and the
         * plainer reading is the one that is true of it everywhere rather than only here */
        return linear ? "linear" : "clamped";
    }

    private static Dictionary<string, float> WeighConstants(FExpressionObject expression, string[] constants)
    {
        var weights = new Dictionary<string, float>(constants.Length);

        foreach (var constant in constants)
        {
            var weight = CurveExpressionText.Evaluate(expression, name =>
                string.Equals(name, constant, StringComparison.Ordinal) ? 1f : 0f);

            if (float.IsFinite(weight) && weight != 0f)
            {
                weights[constant] = weight;
            }
        }

        return weights;
    }
}
