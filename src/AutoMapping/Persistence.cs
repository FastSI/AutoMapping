using System.Linq.Expressions;
using System.Reflection;
using AutoMapping.Internal;

namespace AutoMapping;

/// <summary>
/// Insert / update / delete of entities from source objects, matched through <c>EqualityComparison</c>
/// (the equivalent of AutoMapper.Collection.EFCore's <c>Persist()</c>).
/// </summary>
public interface IPersistence<TDestination> where TDestination : class
{
    /// <summary>Updates the entity equivalent to <paramref name="source"/>, or adds a new one. Returns the entity.</summary>
    TDestination InsertOrUpdate<TSource>(TSource source) where TSource : class;

    /// <summary>Same for every item (named Range because an array would otherwise bind to the single-item overload).</summary>
    void InsertOrUpdateRange<TSource>(IEnumerable<TSource> sources) where TSource : class;

    /// <summary>Removes the entity equivalent to <paramref name="source"/>; returns false when none matches.</summary>
    bool Remove<TSource>(TSource source) where TSource : class;

    /// <summary>Same for every item; returns how many entities were removed.</summary>
    int RemoveRange<TSource>(IEnumerable<TSource> sources) where TSource : class;
}

public static class PersistenceExtensions
{
    /// <summary>
    /// Persistence over a query source that exposes <c>Add(T)</c> and <c>Remove(T)</c> (EF Core <c>DbSet&lt;T&gt;</c>, or any
    /// <see cref="ICollection{T}"/>). The equivalence predicate is sent to the query provider as an expression, so with EF Core
    /// it runs in the database. Call <c>SaveChanges</c> yourself afterwards.
    /// </summary>
    public static IPersistence<TDestination> Persist<TDestination>(this IQueryable<TDestination> set, IMapper mapper)
        where TDestination : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(mapper);
        return new Persistence<TDestination>(set, mapper);
    }

    private sealed class Persistence<TDestination> : IPersistence<TDestination> where TDestination : class
    {
        private readonly IQueryable<TDestination> _set;
        private readonly IMapper _mapper;
        private readonly MapperConfiguration _config;
        private readonly Action<TDestination> _add;
        private readonly Action<TDestination> _remove;

        public Persistence(IQueryable<TDestination> set, IMapper mapper)
        {
            _set = set;
            _mapper = mapper;
            _config = mapper.ConfigurationProvider as MapperConfiguration
                      ?? throw new ArgumentException("Persist needs a mapper created by AutoMapping.", nameof(mapper));

            if (set is ICollection<TDestination> collection)
            {
                _add = collection.Add;
                _remove = e => collection.Remove(e);
            }
            else
            {
                _add = Bind(set.GetType(), "Add", set);
                _remove = Bind(set.GetType(), "Remove", set);
            }
        }

        private static Action<TDestination> Bind(Type type, string name, object target)
        {
            var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == name && m.GetParameters() is { Length: 1 } p && p[0].ParameterType.IsAssignableFrom(typeof(TDestination)))
                ?? throw new NotSupportedException($"{type.Name} has no public {name}({typeof(TDestination).Name}) method; pass a DbSet<T> or an ICollection<T>.");
            return (Delegate.CreateDelegate(typeof(Action<TDestination>), target, method, throwOnBindFailure: false) as Action<TDestination>)
                   ?? (e => method.Invoke(target, new object[] { e }));   // methods that return a value (EF's Add returns EntityEntry)
        }

        private TDestination? Find<TSource>(TSource source)
        {
            var equality = _config.GetEqualityExpression(typeof(TSource), typeof(TDestination))
                           ?? throw new MappingConfigurationException(
                               $"Persist needs EqualityComparison on the map {typeof(TSource).Name} -> {typeof(TDestination).Name}.");
            var sourceParameter = equality.Parameters[0];
            var entityParameter = equality.Parameters[1];
            var body = ParameterReplacer.Replace(equality.Body, sourceParameter, Expression.Constant(source, sourceParameter.Type));
            var predicate = Expression.Lambda<Func<TDestination, bool>>(body, entityParameter);
            return _set.FirstOrDefault(predicate);
        }

        public TDestination InsertOrUpdate<TSource>(TSource source) where TSource : class
        {
            ArgumentNullException.ThrowIfNull(source);
            var existing = Find(source);
            if (existing != null) return _mapper.Map(source, existing);
            var created = _mapper.Map<TSource, TDestination>(source);
            _add(created);
            return created;
        }

        public void InsertOrUpdateRange<TSource>(IEnumerable<TSource> sources) where TSource : class
        {
            ArgumentNullException.ThrowIfNull(sources);
            foreach (var item in sources.ToList()) InsertOrUpdate(item);
        }

        public bool Remove<TSource>(TSource source) where TSource : class
        {
            ArgumentNullException.ThrowIfNull(source);
            var existing = Find(source);
            if (existing == null) return false;
            _remove(existing);
            return true;
        }

        public int RemoveRange<TSource>(IEnumerable<TSource> sources) where TSource : class
        {
            ArgumentNullException.ThrowIfNull(sources);
            return sources.ToList().Count(item => Remove(item));
        }
    }
}
