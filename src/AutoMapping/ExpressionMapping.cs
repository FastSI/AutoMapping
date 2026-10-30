using System.Linq.Expressions;
using AutoMapping.Internal;

namespace AutoMapping;

/// <summary>
/// Rewrites a predicate or selector written against the destination type (usually a DTO) so it runs against the source type
/// (usually an entity). The equivalent of AutoMapper.Extensions.ExpressionMapping's <c>MapExpression</c>.
/// </summary>
public static class ExpressionMappingExtensions
{
    /// <summary>
    /// <c>mapper.MapExpression&lt;Order, OrderDto, bool&gt;(d =&gt; d.CustomerName == "Ann")</c> returns
    /// <c>o =&gt; o.Customer.Name == "Ann"</c>, using the Order -> OrderDto map. The result can be given to EF Core.
    /// </summary>
    public static Expression<Func<TSource, TResult>> MapExpression<TSource, TDestination, TResult>(
        this IMapper mapper, Expression<Func<TDestination, TResult>> expression)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return mapper.ConfigurationProvider.MapExpression<TSource, TDestination, TResult>(expression);
    }

    public static Expression<Func<TSource, TResult>> MapExpression<TSource, TDestination, TResult>(
        this IConfigurationProvider provider, Expression<Func<TDestination, TResult>> expression)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(expression);
        var config = provider as MapperConfiguration ?? throw new ArgumentException("MapExpression needs a AutoMapping configuration.", nameof(provider));
        return (Expression<Func<TSource, TResult>>)MapLambda(config, typeof(TSource), typeof(TDestination), expression);
    }

    /// <summary>Non-generic core, also used by the OData support.</summary>
    internal static LambdaExpression MapLambda(MapperConfiguration config, Type source, Type destination, LambdaExpression expression)
    {
        // the projection is "entity => new Dto { A = e.X, B = e.Y.Z }": every dto.A in the expression becomes its binding
        var projection = config.GetProjection(source, destination);
        var rewritten = new DestinationToSource(projection.Body, destination).Visit(
            ParameterReplacer.Replace(expression.Body, expression.Parameters[0], projection.Body));
        var delegateType = typeof(Func<,>).MakeGenericType(source, expression.Body.Type);
        return Expression.Lambda(delegateType, rewritten, projection.Parameters[0]);
    }

    /// <summary>Resolves "member of an object initializer" to the expression that initializes it.</summary>
    private sealed class DestinationToSource : ExpressionVisitor
    {
        private readonly Type _destination;
        public DestinationToSource(Expression _, Type destination) => _destination = destination;

        protected override Expression VisitMember(MemberExpression node)
        {
            var inner = Visit(node.Expression);
            var resolved = Resolve(inner, node);
            return resolved ?? node.Update(inner);
        }

        private static Expression? Resolve(Expression? target, MemberExpression node)
        {
            switch (target)
            {
                case MemberInitExpression init:
                    foreach (var binding in init.Bindings)
                        if (binding is MemberAssignment a && a.Member.Name == node.Member.Name)
                            return a.Expression.Type == node.Type ? a.Expression : Expression.Convert(a.Expression, node.Type);
                    throw new MappingConfigurationException($"{init.Type.Name}.{node.Member.Name} is not mapped, so it cannot be used in a mapped expression.");

                // nested object projections are wrapped as "src == null ? default : new Dto { ... }"
                case ConditionalExpression { IfFalse: MemberInitExpression nested } cond:
                {
                    var member = Resolve(nested, node)!;
                    var nullable = member.Type.IsValueType && Nullable.GetUnderlyingType(member.Type) == null
                        ? typeof(Nullable<>).MakeGenericType(member.Type) : member.Type;
                    var value = nullable == member.Type ? member : Expression.Convert(member, nullable);
                    var result = Expression.Condition(cond.Test, Expression.Default(nullable), value, nullable);
                    return nullable == node.Type ? result : Expression.Convert(result, node.Type);
                }
                default:
                    return null;
            }
        }
    }
}
