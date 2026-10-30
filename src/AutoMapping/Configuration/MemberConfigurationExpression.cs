using System.Linq.Expressions;
using System.Reflection;
using AutoMapping.Internal;

namespace AutoMapping;

internal static class ConfigHelpers
{
    /// <summary>Builds <c>s =&gt; s.A.B</c> from a dotted member path (case-insensitive; fields, properties, parameterless methods).</summary>
    public static LambdaExpression MemberLambda(Type sourceType, string path)
    {
        var p = Expression.Parameter(sourceType, "s");
        Expression body = p;
        foreach (var part in path.Split('.'))
        {
            var members = body.Type.GetMember(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var member = members.FirstOrDefault(m => m is PropertyInfo or FieldInfo)
                         ?? members.OfType<MethodInfo>().FirstOrDefault(m => m.GetParameters().Length == 0 && m.ReturnType != typeof(void))
                         ?? throw new MappingConfigurationException($"'{path}' is not a member of {TypeHelpers.Pretty(sourceType)} ('{part}' not found on {TypeHelpers.Pretty(body.Type)}).");
            body = member is MethodInfo method ? Expression.Call(body, method) : Expression.MakeMemberAccess(body, member);
        }
        return Expression.Lambda(body, p);
    }

    public static Expression<Func<TSource, TResult>> MemberLambda<TSource, TResult>(string path)
    {
        var lambda = MemberLambda(typeof(TSource), path);
        var body = lambda.Body.Type == typeof(TResult) ? lambda.Body : Expression.Convert(lambda.Body, typeof(TResult));
        return Expression.Lambda<Func<TSource, TResult>>(body, lambda.Parameters);
    }

    public static Expression StripConvert(Expression e)
    {
        while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u) e = u.Operand;
        return e;
    }

    /// <summary>Members from the parameter to the leaf: <c>d =&gt; d.A.B</c> gives [A, B]; null if the expression is not a plain member chain.</summary>
    public static List<MemberInfo>? MemberChain(Expression body)
    {
        var chain = new List<MemberInfo>();
        var e = StripConvert(body);
        while (e is MemberExpression { Member: PropertyInfo or FieldInfo } m)
        {
            chain.Insert(0, m.Member);
            e = m.Expression is null ? null! : StripConvert(m.Expression);
        }
        return e is ParameterExpression && chain.Count > 0 ? chain : null;
    }

    public static Type TypeOf(MemberInfo member) => member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
}

