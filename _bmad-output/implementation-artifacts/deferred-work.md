# Deferred Work

## Deferred from: code review of 1-6-fakeupnpdevice-minimal-modes-first-chaos-test-netarchtest-rules (2026-06-02)

- **FluentAssertions 8.0.0 commercial license** — `FluentAssertions 8.x` moved to an Xceed commercial license; a warning is emitted at test runtime on every run. Pre-existing from Story 1.1's `Directory.Packages.props` pin. Resolve at Epic 1 retrospective: downgrade to `7.x` (last MIT-licensed version) or switch to `Shouldly` / `xunit.assert`.
