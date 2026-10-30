namespace AutoMapping.Tests;

// ---- resolvers / converters / actions
public class Stock { public string Name { get; set; } = ""; public int Qty { get; set; } public decimal Price { get; set; } }
public class StockDto
{
    public string Label { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Total { get; set; }
    public string Upper { get; set; } = "";
}

public class Prefixer { public string Prefix => "item:"; }

public class LabelResolver : IValueResolver<Stock, StockDto, string>
{
    private readonly Prefixer _prefixer;
    public LabelResolver(Prefixer prefixer) => _prefixer = prefixer;
    public string Resolve(Stock source, StockDto destination, string destMember, ResolutionContext context) => _prefixer.Prefix + source.Name;
}

public class TotalResolver : IMemberValueResolver<Stock, StockDto, int, decimal>
{
    public decimal Resolve(Stock source, StockDto destination, int sourceMember, decimal destMember, ResolutionContext context) => sourceMember * source.Price;
}

public class UpperConverter : IValueConverter<string, string>
{
    public string Convert(string sourceMember, ResolutionContext context) => sourceMember.ToUpperInvariant();
}

public class StockConverter : ITypeConverter<Stock, StockDto>
{
    public StockDto Convert(Stock source, StockDto destination, ResolutionContext context) => new() { Label = "conv:" + source.Name };
}

public class StampAction : IMappingAction<Stock, StockDto>
{
    public void Process(Stock source, StockDto destination, ResolutionContext context) =>
        destination.Name = (context.Items.TryGetValue("tag", out var tag) ? tag : "none") + ":" + source.Name;
}

// ---- general
public class Person
{
    public string First { get; set; } = "";
    public string? Last { get; set; }
    public int Age { get; set; }
    public Address? Home { get; set; }
    public string? Notes { get; set; }
}

public class PersonDto
{
    public string First { get; set; } = "";
    public string? Last { get; set; }
    public int Age { get; set; }
    public string? HomeCity { get; set; }
    public string? Town { get; set; }
    public string? Notes { get; set; }
}

public class Price { public decimal Value { get; set; } }
public record Money(decimal Amount, string Currency);

public class Flat { public string Name { get; set; } = ""; public string AddressCity { get; set; } = ""; public string AddressStreet { get; set; } = ""; }
public class Nested { public string Name { get; set; } = ""; public Address? Address { get; set; } }

public class Inner { public string Title { get; set; } = ""; }
public class Outer { public int Id { get; set; } public Inner? Inner { get; set; } }
public class OuterDto { public int Id { get; set; } public string Title { get; set; } = ""; }

public class WideSource { public string Used { get; set; } = ""; public string Unused { get; set; } = ""; }
public class NarrowDest { public string Used { get; set; } = ""; }

public class ListSrc { public List<int> Numbers { get; set; } = new(); public Dictionary<string, int> Lookup { get; set; } = new(); }
public class ListDst
{
    public List<int> Numbers { get; } = new() { 99 };
    public Dictionary<string, int> Lookup { get; } = new() { ["old"] = 1 };
}

public class ReuseSrc { public List<int> Numbers { get; set; } = new(); }
public class ReuseDst { public List<int> Numbers { get; set; } = new(); }

public class Zoo { public Animal? Pet { get; set; } }
public class ZooDto { public AnimalDto? Pet { get; set; } }

public class Cust2 { public int Id { get; set; } public string Name { get; set; } = ""; public List<Order> Orders { get; set; } = new(); }
public class Cust2Dto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<OrderDto> Orders { get; set; } = new();
    public string Tag { get; set; } = "";
}

public class Ordered { public int A { get; set; } public int B { get; set; } public int C { get; set; } }

public interface IThing { string Name { get; } }
public class Thing : IThing { public string Name { get; set; } = ""; }
public class ThingSrc : IThing { public string Name { get; set; } = ""; }
public class ThingDto { public string Name { get; set; } = ""; }

public class Page<T> { public List<T> Items { get; set; } = new(); public int Total { get; set; } }
public class PageDto<T> { public List<T> Items { get; set; } = new(); public int Total { get; set; } }
public class PageConverter<TS, TD> : ITypeConverter<Page<TS>, PageDto<TD>>
{
    public PageDto<TD> Convert(Page<TS> source, PageDto<TD> destination, ResolutionContext context) => new() { Total = source.Total + 100 };
}

public class Holder { public Order? FirstOrder { get; set; } public string Name { get; set; } = ""; }
public class HolderDto { public OrderDto? FirstOrder { get; set; } public string Name { get; set; } = ""; }

// ---- naming conventions
public class PrefixedSource { public string SrcName { get; set; } = ""; public string TitleValue { get; set; } = ""; public string Äge { get; set; } = ""; }
public class PrefixedDest { public string Name { get; set; } = ""; public string Title { get; set; } = ""; public string Age { get; set; } = ""; public string InternalNote { get; set; } = "untouched"; }

public class DestPrefixSource { public string Title { get; set; } = ""; }
public class DestPrefixDest { public string DestTitle { get; set; } = ""; }

public class SecretSource { public string Open { get; set; } = ""; public string Secret { get; set; } = ""; }
public class SecretDest { public string Open { get; set; } = ""; public string Secret { get; set; } = "kept"; }

public class SnakeDest { public string address_city { get; set; } = ""; }
public class AddressHolder { public Address? Address { get; set; } }

public class TextSrc { public string? A { get; set; } public string? B { get; set; } }
public class TextDst { public string? A { get; set; } public string? B { get; set; } }

public class StrictProfile : Profile
{
    public StrictProfile()
    {
        AllowNullCollections = true;
        CreateMap<Customer, CustomerDto>().ForMember(d => d.FullName, o => o.Ignore());
    }
}

// ---- gap-closing features
public sealed class MPrefixConvention : INamingConvention
{
    public System.Text.RegularExpressions.Regex SplittingExpression { get; } = new(@"(?<=^m_)[A-Za-z]+");
    public string SeparatorCharacter => "";
}
public class MPrefixed { public string m_Name { get; set; } = ""; public int m_Age { get; set; } }
public class MPlain { public string Name { get; set; } = ""; public int Age { get; set; } }

public class PrivDest
{
    public string First { get; private set; } = "";
    public int Age { get; internal set; }
    public string? Notes { get; protected set; }
}

public class BagSrc { public Dictionary<string, object?> Data { get; set; } = new(); }
public class BagDst { public PersonDto? Data { get; set; } }

// ---- second gap batch
public class AliasSrc { public string Nick { get; set; } = ""; }
public class AliasDst { public string Handle { get; set; } = ""; }

public class CtorChoice
{
    public string? Name { get; set; }
    public string Via { get; set; } = "default";
    public CtorChoice() { }
    public CtorChoice(string name) { Name = name; Via = "param"; }
}
public class CtorChoiceSrc { public string Name { get; set; } = ""; }

public class Outer2 { public Inner2? In { get; set; } }
public class Inner2 { public int V { get; set; } }
public class Outer2Dto { public Inner2Dto? In { get; set; } }
public class Inner2Dto { public int V { get; set; } public string Extra { get; set; } = ""; }

public class ItemSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class ItemDst { public int Id { get; set; } public string Name { get; set; } = ""; }
public class ParentSrc { public List<ItemSrc> Items { get; set; } = new(); }
public class ParentDst { public List<ItemDst> Items { get; set; } = new(); }

public enum ColorA { Red, Green, Blue }
public enum ColorB { Red, Green, Crimson }
public enum NumA { One = 1, Two = 2 }
public enum NumB { Uno = 1, Dos = 2 }
