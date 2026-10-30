using AutoMapping;
using AutoMapping.OData;
using Xunit;

// Stands in for the official package's types: the bridge identifies them by full name and reads RawValues by reflection.
namespace Microsoft.AspNetCore.OData.Query
{
    public class ODataRawQueryOptions
    {
        public string? Filter { get; set; } public string? OrderBy { get; set; } public string? Search { get; set; }
        public string? Select { get; set; } public string? Expand { get; set; } public string? Top { get; set; }
        public string? Skip { get; set; } public string? Count { get; set; } public string? Apply { get; set; }
    }
    public class ODataQueryOptions { public ODataRawQueryOptions RawValues { get; } = new(); }
    public class ODataQueryOptions<T> : ODataQueryOptions { }
}

namespace AutoMapping.Tests
{
    public class AspNetCoreODataBridgeTests
    {
        [Fact]
        public void Reads_raw_values_from_the_official_options_by_reflection()
        {
            var o = new Microsoft.AspNetCore.OData.Query.ODataQueryOptions<OdOrderDto>();
            o.RawValues.Filter = "Total gt 50"; o.RawValues.OrderBy = "Total desc"; o.RawValues.Top = "2"; o.RawValues.Count = "true";
            var ids = Enumerable.Range(1, 4).Select(i => new OdOrder { Id = i, Total = i * 40m }).AsQueryable()
                .Get<OdOrder, OdOrderDto>(new MapperConfiguration(c => c.CreateMap<OdOrder, OdOrderDto>()).CreateMapper(), o);
            Assert.Equal(new[] { 4, 3 }, ids.Items.Select(i => i.Id).ToArray());
            Assert.Equal(3, ids.Count);
        }

        [Fact]
        public void Rejects_apply_and_foreign_objects()
        {
            var o = new Microsoft.AspNetCore.OData.Query.ODataQueryOptions(); o.RawValues.Apply = "groupby((Id))";
            Assert.Throws<NotSupportedException>(() => AspNetCoreODataBridge.ToAutoMapping(o));
            Assert.Throws<ArgumentException>(() => AspNetCoreODataBridge.ToAutoMapping(new object()));
            Assert.Throws<ODataException>(() => { var b = new Microsoft.AspNetCore.OData.Query.ODataQueryOptions(); b.RawValues.Top = "-1"; AspNetCoreODataBridge.ToAutoMapping(b); });
        }
    }
}
