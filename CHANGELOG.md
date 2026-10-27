# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com); versions follow [Semantic Versioning](https://semver.org).

## [4.0.0]

### Added
- Alerts: `AddAutoMappingAlerts(...)` raises alerts for error rate, slow mapping (p95), slow compile, GC pressure, cache growth,
  suspected memory leak and failing report delivery. Needs `AddAutoMappingReporting`.
- Alert sinks: application log, signed JSON webhook, email (SMTP, TLS by default) and a callback.
- Cooldown, escalation to critical, reminders, "resolved" messages, bounded memory, retries, `AlertService.ActiveAlerts`.
- 31 tests for alerts.

### Changed
- Test only: the SFTP and hardening tests share an xunit collection so they never run at the same time (one sets a process-wide
  environment variable).

No change to mapping or reporting code.

## [3.0.0]

### Changed
- Benchmarks re-measured on .NET 8 and .NET 10 against AutoMapper; results shown as tables (time, speed-up, memory).
- README rewritten: quick start, feature table, rules to know, release notes.

### Added
- Release notes in the NuGet package metadata and this changelog.

The mapping engine is unchanged since 2.0.0.

## [2.0.0]

### Changed
- Package renamed to `FastSI.AutoMapping` (namespace `AutoMapping` unchanged).
- Faster collection mapping: the compiled element mapping is looked up once per list or array, not once per item.

### Added
- `LICENSE` and a package icon in the NuGet package.
- Benchmark scenario for arrays and dictionaries; benchmark selection menu.
- `docs/REFERENCE.md` with the full reference.
