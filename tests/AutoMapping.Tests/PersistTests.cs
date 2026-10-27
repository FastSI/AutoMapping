using System.Collections;
using System.Linq.Expressions;
using AutoMapping;
using Xunit;

namespace AutoMapping.Tests;

public class PsEntity { public int Id { get; set; } public string Name { get; set; } = ""; }
public class PsDto { public int Id { get; set; } public string Name { get; set; } = ""; }

/// <summary>Stand-in for a DbSet: queryable, with Add/Remove that return a value like EF's.</summary>
public class PsSet : IQueryable<PsEntity>
{
    public List<PsEntity> Items { get; } = new();
    public object Add(PsEntity e) { Items.Add(e); return e; }
    public object Remove(PsEntity e) { Items.Remove(e); return e; }
    public Type ElementType => typeof(PsEntity);
    public Expression Expression => Items.AsQueryable().Expression;
    public IQueryProvider Provider => Items.AsQueryable().Provider;
    public IEnumerator<PsEntity> GetEnumerator() => Items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class PersistTests
{
    private static IMapper Mapper() => new MapperConfiguration(c =>
        c.CreateMap<PsDto, PsEntity>().EqualityComparison((dto, e) => dto.Id == e.Id)).CreateMapper();

    [Fact]
    public void InsertOrUpdate_adds_new_and_updates_existing()
    {
        var set = new PsSet();
        set.Items.Add(new PsEntity { Id = 1, Name = "old" });
        var persist = set.Persist(Mapper());

        var updated = persist.InsertOrUpdate(new PsDto { Id = 1, Name = "new" });
        persist.InsertOrUpdateRange(new[] { new PsDto { Id = 2, Name = "two" }, new PsDto { Id = 3, Name = "three" } });

        Assert.Same(set.Items[0], updated);
        Assert.Equal("new", set.Items[0].Name);
        Assert.Equal(new[] { 1, 2, 3 }, set.Items.Select(e => e.Id));
    }

    [Fact]
    public void Remove_deletes_equivalent_entity_only()
    {
        var set = new PsSet();
        set.Items.AddRange(new[] { new PsEntity { Id = 1 }, new PsEntity { Id = 2 } });
        var persist = set.Persist(Mapper());

        Assert.True(persist.Remove(new PsDto { Id = 1 }));
        Assert.False(persist.Remove(new PsDto { Id = 99 }));
        Assert.Equal(1, persist.RemoveRange(new[] { new PsDto { Id = 2 }, new PsDto { Id = 5 } }));
        Assert.Empty(set.Items);
    }

    [Fact]
    public void Works_on_plain_collections_and_requires_EqualityComparison()
    {
        var list = new ListQuery();
        Mapper().Let(m => list.Persist(m).InsertOrUpdate(new PsDto { Id = 7 }));
        Assert.Single(list);

        var bare = new MapperConfiguration(c => c.CreateMap<PsDto, PsEntity>()).CreateMapper();
        Assert.Throws<MappingConfigurationException>(() => list.Persist(bare).InsertOrUpdate(new PsDto { Id = 7 }));
    }

    private sealed class ListQuery : List<PsEntity>, IQueryable<PsEntity>
    {
        private IQueryable<PsEntity> Snapshot => new List<PsEntity>(this).AsQueryable();
        public Type ElementType => typeof(PsEntity);
        public Expression Expression => Snapshot.Expression;
        public IQueryProvider Provider => Snapshot.Provider;
    }
}

internal static class PsExt { public static void Let<T>(this T value, Action<T> action) => action(value); }
