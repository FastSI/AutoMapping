# FastSI.AutoMapping reference

Fast object-to-object mapping for .NET with an AutoMapper-style API.

**Version 4.0.0** | Copyright (c) 2026 FastSI | MIT License | `net8.0` `net9.0` `net10.0`

- **Compiled mappings.** Each map is turned into one expression tree on first use. Same-type members are plain assignments, so there is no per-member reflection or dictionary lookup at runtime.
- **ML.NET assisted configuration.** Optional fuzzy member matching (ML.NET text featurization + SIMD cosine similarity), plus a mapping-plan explainer. It runs once while the configuration is built, never inside a `Map` call.
- **Production minded.** Thread-safe singleton mapper, startup validation (`AssertConfigurationIsValid`), optional eager compile (`CompileMappings`), cycle protection, DI integration.

```
dotnet add package FastSI.AutoMapping
```

## Quick start

```csharp
using AutoMapping;

var config = new MapperConfiguration(cfg =>
{
    cfg.CreateMap<Order, OrderDto>();
    cfg.CreateMap<Customer, CustomerDto>()
       .ForMember(d => d.FullName, o => o.MapFrom(s => s.FirstName + " " + s.LastName));
});

config.AssertConfigurationIsValid();   // fail at startup, not in production traffic
config.CompileMappings();              // optional: pay the compile cost now

IMapper mapper = config.CreateMapper();
CustomerDto dto = mapper.Map<CustomerDto>(customer);
```

### Dependency injection

```csharp
builder.Services.AddAutoMapping(typeof(Program).Assembly);          // scans for Profile classes
builder.Services.AddAutoMapping(cfg => cfg.UseMlMemberMatching());  // or configure inline

public class OrderProfile : Profile
{
    public OrderProfile() => CreateMap<Order, OrderDto>();
}
```

`MapperConfiguration` (also as `IConfigurationProvider`) is a singleton. `IMapper` is transient by default, like AutoMapper; pass `ServiceLifetime.Scoped` when resolvers depend on scoped services such as a `DbContext`.

## Production readiness

