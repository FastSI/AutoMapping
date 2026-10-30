using AutoMapping;
using Xunit;

namespace AutoMapping.Tests;

public class LnSrc { public int Id { get; set; } public string Name { get; set; } = ""; public LnInner Inner { get; set; } = new(); public List<int> Nums { get; set; } = new(); }
public class LnInner { public string City { get; set; } = ""; }
public class LnDst { public int Id { get; set; } public string Name { get; set; } = ""; public LnInnerDto Inner { get; set; } = new(); public List<int> Nums { get; set; } = new(); }
public class LnInnerDto { public string City { get; set; } = ""; }

public class LnTree { public string Name { get; set; } = ""; public LnTree? Child { get; set; } }
public class LnTreeDto { public string Name { get; set; } = ""; public LnTreeDto? Child { get; set; } }

public class LeanTests
{
    [Fact]
    public void Lean_and_general_paths_give_identical_results()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<LnSrc, LnDst>();
            c.CreateMap<LnInner, LnInnerDto>();
        });
        var src = new LnSrc { Id = 3, Name = "n", Inner = new LnInner { City = "Oslo" }, Nums = { 1, 2, 3 } };

        var lean = cfg.CreateMapper().Map<LnDst>(src);
        var general = new MapperConfiguration(c => { c.CreateMap<LnSrc, LnDst>(); c.CreateMap<LnInner, LnInnerDto>(); });
        general.DisableLeanPath = true;
        var slow = general.CreateMapper().Map<LnDst>(src);

        Assert.Equal(slow.Id, lean.Id);
        Assert.Equal(slow.Inner.City, lean.Inner.City);
        Assert.Equal(slow.Nums.Count, lean.Nums.Count);
    }

    [Fact]
    public void Depth_is_restored_when_a_resolver_swallows_a_nested_failure()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<LnInner, LnInnerDto>().ForMember(d => d.City, o => o.MapFrom(new Func<LnInner, LnInnerDto, string>((s, d) => throw new InvalidOperationException("boom"))));
            c.CreateMap<LnSrc, LnDst>()
                .ForMember(d => d.Inner, o => o.Ignore())
                .ForMember(d => d.Name, o => o.MapFrom((s, d, m, ctx) =>
                {
                    try { ctx.Mapper.Map<LnInnerDto>(s.Inner); } catch (Exception) { /* swallowed */ }
                    return s.Name;
                }));
        });
        var mapper = cfg.CreateMapper();
        for (var i = 0; i < 400; i++) Assert.Equal("n", mapper.Map<LnDst>(new LnSrc { Name = "n" }).Name);   // would exceed MaxDepth if depth leaked
    }

    [Fact]
    public void ProjectTo_recursive_map_without_MaxDepth_throws_clear_error()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<LnTree, LnTreeDto>());
        var ex = Assert.Throws<MappingConfigurationException>(() => new List<LnTree>().AsQueryable().ProjectTo<LnTreeDto>(cfg));
        Assert.Contains("MaxDepth", ex.Message);
    }

    [Fact]
    public void ProjectTo_recursive_map_honors_MaxDepth()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<LnTree, LnTreeDto>().MaxDepth(3));
        var data = new List<LnTree> { new() { Name = "a", Child = new() { Name = "b", Child = new() { Name = "c", Child = new() { Name = "d" } } } } };
        var dto = data.AsQueryable().ProjectTo<LnTreeDto>(cfg).Single();
        Assert.Equal("a", dto.Name);
        Assert.Equal("c", dto.Child!.Child!.Name);
        Assert.Null(dto.Child.Child.Child);
    }

    [Fact]
    public void Dictionary_to_object_never_writes_non_public_setters()
    {
        var mapper = new MapperConfiguration(c => { }).CreateMapper();
        var bag = new Dictionary<string, object?> { ["Name"] = "x", ["Secret"] = "pwned", ["Role"] = "admin" };
        var dto = mapper.Map<LnGuarded>(bag);
        Assert.Equal("x", dto.Name);
        Assert.Equal("user", dto.Secret);
        Assert.Equal("user", dto.Role);
    }
}

public class LnGuarded
{
    public string Name { get; set; } = "";
    public string Secret { get; private set; } = "user";
    public string Role { get; internal set; } = "user";
}
