# Changelog

All notable changes to ohSpy are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [1.0.0] — 2026-06-12

First release. **ohSpy** is a native Windows desktop UPnP inspector for Linn
software engineers — the supported successor to Intel's discontinued Device
Spy. A single, dense, fast window that shows every UPnP device on your network,
lets you walk its services and actions, invoke any action interactively, and
subscribe to GENA events live — staying responsive and telling you *why* when a
device is slow or broken.

### Device discovery & tree browsing
- Live SSDP discovery of UPnP devices, presented as an expandable device →
  service → action tree.
- Graceful removal on `ssdp:byebye`, and **inferred byebye**: a device pulled
  off the network without a byebye is evicted once no `ssdp:alive` arrives
  within its advertised `CACHE-CONTROL max-age` lease.
- **Network-change auto-rebind**: moving the host between networks is detected
  and the bound adapter is re-selected automatically, clearing the stale
  network's devices.
- Manual **Rescan** and a live **SSDP message log** (virtualised, auto-follow).

### Inspection
- Full device and service description (SCPD) viewing, including large service
  descriptions.
- **Properties** window, right-click context menus, and raw XML viewing in the
  default browser.
- UDN-based device identity (handles non-RFC-4122 UDNs correctly).

### Action invocation
- Invoke any action interactively through a popup, with argument entry and
  results, including against slow or misbehaving devices without blocking the UI.

### GENA eventing
- Subscribe to a service and watch event notifications stream in live, with a
  bounded per-subscription event list.

### Operator tooling
- **Diagnostics viewer** — a severity-filterable diagnostic stream in one menu
  click; the filter also drives the emitter's verbosity at runtime.
- **Network adapter switching** from the View menu.
- Free window z-order with sensible parent/child ownership.

### Quality & delivery
- Headless soak-verified: a 1-hour representative-session soak (15 normal + 4
  misbehaving devices) runs with 0 crashes, 0 UI-thread stalls > 1 s, bounded
  memory (no leak), and all bounded collections holding at their caps.
- Per-user InnoSetup installer (no admin, no MSIX); self-contained — needs
  nothing pre-installed on a clean Windows 11 machine.
- .NET 10 / WinUI 3, with a WinUI-free `ohSpy.Core` enforced by a boundary test.

[1.0.0]: https://github.com/openhome/ohSpy/releases/tag/v1.0.0