internal sealed class MemberConfigurationExpression<TSource, TDestination, TMember>
    : IMemberConfigurationExpression<TSource, TDestination, TMember>
{
    private readonly PropertyMap _map;

    public MemberConfigurationExpression(PropertyMap map) => _map = map;

    private IMemberConfigurationExpression<TSource, TDestination, TMember> Self => this;

    /// <summary>A new source replaces whatever source was configured before.</summary>
    private void ResetSource()
    {
        _map.CustomSource = null;
        _map.Resolver = null;
        _map.ResolverSource = null;
        _map.ResolverKind = ResolverKind.Plain;
        _map.HasConstant = false;
        _map.Constant = null;
        _map.ConstantType = null;
        _map.Ignored = false;
    }

    private IMemberConfigurationExpression<TSource, TDestination, TMember> SetResolver(Delegate resolver, ResolverKind kind, LambdaExpression? source)
    {
        ResetSource();
        _map.Resolver = resolver;
        _map.ResolverKind = kind;
        _map.ResolverSource = source;
        return this;
    }

    // ---- MapFrom ---------------------------------------------------------------------------------------

    public IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom<TResult>(Expression<Func<TSource, TResult>> sourceMember)
    {
        ArgumentNullException.ThrowIfNull(sourceMember);
        ResetSource();
        _map.CustomSource = sourceMember;
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom<TResult>(Func<TSource, TDestination, TResult> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return SetResolver(new Func<TSource, TDestination, TMember, ResolutionContext, TResult>((s, d, _, _) => resolver(s, d)), ResolverKind.Plain, null);
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom<TResult>(Func<TSource, TDestination, TMember, TResult> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return SetResolver(new Func<TSource, TDestination, TMember, ResolutionContext, TResult>((s, d, m, _) => resolver(s, d, m)), ResolverKind.Plain, null);
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom<TResult>(Func<TSource, TDestination, TMember, ResolutionContext, TResult> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return SetResolver(resolver, ResolverKind.Plain, null);
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom(IValueResolver<TSource, TDestination, TMember> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return SetResolver(new Func<TSource, TDestination, TMember, ResolutionContext, TMember>(resolver.Resolve), ResolverKind.Plain, null);
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom<TValueResolver>()
        where TValueResolver : IValueResolver<TSource, TDestination, TMember> =>
        SetResolver(new Func<TSource, TDestination, TMember, ResolutionContext, TMember>(
            (s, d, m, c) => ((IValueResolver<TSource, TDestination, TMember>)c.CreateInstance(typeof(TValueResolver))).Resolve(s, d, m, c)),
            ResolverKind.Plain, null);

    public IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom<TValueResolver, TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember)
        where TValueResolver : IMemberValueResolver<TSource, TDestination, TSourceMember, TMember>
    {
        ArgumentNullException.ThrowIfNull(sourceMember);
        return SetResolver(new Func<TSource, TDestination, TSourceMember, TMember, ResolutionContext, TMember>(
            (s, d, sm, m, c) => ((IMemberValueResolver<TSource, TDestination, TSourceMember, TMember>)c.CreateInstance(typeof(TValueResolver))).Resolve(s, d, sm, m, c)),
            ResolverKind.SourceMember, sourceMember);
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom<TValueResolver, TSourceMember>(string sourceMemberName)
        where TValueResolver : IMemberValueResolver<TSource, TDestination, TSourceMember, TMember> =>
        MapFrom<TValueResolver, TSourceMember>(ConfigHelpers.MemberLambda<TSource, TSourceMember>(sourceMemberName));

    public IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom(string sourceMemberName)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceMemberName);
        ResetSource();
        _map.CustomSource = ConfigHelpers.MemberLambda(typeof(TSource), sourceMemberName);
        return this;
    }

    // ---- ConvertUsing (value converters) -----------------------------------------------------------------

    public IMemberConfigurationExpression<TSource, TDestination, TMember> ConvertUsing<TValueConverter, TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember)
        where TValueConverter : IValueConverter<TSourceMember, TMember> =>
        SetResolver(new Func<TSourceMember, ResolutionContext, TMember>(
            (sm, c) => ((IValueConverter<TSourceMember, TMember>)c.CreateInstance(typeof(TValueConverter))).Convert(sm, c)),
            ResolverKind.Converter, sourceMember);

    public IMemberConfigurationExpression<TSource, TDestination, TMember> ConvertUsing<TValueConverter, TSourceMember>(string sourceMemberName)
        where TValueConverter : IValueConverter<TSourceMember, TMember> =>
        ConvertUsing<TValueConverter, TSourceMember>(ConfigHelpers.MemberLambda<TSource, TSourceMember>(sourceMemberName));

    public IMemberConfigurationExpression<TSource, TDestination, TMember> ConvertUsing<TValueConverter, TSourceMember>()
        where TValueConverter : IValueConverter<TSourceMember, TMember> =>
        SetResolver(new Func<TSourceMember, ResolutionContext, TMember>(
            (sm, c) => ((IValueConverter<TSourceMember, TMember>)c.CreateInstance(typeof(TValueConverter))).Convert(sm, c)),
            ResolverKind.Converter, null);

    public IMemberConfigurationExpression<TSource, TDestination, TMember> ConvertUsing<TSourceMember>(
        IValueConverter<TSourceMember, TMember> converter, Expression<Func<TSource, TSourceMember>> sourceMember)
    {
        ArgumentNullException.ThrowIfNull(converter);
        return SetResolver(new Func<TSourceMember, ResolutionContext, TMember>(converter.Convert), ResolverKind.Converter, sourceMember);
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> ConvertUsing<TSourceMember>(IValueConverter<TSourceMember, TMember> converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        return SetResolver(new Func<TSourceMember, ResolutionContext, TMember>(converter.Convert), ResolverKind.Converter, null);
    }

    // ---- simple options -----------------------------------------------------------------------------------

    public IMemberConfigurationExpression<TSource, TDestination, TMember> UseValue<TValue>(TValue value)
    {
        ResetSource();
        _map.HasConstant = true;
        _map.Constant = value;
        _map.ConstantType = typeof(TValue);
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> Ignore()
    {
        _map.Ignored = true;
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> UseDestinationValue()
    {
        _map.UseDestinationValue = true;
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> DoNotUseDestinationValue()
    {
        _map.UseDestinationValue = false;
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> AllowNull()
    {
        _map.AllowNull = true;
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> DoNotAllowNull()
    {
        _map.AllowNull = false;
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> MapAtRuntime()
    {
        _map.MapAtRuntime = true;
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> ExplicitExpansion()
    {
        _map.ExplicitExpansion = true;
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> SetMappingOrder(int order)
    {
        _map.Order = order;
        return this;
    }

    // ---- conditions -----------------------------------------------------------------------------------------

    public IMemberConfigurationExpression<TSource, TDestination, TMember> MapWhen(Expression<Func<TSource, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var compiled = predicate.Compile();
        _map.WhenExpression = predicate;
        _map.PreCondition = new Func<TSource, TDestination, ResolutionContext, bool>((s, _, _) => compiled(s));
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> PreCondition(Func<TSource, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _map.WhenExpression = null;
        _map.PreCondition = new Func<TSource, TDestination, ResolutionContext, bool>((s, _, _) => predicate(s));
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> PreCondition(Func<TSource, ResolutionContext, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _map.WhenExpression = null;
        _map.PreCondition = new Func<TSource, TDestination, ResolutionContext, bool>((s, _, c) => predicate(s, c));
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> PreCondition(Func<TSource, TDestination, ResolutionContext, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _map.WhenExpression = null;
        _map.PreCondition = predicate;
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> Condition(Func<TSource, TDestination, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _map.Condition = new Func<TSource, TDestination, TMember, TMember, ResolutionContext, bool>((s, d, _, _, _) => predicate(s, d));
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> Condition(Func<TSource, TDestination, TMember, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _map.Condition = new Func<TSource, TDestination, TMember, TMember, ResolutionContext, bool>((s, d, sm, _, _) => predicate(s, d, sm));
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> Condition(Func<TSource, TDestination, TMember, TMember, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _map.Condition = new Func<TSource, TDestination, TMember, TMember, ResolutionContext, bool>((s, d, sm, dm, _) => predicate(s, d, sm, dm));
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> Condition(Func<TSource, TDestination, TMember, TMember, ResolutionContext, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _map.Condition = predicate;
        return this;
    }

    public IMemberConfigurationExpression<TSource, TDestination, TMember> NullSubstitute(object nullSubstitute)
    {
        _map.HasNullSubstitute = true;
        _map.NullSubstitute = nullSubstitute;
        return this;
    }
}

internal sealed class CtorParamConfigurationExpression<TSource, TDestination> : ICtorParamConfigurationExpression<TSource>
{
    private readonly PropertyMap _map;

    public CtorParamConfigurationExpression(PropertyMap map) => _map = map;

    public void MapFrom<TResult>(Expression<Func<TSource, TResult>> sourceMember)
    {
        ArgumentNullException.ThrowIfNull(sourceMember);
        _map.Resolver = null;
        _map.CustomSource = sourceMember;
    }

    public void MapFrom<TResult>(Func<TSource, ResolutionContext, TResult> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _map.CustomSource = null;
        _map.ResolverKind = ResolverKind.Plain;
        _map.Resolver = new Func<TSource, TDestination, object?, ResolutionContext, TResult>((s, _, _, c) => resolver(s, c));
    }

    public void MapFrom(string sourceMemberName)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceMemberName);
        _map.Resolver = null;
        _map.CustomSource = ConfigHelpers.MemberLambda(typeof(TSource), sourceMemberName);
    }
}

internal sealed class SourceMemberConfigurationExpression : ISourceMemberConfigurationExpression
{
    private readonly Action _doNotValidate;

    public SourceMemberConfigurationExpression(Action doNotValidate) => _doNotValidate = doNotValidate;

    public void DoNotValidate() => _doNotValidate();
}
