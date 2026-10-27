# FastSI.AutoMapping

[![NuGet](https://img.shields.io/nuget/v/FastSI.AutoMapping.svg)](https://www.nuget.org/packages/FastSI.AutoMapping)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4.svg)](#targets)

> **Official links**
> [Source](https://github.com/FastSI/AutoMapping) · [Full reference](docs/REFERENCE.md) · [Changelog](CHANGELOG.md) · [License](LICENSE)

**Fast object-to-object mapping for .NET, with an AutoMapper-style API and lower allocations.**

## Install

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

config.AssertConfigurationIsValid();   // fail at startup, not in production
IMapper mapper = config.CreateMapper();

CustomerDto dto = mapper.Map<CustomerDto>(customer);
```

ASP.NET Core / dependency injection:

```csharp
builder.Services.AddAutoMapping(typeof(Program).Assembly);   // scans for Profile classes

public class OrderProfile : Profile
{
    public OrderProfile() => CreateMap<Order, OrderDto>();
}
```

Inject `IMapper` where you need it.

## Enterprise ready

| | |
|---|---|
| ⚡ **Fast** | Each map compiles to one expression tree. Maps that need no context run on a lean path with no extra allocation. |
| 🧩 **Familiar API** | `CreateMap`, `ForMember`, `MapFrom`, `ConvertUsing`, `ReverseMap`, profiles, inheritance, open generic maps, flattening. |
| ✅ **Fails early** | `AssertConfigurationIsValid()` at startup, `CompileMappings()` to pay the compile cost up front, `GetMappingPlan` to see how each member is mapped. |
| 🔒 **Thread safe** | One configuration, shared by the whole application. Cycle protection built in. |
| 🔌 **DI friendly** | `AddAutoMapping(...)` with singleton, scoped or transient `IMapper`. |
| 🗃️ **Query support** | `ProjectTo<T>` for EF Core style projection, expression mapping, OData-style `$filter` / `$orderby` / `$top` / `$skip` / `$count` / `$search`. |
| 💾 **EF Core helpers** | `Persist` (insert or update by key, remove) and key-based equality from the EF Core model. |
| 🧠 **Fuzzy matching (optional)** | ML.NET member matching for wide DTOs. Off by default and runs once at configuration time. |
| 📊 **Diagnostics (optional)** | Scheduled reports on exceptions, performance and memory-leak indicators, sent to a folder, webhook or SFTP. Bounded memory, retry with an on-disk outbox. |
| 🚨 **Alerts (optional)** | Get told when mapping gets slow, errors rise, memory or caches grow, or reports stop arriving. Sent to the log, a webhook, email or your own code, with cooldown and "resolved" messages. |
| 🛡️ **Privacy by default** | Reports contain no mapped values and exception messages are off by default. Dictionary-to-object mapping writes public setters only. |
| 🧪 **Tested** | 171 automated tests, run on .NET 8, 9 and 10 (513 test runs, all passing). |
| 📄 **Clear licensing** | MIT, with the license text included in the package. |
| 🐞 **Debuggable** | Symbols package (`.snupkg`) built alongside each release. |

**Not yet verified against the real thing:** EF Core `DbContext` (Persist, key equality), the official `Microsoft.AspNetCore.OData`
package, a live SFTP server, a real SMTP server and a real webhook endpoint were tested only with stand-ins. Try them once in your
own environment first.

## Alerts

Alerts watch the numbers that reporting already collects and notify you when a limit is crossed. They are optional and add nothing
to the mapping path: with alerts off, or on, `Map` behaves exactly the same.

```csharp
builder.Services.AddAutoMapping(typeof(Program).Assembly);

builder.Services.AddAutoMappingReporting(o =>          // alerts read the data this collects
{
    o.Application = "orders-api";
    o.AddLocalDirectory(@"D:\reports");
    o.Daily(new TimeOnly(6, 0));
});

builder.Services.AddAutoMappingAlerts(o =>
{
    o.AddLog();                                                              // application log
    o.AddWebhook(new Uri("https://hooks.example.com/alerts"), signingSecret); // signed JSON
    o.AddSmtp(s =>                                                           // email (TLS on by default)
    {
        s.Host = "smtp.example.com";
        s.From = "alerts@example.com";
        s.To.Add("ops@example.com");
        s.UserName = config["Smtp:User"];
        s.Password = config["Smtp:Password"];                                // keep secrets out of code
    });
    o.AddCallback((alert, ct) => MyPager.SendAsync(alert, ct));              // your own code
});
```

| Alert | Raised when (defaults) |
|---|---|
| Error rate | A map pair fails 1% (warning) or 5% (critical) of calls in the last 5 minutes |
| Slow mapping | p95 latency of a map pair is 1 ms (warning) or 10 ms (critical) or more |
| Slow compile | Maps compiled in the window took 500 ms or more on average |
| GC pressure | Garbage collection pauses take 10% (warning) or 30% (critical) of the time |
| Cache growth | Mapper caches grew 25% or more in the window |
| Possible memory leak | The live set after full GCs keeps rising (a heuristic: confirm with a memory dump) |
| Report delivery | 3 (warning) or 10 (critical) report deliveries failed in a row |

How it behaves:

- Checks run every minute. Rates and latency need at least 100 calls in the window, so quiet systems do not alert on noise.
- One alert when a condition starts, another if it becomes critical, a reminder after 30 minutes if it persists, and a "resolved" message after it has been gone for 2 checks.
- Alerts contain type names and numbers only: no mapped values and no exception messages.
- A sink that fails is retried and logged. It never throws into your application, and one broken sink does not block the others.
- `AlertService.ActiveAlerts` lists what is firing now, for your own health endpoint.
- Every limit is a property of `AlertOptions.Thresholds`; `Window`, `Cooldown`, `MinSeverity` and others are on `AlertOptions`.

## Performance

Compared with AutoMapper in the same BenchmarkDotNet run, Release build. "Speed-up" is AutoMapper time divided by FastSI time,
so higher is better.

### .NET 10

| Scenario | Hand-written | FastSI.AutoMapping | AutoMapper | Speed-up vs AutoMapper |
|---|---:|---:|---:|---:|
| One order with 10 lines | 154 ns | **165 ns** | 261 ns | **1.58x** |
| List of 1000 orders | 188 µs | **193 µs** | 272 µs | **1.41x** |
| Array and dictionary of 10 lines | n/a | **510 ns** | 584 ns | **1.14x** |

### .NET 8

| Scenario | Hand-written | FastSI.AutoMapping | AutoMapper | Speed-up vs AutoMapper |
|---|---:|---:|---:|---:|
| One order with 10 lines | 203 ns | **201 ns** | 326 ns | **1.62x** |
| List of 1000 orders | 231 µs | **225 µs** | 309 µs | **1.37x** |
| Array and dictionary of 10 lines | n/a | **728 ns** | 706 ns | 0.97x (about equal) |

### Memory per call

| Scenario | Hand-written | FastSI.AutoMapping | AutoMapper |
|---|---:|---:|---:|
| One order with 10 lines | 784 B | **712 B** | 904 B |
| List of 1000 orders | 774 KB | **703 KB** | 899 KB |
| Array and dictionary of 10 lines | n/a | **1.64 KB** | 2.13 KB |

How to read this:

- Machine: 12th Gen Intel Core i7-12800H, Windows 11, BenchmarkDotNet 0.15.8, default job, `MemoryDiagnoser`.
- Compare the columns within one table. Absolute times on the same machine changed by up to 2x between runs (power state,
  background load), but the speed-up ratios stayed close. The .NET 8 array and dictionary result varied between runs, from
  faster to slower than AutoMapper, so treat it as equal.
- One machine, one object shape per scenario, single thread. Run the benchmark on your own types before relying on the numbers:

```
dotnet run -c Release -f net10.0 --project benchmarks/AutoMapping.Benchmarks -- --filter "*"
```

## Rules to know

- Create **one** `MapperConfiguration` per application and reuse it. The DI extension does this for you.
- Mapping between two complex types needs a `CreateMap` (or `CreateMissingTypeMaps = true`).
- Collections are copied into a new collection. Null source collections become empty lists.
- Benchmark and publish in **Release** builds only.

## Targets

`net8.0`, `net9.0`, `net10.0`, in one multi-targeted package. .NET 10 is the primary target.

## Documentation

| Topic | Where |
|---|---|
| Full reference: features, AutoMapper parity, `ProjectTo`, OData, EF Core `Persist`, reporting, ML.NET matching, security, known limits | [docs/REFERENCE.md](docs/REFERENCE.md) |
| Release history | [CHANGELOG.md](CHANGELOG.md) |
| Benchmarks | [benchmarks/AutoMapping.Benchmarks](benchmarks/AutoMapping.Benchmarks) |
| License | [LICENSE](LICENSE) |

## Release notes

### 4.0.0

- **New: alerts.** `AddAutoMappingAlerts(...)` notifies you about high error rates, slow mapping, slow compiles, GC pressure, cache growth,
  suspected memory leaks and failing report delivery.
- Sinks: application log, signed JSON webhook, email (SMTP, TLS by default) and a callback for your own code.
- Cooldown, escalation to critical, reminders and "resolved" messages; bounded memory; retries; a failing sink never affects mapping.
- Nothing existing changed: mapping and reporting code are untouched, and all existing tests pass unchanged.
- 31 new tests. The SFTP and hardening tests no longer run at the same time, because one of them changes a process-wide environment variable.

### 3.0.0

- Benchmarks re-measured on .NET 8 and .NET 10 and shown as tables (time, speed-up, memory).
- README rewritten: install, quick start, enterprise ready table, performance, documentation links.
- Release notes added to the NuGet package page and to `CHANGELOG.md`.
- No change to the mapping engine since 2.0.0.

### 2.0.0

- Package renamed to `FastSI.AutoMapping`. The namespace is still `AutoMapping`, so `using AutoMapping;` is unchanged.
- Faster collection mapping: the compiled element mapping is looked up once per list or array, not once per item.
- `LICENSE` and a new package icon included in the NuGet package.
- Full reference moved to `docs/REFERENCE.md`.

## License

MIT. Copyright (c) 2026 FastSI. See [LICENSE](LICENSE).
