using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AutoMapping.Internal;

/// <summary>Allocation-conscious collection helpers called from compiled mappings. <c>elem == null</c> means identity.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class CollectionMappers
{
    /// <summary>A null context means the caller proved the element map is context-free.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static TD Apply<TS, TD>(Mapping<TS, TD> elem, TS item, ResolutionContext? ctx) =>
        ctx is null ? elem.MapLean(item, default!) : elem.Map(item, default!, ctx);

    private static int CountHint<T>(IEnumerable<T> source) => source switch
    {
        ICollection<T> c => c.Count,
        IReadOnlyCollection<T> r => r.Count,
        _ => 0
    };

    public static List<TD>? ToList<TS, TD>(IEnumerable<TS>? source, Mapping<TS, TD>? elem, ResolutionContext? ctx, bool allowNull)
    {
        if (source is null) return allowNull ? null : new List<TD>();

        if (source is List<TS> list)
        {
            var span = CollectionsMarshal.AsSpan(list);
            var result = new List<TD>(span.Length);
            if (elem is null)
                foreach (var item in span) result.Add((TD)(object)item!);
            else if (ctx is null)
            {
                // resolve the compiled delegate once instead of per element
                var lean = elem.LeanDelegate;
                foreach (var item in span) result.Add(lean(item, default!));
            }
            else
                foreach (var item in span) result.Add(elem.Map(item, default!, ctx));
            return result;
        }

        var output = new List<TD>(CountHint(source));
        if (elem is null)
            foreach (var item in source) output.Add((TD)(object)item!);
        else
            foreach (var item in source) output.Add(Apply(elem, item, ctx));
        return output;
    }

    public static TD[]? ToArray<TS, TD>(IEnumerable<TS>? source, Mapping<TS, TD>? elem, ResolutionContext? ctx, bool allowNull)
    {
        if (source is null) return allowNull ? null : Array.Empty<TD>();

        if (source is TS[] array)
        {
            var result = new TD[array.Length];
            if (elem is null)
                for (var i = 0; i < array.Length; i++) result[i] = (TD)(object)array[i]!;
            else if (ctx is null)
            {
                var lean = elem.LeanDelegate;
                for (var i = 0; i < array.Length; i++) result[i] = lean(array[i], default!);
            }
            else
                for (var i = 0; i < array.Length; i++) result[i] = elem.Map(array[i], default!, ctx);
            return result;
        }

        if (source is List<TS> list)
        {
            var span = CollectionsMarshal.AsSpan(list);
            var result = new TD[span.Length];
            if (elem is null)
                for (var i = 0; i < span.Length; i++) result[i] = (TD)(object)span[i]!;
            else if (ctx is null)
            {
                var lean = elem.LeanDelegate;
                for (var i = 0; i < span.Length; i++) result[i] = lean(span[i], default!);
            }
            else
                for (var i = 0; i < span.Length; i++) result[i] = elem.Map(span[i], default!, ctx);
            return result;
        }

        var buffer = new List<TD>(CountHint(source));
        foreach (var item in source)
            buffer.Add(elem is null ? (TD)(object)item! : Apply(elem, item, ctx));
        return buffer.ToArray();
    }

    public static HashSet<TD>? ToHashSet<TS, TD>(IEnumerable<TS>? source, Mapping<TS, TD>? elem, ResolutionContext? ctx, bool allowNull)
    {
        if (source is null) return allowNull ? null : new HashSet<TD>();
        var set = new HashSet<TD>();
        foreach (var item in source)
            set.Add(elem is null ? (TD)(object)item! : Apply(elem, item, ctx));
        return set;
    }

    public static ReadOnlyCollection<TD>? ToReadOnlyCollection<TS, TD>(IEnumerable<TS>? source, Mapping<TS, TD>? elem, ResolutionContext? ctx, bool allowNull)
    {
        var list = ToList(source, elem, ctx, allowNull);
        return list is null ? null : new ReadOnlyCollection<TD>(list);
    }

    /// <summary>Any concrete <c>ICollection&lt;T&gt;</c> with a public parameterless constructor (Collection&lt;T&gt;, ObservableCollection&lt;T&gt;, ...).</summary>
    public static TColl? ToCollection<TS, TD, TColl>(IEnumerable<TS>? source, Mapping<TS, TD>? elem, ResolutionContext? ctx, bool allowNull)
        where TColl : class, ICollection<TD>, new()
    {
        if (source is null) return allowNull ? null : new TColl();
        var coll = new TColl();
        foreach (var item in source)
            coll.Add(elem is null ? (TD)(object)item! : Apply(elem, item, ctx));
        return coll;
    }

    public static Dictionary<KD, VD>? ToDictionary<KS, VS, KD, VD>(
        IEnumerable<KeyValuePair<KS, VS>>? source,
        Mapping<KS, KD>? keyMap,
        Mapping<VS, VD>? valueMap,
        ResolutionContext? ctx,
        bool allowNull) where KD : notnull
    {
        if (source is null) return allowNull ? null : new Dictionary<KD, VD>();
        var result = new Dictionary<KD, VD>(CountHint(source));
        foreach (var pair in source)
        {
            var key = keyMap is null ? (KD)(object)pair.Key! : Apply(keyMap, pair.Key, ctx);
            var value = valueMap is null ? (VD)(object)pair.Value! : Apply(valueMap, pair.Value, ctx);
            result[key] = value;
        }
        return result;
    }

    /// <summary>
    /// Collection equivalency: items of <paramref name="existing"/> that match a source item (by <paramref name="same"/>) are updated in
    /// place, unmatched source items are mapped and added, unmatched existing items are removed.
    /// </summary>
    public static ICollection<TD>? Equivalent<TS, TD>(IEnumerable<TS>? source, ICollection<TD>? existing, Func<TS, TD, bool> same,
        Mapping<TS, TD> elem, ResolutionContext? ctx, bool allowNull)
    {
        if (existing is null) return ToList(source, elem, ctx, allowNull);
        if (source is null)
        {
            if (allowNull) return null;
            existing.Clear();
            return existing;
        }

        var unmatched = new List<TD>(existing);
        var additions = new List<TD>();
        foreach (var item in source)
        {
            var index = unmatched.FindIndex(candidate => same(item, candidate));
            if (index < 0)
            {
                additions.Add(Apply(elem, item, ctx));
                continue;
            }

            var current = unmatched[index];
            unmatched.RemoveAt(index);
            var updated = ctx is null ? elem.MapLean(item, current) : elem.Map(item, current, ctx);
            if (!ReferenceEquals(updated, current) && typeof(TD).IsValueType)
            {
                existing.Remove(current);
                additions.Add(updated);
            }
        }
        foreach (var gone in unmatched) existing.Remove(gone);
        foreach (var added in additions) existing.Add(added);
        return existing;
    }

    /// <summary>Clears <paramref name="target"/> and refills it (get-only collection members, UseDestinationValue).</summary>
    public static void Populate<TS, TD>(IEnumerable<TS>? source, ICollection<TD> target, Mapping<TS, TD>? elem, ResolutionContext? ctx)
    {
        target.Clear();
        if (source is null) return;
        foreach (var item in source)
            target.Add(elem is null ? (TD)(object)item! : Apply(elem, item, ctx));
    }

    public static void PopulateDictionary<KS, VS, KD, VD>(
        IEnumerable<KeyValuePair<KS, VS>>? source,
        IDictionary<KD, VD> target,
        Mapping<KS, KD>? keyMap,
        Mapping<VS, VD>? valueMap,
        ResolutionContext? ctx) where KD : notnull
    {
        target.Clear();
        if (source is null) return;
        foreach (var pair in source)
        {
            var key = keyMap is null ? (KD)(object)pair.Key! : Apply(keyMap, pair.Key, ctx);
            var value = valueMap is null ? (VD)(object)pair.Value! : Apply(valueMap, pair.Value, ctx);
            target[key] = value;
        }
    }
}