| Topic | Status |
|---|---|
| Supported runtimes | `net8.0`, `net9.0`, `net10.0` in one multi-targeted package; .NET 10 is the primary target |
| Tests | 171 automated tests run on all three runtimes (513 test runs), all passing for this release |
| Thread safety | One `MapperConfiguration` per application; the mapper is stateless and safe to share |
| Startup checks | `AssertConfigurationIsValid()` fails at startup, not under traffic |
| Versioning | [Semantic Versioning](https://semver.org): breaking changes only in a new major version |
| Dependencies | Microsoft.ML, SSH.NET and Microsoft.Extensions.* abstractions, all MIT |
| License | MIT, included in the package ([LICENSE](LICENSE)) |
| Data handling | The core touches no files or network and stores no mapped values; reports contain no mapped values (see Security below) |
| Symbols | `.snupkg` symbols package built alongside each release for source-level debugging |

Before you go live: build in Release, run `AssertConfigurationIsValid()` at startup, create one configuration and reuse it, and
benchmark your own types. The "Remaining limits" and "Verification status" sections below list what has not been tested against
real EF Core, SFTP servers or the official OData package.

## Alerts

Optional. `services.AddAutoMappingAlerts(o => { ... })` (namespace `AutoMapping.Alerting`) checks the telemetry collected by
`AddAutoMappingReporting` every `EvaluationInterval` (default 1 minute) and notifies the configured sinks. It only reads data: the
mapping path is not touched. Without reporting registered, resolving the alert service throws an `AlertConfigurationException`
that says so.

| Alert (`AlertKind`) | Warning | Critical | Property |
|---|---|---|---|
| `ErrorRate` | 1% of calls | 5% | `Thresholds.ErrorRateWarning` / `ErrorRateCritical` |
| `SlowMapping` (p95) | 1,000 µs | 10,000 µs | `Thresholds.SlowP95WarningMicroseconds` / `SlowP95CriticalMicroseconds` |
| `SlowCompile` (average) | 500 ms | n/a | `Thresholds.SlowCompileMilliseconds` |
| `GcPause` | 10% | 30% | `Thresholds.GcPausePercentWarning` / `GcPausePercentCritical` |
| `CacheGrowth` | 25% growth | n/a | `Thresholds.CacheGrowthFraction` |
| `MemoryLeak` | suspected | n/a | `ReportingOptions.Thresholds.Leak`, `AlertOptions.LeakWindow` |
| `ReportDelivery` | 3 failures in a row | 10 | `Thresholds.DeliveryFailuresWarning` / `DeliveryFailuresCritical` |

Rates, latency and compiles are judged over `Window` (default 5 minutes) as the difference of two telemetry snapshots, so old
failures do not keep alerting. Map pairs with fewer than `MinCallsForRates` (100) calls in the window are skipped.

**Lifecycle.** A condition is sent as `Firing` when it starts, again if it escalates to `Critical`, and again as a reminder after
`Cooldown` (30 minutes) while it persists. It is sent as `Resolved` once it has been absent for `ClearEvaluations` (2) checks in a
row (turn off with `NotifyOnResolved = false`). `MinSeverity` filters out warnings. `MaxActiveAlerts` (500) bounds memory.

**Sinks.**

```csharp
o.AddLog();                                              // ILogger: Warning, Error, Information (resolved)
o.AddCallback((alert, ct) => MyPager.SendAsync(alert, ct));
o.AddWebhook(new Uri("https://..."), signingSecret);     // https only, no redirects, optional HMAC signature
o.AddSmtp(s => { s.Host = ...; s.From = ...; s.To.Add(...); s.UserName = ...; s.Password = ...; });
o.AddSink(new MyAlertSink());                            // implement IAlertSink
```

The webhook signature uses the same scheme as report webhooks: `X-AutoMapping-Signature` is `sha256=` plus the hex HMAC-SHA256 of
`"{X-AutoMapping-Timestamp}.{body}"`; reject old timestamps on the receiving side. The body is the `Alert` record as camelCase JSON
with enum names as strings. Email uses `System.Net.Mail.SmtpClient` (no extra dependency) with TLS on by default; the password never
appears in exception messages, logs or `ToString()`, and the subject is forced onto one line.

**Safety.** Alerts carry type names and numbers only, never mapped values or exception messages. A failing sink is retried
(`Retry`, 3 attempts by default for transient errors) and logged; it never throws into the application and never blocks the other
sinks. `AlertService.ActiveAlerts` returns what is firing now, for a health endpoint. `AlertEvaluator.Evaluate` is a pure function if
you want to drive the checks yourself.

**Verification status.** Covered by 31 tests with fake clocks, sinks, a fake HTTP handler and a fake SMTP sender. **Not run here:** a
real SMTP server, a real webhook receiver (Teams, Slack, PagerDuty and so on) and a long-running host. Send one test alert in your
own environment before relying on it.

## Targets

`net8.0`, `net9.0`, `net10.0` (one package, multi-targeted; .NET 10 is the primary target).

## AutoMapper parity

| Area | Supported |
|---|---|
| Conventions | case/underscore-insensitive names, `GetXxx()` methods, flattening, `RecognizePrefixes/Postfixes`, `RecognizeDestinationPrefixes/Postfixes`, `ReplaceMemberName`, `AddGlobalIgnore`, `ShouldMapProperty/Field/Method`, destination naming convention |
| Members | `ForMember` (expression or name), `MapFrom` (expression / path string / delegates / resolver classes), `ConvertUsing` value converters, `Ignore`, `UseValue`, `NullSubstitute`, `Condition`, `PreCondition`, `MapWhen` (expression, ProjectTo-friendly), `ForAllMembers`, `ForAllOtherMembers`, `ForPath`, `ForCtorParam`, `ForSourceMember`/`DoNotValidate`, `IncludeMembers`, `SetMappingOrder`, `UseDestinationValue`, `AllowNull`/`DoNotAllowNull`, `MapAtRuntime`, `ExplicitExpansion` |
| Whole map | `ConvertUsing` (func / `ITypeConverter` / open generic converter), `ConstructUsing`, `ConstructUsingServiceLocator`, `BeforeMap`/`AfterMap` (delegates and `IMappingAction`), `As<T>`, `AddTransform`, global `ValueTransformers`, `MaxDepth`, `PreserveReferences`, `DisableCtorValidation` |
| Inheritance | `Include`, `IncludeBase`, `IncludeAllDerived`, interface/base-class map lookup |
| Generics | open generic maps (`CreateMap(typeof(Page<>), typeof(PageDto<>))`) |
| Reverse | `ReverseMap()` including nested `MapFrom` chains, `ForPath`, and un-flattening |
| Runtime | dictionary <-> object (property bags), non-public setters, `SourceMemberNamingConvention`, nested per-call options (hooks, items, service constructor), `IMapper.Map` with per-call `Items`, `BeforeMap`, `AfterMap`; `ResolutionContext.Mapper/Items/CreateInstance`; get-only collections/dictionaries filled in place |
| Profiles / DI | `Profile`, per-profile settings, `AddMaps`, `AddAutoMapping(...)` with scoped/transient/singleton `IMapper` and DI-created resolvers |
| Validation / diagnostics | `AssertConfigurationIsValid()` (+ per profile), `MemberList.Source/None`, `CompileMappings()`, `BuildExecutionPlan`, `GetMappingPlan` |
| Queries | `ProjectTo<T>(config, parameters, membersToExpand)` |
| Beyond AutoMapper | optional ML.NET fuzzy member matching, compiled expression trees |

### Also supported

`ConvertUsing(expression)` (also in `ProjectTo`), `ForAllMaps`, `RecognizeAlias`, `CreateProjection`, `ValidateInlineMaps`,
`EnableNullPropagationForQueryMapping`, collection equivalency (`EqualityComparison`, update/add/remove in place),
enum mapping (`ConvertUsingEnumMapping`, `EnableEnumMappingValidation`), `ShouldUseConstructor` (covered by a test).

> `ConvertUsing(Func<TSource, TDestination>)` is now `ConvertUsing(Expression<Func<...>>)`, as in current AutoMapper.
> Statement lambdas need the `(s, d, ctx)` overload. `EnableNullPropagationForQueryMapping` defaults to false, so in-memory
> `ProjectTo` on null nested members throws unless you turn it on.

### Performance

Each map compiles to one expression tree. Maps that need nothing from the `ResolutionContext` (no resolvers or hooks,
no `PreserveReferences`/`MaxDepth`, no cycles, no `CreateMissingTypeMaps`) run on a *lean* path that allocates no context.
Measured with BenchmarkDotNet 0.15.8 (Release build, default job, `MemoryDiagnoser`) on a 12th Gen Intel Core i7-12800H,
Windows 11. Time is shown relative to AutoMapper in the same run (lower is better), because absolute times on one machine
moved by up to 2x between runs (power state, background load). Ratios stayed stable.

| Scenario | Runtime | FastSI.AutoMapping vs AutoMapper | FastSI.AutoMapping vs hand-written |
|---|---|---|---|
| order + 10 lines, single | .NET 8 | 38% faster | about equal |
| order + 10 lines, single | .NET 10 | 37% faster | 7% slower |
| list of 1000 orders | .NET 8 | 27% faster | about equal |
| list of 1000 orders | .NET 10 | 29% faster | 3% slower |
| array + dictionary of 10 lines | .NET 8 | about equal (varied between runs) | n/a |
| array + dictionary of 10 lines | .NET 10 | 13% faster | n/a |

Memory per call (same in every run): single order **712 B** (AutoMapper 904 B, hand-written 784 B); 1000 orders **720 KB**
(AutoMapper 921 KB); array + dictionary **1.64 KB** (AutoMapper 2.13 KB).

What this does not show: one machine, one object shape per scenario, no concurrency test, and AutoMapper at the version
referenced by the benchmark project. Run `benchmarks/AutoMapping.Benchmarks` in Release on your own types before relying on
any figure here.

### Persist (AutoMapper.Collection.EFCore equivalent)

```csharp
cfg.CreateMap<ProductDto, Product>().EqualityComparison((dto, entity) => dto.Id == entity.Id);

var persist = db.Products.Persist(mapper);      // any IQueryable<T> with Add/Remove: DbSet<T> or ICollection<T>
persist.InsertOrUpdate(dto);                     // update the equivalent entity or add a new one
persist.InsertOrUpdateRange(dtos);
persist.Remove(dto);  persist.RemoveRange(dtos);
await db.SaveChangesAsync();                     // you control the transaction
```

The equality expression is sent to the query provider (translated to SQL by EF Core). AutoMapping has no EF Core dependency, so
`DbSet<T>` is bound through its public `Add`/`Remove`; this is tested against a stand-in set, **not against a real `DbContext`**.

Equality can also come from keys instead of a hand-written predicate:

```csharp
cfg.CreateMap<ProductDto, Product>().EqualityComparisonByKeys("TenantId", "Code");   // matched ignoring case on both types
cfg.UseEntityFrameworkCoreModel(dbContext);   // every map whose destination is an entity gets its primary key as equality
```

`UseEntityFrameworkCoreModel` reads the model through its public members (no EF Core reference); it is tested with a stand-in model
shaped like EF Core's, **not with a real `DbContext`**. Explicit `EqualityComparison` calls win; maps whose source lacks a key member are skipped.

### Expression mapping (AutoMapper.Extensions.ExpressionMapping equivalent)

```csharp
// the API speaks DTOs, the database speaks entities
Expression<Func<OrderDto, bool>> filter = d => d.CustomerName == "Ann" && d.Total > 10;
Expression<Func<Order, bool>> onEntity = mapper.MapExpression<Order, OrderDto, bool>(filter);   // o => o.Customer.Name == "Ann" && ...
var rows = db.Orders.Where(onEntity).ProjectTo<OrderDto>(config);
```

Uses the `Order -> OrderDto` map; members mapped with delegates cannot be rewritten and throw.

### OData query support (AutoMapper.AspNetCore.OData.EFCore equivalent)

```csharp
app.MapGet("/orders", (HttpRequest request, AppDb db, IMapper mapper) =>
{
    var options = ODataQueryOptions.Parse(request.QueryString.Value);          // ?$filter=Total gt 100&$orderby=CustomerName&$top=20&$count=true
    ODataResult<OrderDto> page = db.Orders.Get<Order, OrderDto>(mapper, options);
    return Results.Ok(new { page.Count, page.Items });
});
```

The client speaks DTO names; `$filter` and `$orderby` are translated onto the entity through the map (`MapExpression`), so with EF Core
they run in the database. Supported: `$filter` (`eq ne gt ge lt le and or not add sub mul div mod`, `contains startswith endswith tolower
toupper trim length concat indexof substring year month day hour minute second round floor ceiling`), `$orderby`, `$top`, `$skip`,
`$count`, `$search` (terms, `OR`, `NOT`, "phrases" over the DTO's string properties), `any()`/`all()` lambdas in `$filter` (`Lines/any(l: l/Price gt 5)`), `$select` (top-level), `$expand` (members marked `ExplicitExpansion`). `GetQuery` returns the unexecuted `IQueryable<TDto>`.

Query strings are untrusted: only public members of the DTO and the functions above are reachable, length and nesting are capped,
`$top` is capped (`ODataSettings.MaxTop`, default 1000) and errors are `ODataException` (map to HTTP 400).

#### Using the official `Microsoft.AspNetCore.OData` package

No extra AutoMapping package is needed. `AutoMapping` doesn't reference `Microsoft.AspNetCore.OData`; it reads the official options object by reflection, so you only install the OData package in your own web project. Pass the official options straight in to keep its model binding and routing:

```csharp
[HttpGet]
public IActionResult Get(ODataQueryOptions<OrderDto> options, [FromServices] IMapper mapper)
{
    ODataResult<OrderDto> page = db.Orders.Get<Order, OrderDto>(mapper, options);   // official options in, DTO page out
    return Ok(page);
}
```

It reads the raw `$filter/$orderby/$search/$top/$skip/$count/$select/$expand` values from the official options and runs them through
AutoMapping's engine, so the supported syntax is the one above (`$apply` throws `NotSupportedException`). **The bridge is tested
only against a stand-in class with the same full name and `RawValues` shape, because the real package could not be restored where
it was written. Try it once against the real `Microsoft.AspNetCore.OData` 9.x before relying on it.** It is not a replacement for `[EnableQuery]`
on endpoints that return entities directly.

### Remaining limits

- OData support is a built-in subset, not the `Microsoft.AspNetCore.OData` package: no `$apply`, and no nested options inside `$select`/`$expand`. It is tested in-memory, **not against EF Core SQL translation**.
- `ProjectTo` on a recursive map needs `MaxDepth(n)` (otherwise it throws a clear error); non-recursive nesting is limited to 32 levels.
- A `ForMember` setter for a private-set member is mapped in memory but not validated or projected (matches AutoMapper).

- `ProjectTo` cannot translate delegates (resolver classes, hooks, delegate `ConvertUsing`/`Condition`). Use `MapFrom(expression)`, `ConvertUsing(expression)` and `MapWhen(expression)`.
- `ProjectTo` skips get-only collections and non-public setters: an expression tree cannot express them. `MapAtRuntime` projects with the static type.
- Dictionary <-> object mapping covers `IDictionary<string, V>` only.

**Enterprise behaviour**

- **Never breaks mapping.** Observer callbacks are exception-safe; with no observer the mapper pays one null check (measured: unchanged 25 ns for the flat benchmark, same allocations). With reporting on, each top-level call costs two clock reads plus about 25 ns of counters: measured **+110 ns** per call on a virtualized CI machine (clock reads are slow there), so decide whether that matters for your hottest paths.
- **Bounded memory.** Tracked map pairs (default 500), exception groups (200), histograms (32 counters each), sample ring buffers and the outbox (200 entries) are all capped; overflow is counted, never grown.
- **Privacy.** Exception messages are off by default (they can carry customer data); only AutoMapping's own messages (type names) are kept. Host names are omitted unless `IncludeHostName` is set. Reports contain no mapped values.
- **Delivery.** Per-attempt timeout, exponential backoff with jitter, transient versus permanent classification, one broken sink never blocks the others. A failed delivery goes to an on-disk **outbox** and is retried with backoff for up to 7 days, then to a dead-letter folder (kept 30 days). Nothing is lost on a restart.
- **Restart safe.** The last run per schedule is stored; a report missed while the process was down is produced on restart if it is within `CatchUpGrace` (6 h by default), otherwise skipped and logged.
- **Cannot crash the host.** The scheduler loop catches everything; a failing report generation backs off for 15 minutes. `ReportingStatus` (consecutive failures, last error, outbox size) is available for your own health endpoint.
- **SFTP security.** The host key **must** be pinned (`SHA256:` fingerprint) or the application refuses to start; a mismatch is a permanent failure and nothing is sent. Uploads go to `name.part` and are renamed when complete. Credentials never appear in logs or exception messages. File names are validated against path traversal.
- **Misconfiguration fails at startup**, not at the first 06:00.

**Verification status.** The scheduler, retries, outbox, renderers, schedules, leak analysis and the SFTP sink logic are covered by 31 tests, run
with fake clocks, sinks and an in-memory SFTP server. **Not run here:** the SSH.NET adapter (`SshNetSessionFactory`) against a real SFTP server. It was
compile-checked only against a stand-in of the SSH.NET types, so build it with the real `SSH.NET` package and do one test upload before depending on it.
Email delivery is not provided; use a webhook or a folder your mail system picks up from. Leak detection needs hours of data (by default 6 live-set
observations spanning at least an hour).

## ML.NET fuzzy member matching

Off by default. When enabled, destination members that have **no** exact/flattened source are matched against the source's
top-level members by ML.NET feature similarity (so `Email` can pick up `EmailAddress`):

```csharp
var config = new MapperConfiguration(cfg =>
{
    cfg.UseMlMemberMatching(o => { o.Threshold = 0.85f; o.MinMargin = 0.03f; });
    cfg.CreateMap<Source, Dest>();
});

Console.WriteLine(config.GetMappingPlan<Source, Dest>());
// Email <- EmailAddress  [ML.NET fuzzy match, similarity 0.91]
```

Safety rails: a candidate must clear `Threshold`, beat the runner-up by `MinMargin`, and have a compatible shape
(scalar↔scalar, collection↔collection). Every ML match is labelled in the mapping plan so you can review it, then pin it
with an explicit `ForMember` once you agree. Treat fuzzy matching as a development-time aid for wide DTOs rather than
a substitute for explicit mapping on critical members.

## ProjectTo

```csharp
var dtos = await db.Customers
    .ProjectTo<CustomerSummaryDto>(config)   // or mapper.ProjectTo<...>(query)
    .ToListAsync();
```

Projection builds `src => new Dto { ... }` with no delegates, so `ConvertUsing`, `ConstructUsing` and `MapFrom((s, d) => ...)`
resolvers throw `NotSupportedException` there; use expression `MapFrom` instead.

## Behaviour notes / differences from AutoMapper

- Mapping between two complex types **requires a map** (or `CreateMissingTypeMaps = true`). Same-type members without a map are copied by reference.
- Collections are copied into a new destination collection; get-only destination collections are populated in place. Null source collections become empty unless `AllowNullCollections = true`.
- Conventional flattening follows PascalCase word boundaries (`AddressCity` <- `Address.City`).

## Build, test, benchmark, pack

```
dotnet build -c Release
dotnet test
dotnet run -c Release -f net8.0 --project benchmarks/AutoMapping.Benchmarks -- --filter "*"
dotnet pack src/AutoMapping -c Release -o artifacts
```

## License

MIT. Copyright (c) 2026 FastSI. See [LICENSE](LICENSE).

## Security & resource notes

- Dictionary -> object mapping only writes members with a **public** setter (or `init`/public field), so untrusted key/value bags cannot reach `private set`/`internal set` members. Object -> object maps follow AutoMapper and may use non-public setters.
- Resolver, converter and `ConstructUsing` types come only from your configuration; no type names are read from input data. Nothing is deserialized, no files or network are touched by the core. Exception messages contain type names only, never property values.
- Create **one** `MapperConfiguration` per application (the DI extension does). It owns the compiled-delegate caches; creating one per request recompiles every map and keeps the compiled code alive until the instance is collected.
- Caches (maps, plans, lean analysis) are bounded by the number of distinct type pairs mapped. With `CreateMissingTypeMaps` and an unbounded variety of runtime types (dynamic/emitted types, collectible assemblies) they grow and pin those types; avoid that mode for such workloads.
