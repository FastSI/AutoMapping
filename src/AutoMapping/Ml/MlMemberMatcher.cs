using System.Numerics;
using Microsoft.ML;

namespace AutoMapping.Internal;

/// <summary>
/// Fuzzy member-name matching powered by ML.NET. Names are split into words and featurized with
/// <c>TextCatalog.FeaturizeText</c> (word + character n-grams); a destination member with no exact source gets the source
/// member whose feature vector has the highest cosine similarity, if it clears the threshold and is not ambiguous.
/// This runs once while the configuration is built and is never part of a mapping call.
/// </summary>
internal sealed class MlMemberMatcher
{
    // Public (inside an internal type, so still not exposed) because ML.NET reflects over these for its schema.
    public sealed class NameRow
    {
        public string Text { get; set; } = string.Empty;
    }

    public sealed class NameVector
    {
        public float[] Features { get; set; } = Array.Empty<float>();
    }

    private readonly MLContext _ml = new(seed: 1);

    public readonly record struct Suggestion(string SourceName, float Score);

    public Dictionary<string, Suggestion> Match(
        IReadOnlyList<string> destinationNames,
        IReadOnlyList<string> sourceNames,
        MlMatchingOptions options)
    {
        var result = new Dictionary<string, Suggestion>(StringComparer.Ordinal);
        if (destinationNames.Count == 0 || sourceNames.Count == 0) return result;

        // One shared vocabulary so destination and source vectors live in the same space.
        var texts = destinationNames.Concat(sourceNames).Select(ToText).Distinct(StringComparer.Ordinal).ToList();
        var data = _ml.Data.LoadFromEnumerable(texts.Select(t => new NameRow { Text = t }));
        var model = _ml.Transforms.Text.FeaturizeText("Features", nameof(NameRow.Text)).Fit(data);
        var vectors = _ml.Data
            .CreateEnumerable<NameVector>(model.Transform(data), reuseRowObject: false)
            .Select(v => v.Features)
            .ToList();

        var byText = new Dictionary<string, float[]>(StringComparer.Ordinal);
        for (var i = 0; i < texts.Count; i++) byText[texts[i]] = vectors[i];

        var sourceVectors = sourceNames.Select(n => byText[ToText(n)]).ToArray();

        foreach (var destination in destinationNames)
        {
            var dv = byText[ToText(destination)];
            var best = -1f;
            var second = -1f;
            var bestIndex = -1;

            for (var i = 0; i < sourceVectors.Length; i++)
            {
                var score = Cosine(dv, sourceVectors[i]);
                if (score > best)
                {
                    second = best;
                    best = score;
                    bestIndex = i;
                }
                else if (score > second)
                {
                    second = score;
                }
            }

            if (bestIndex >= 0 && best >= options.Threshold && (second < 0 || best - second >= options.MinMargin))
                result[destination] = new Suggestion(sourceNames[bestIndex], best);
        }
        return result;
    }

    private static string ToText(string name) =>
        string.Join(' ', MemberMatcher.SplitWords(name).Select(w => w.ToLowerInvariant()));

    /// <summary>SIMD cosine similarity.</summary>
    internal static float Cosine(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n == 0) return 0f;

        var width = Vector<float>.Count;
        var vDot = Vector<float>.Zero;
        var vA = Vector<float>.Zero;
        var vB = Vector<float>.Zero;
        var i = 0;
        for (; i <= n - width; i += width)
        {
            var x = new Vector<float>(a, i);
            var y = new Vector<float>(b, i);
            vDot += x * y;
            vA += x * x;
            vB += y * y;
        }

        var dot = Vector.Sum(vDot);
        var na = Vector.Sum(vA);
        var nb = Vector.Sum(vB);
        for (; i < n; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na <= 0f || nb <= 0f ? 0f : dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }
}
