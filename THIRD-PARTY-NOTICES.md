# Third-party notices

FastSI.AutoMapping is licensed under the MIT License (see [LICENSE](LICENSE)). It depends on the following packages, which
are installed by NuGet and are not modified or redistributed inside this package. Each is licensed under the MIT License by its
own authors; the full text is in the corresponding package.

| Package | Used for | License |
|---|---|---|
| Microsoft.ML | optional fuzzy member matching | MIT |
| SSH.NET | SFTP report delivery (loaded only when configured) | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | DI integration | MIT |
| Microsoft.Extensions.Hosting.Abstractions | report scheduler hosting | MIT |
| Microsoft.Extensions.Logging.Abstractions | logging | MIT |

## Benchmarks only

The `benchmarks/` project (not shipped in the NuGet package) compares against AutoMapper and uses BenchmarkDotNet. AutoMapper is
a separate product with its own license terms; check them before using it in your own work. FastSI.AutoMapping does not
reference AutoMapper and offers a similar public API.

"AutoMapper" and other product names are trademarks of their respective owners and are used here only to describe compatibility.
