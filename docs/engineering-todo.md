# TunProxy.NET Engineering TODO

This checklist tracks the path from a working utility to a maintainable open-source product. Keep changes small, tested, and behavior-preserving unless a task explicitly changes product behavior.

## Phase 1 - Product Foundations

- [x] Centralize product constants such as service name, API URL, restart marker, and default ports.
- [x] Centralize app paths for config, logs, restart markers, bundled resources, and generated files.
- [x] Introduce a single config store for load/save/create/default behavior.
- [x] Centralize Windows service state/control helpers used by CLI API, service install flow, and Tray.
- [x] Centralize system proxy manager creation so service-mode registry targeting stays consistent.
- [x] Add `.editorconfig` and shared build settings after the current warning baseline is known.

## Phase 2 - Application Services

- [x] Move config-save workflow out of HTTP endpoint lambdas into a `ConfigWorkflowService`.
- [x] Move rule resource status/download/prepare behavior into a `RuleResourceService`.
- [x] Move restart marker/helper behavior into a `RestartCoordinator`.
- [x] Keep API endpoints thin: validate request, call application service, translate response.
- [x] Add focused tests around config save, resource preparation, and restart marker behavior.

## Phase 3 - Runtime Decomposition

- [x] Split `TunProxyService` into lifecycle coordinator, packet pipeline, TCP relay coordinator, route coordinator, rule initializer, and diagnostics provider.
  - [x] Extract outbound bind address selection from `TunProxyService`.
  - [x] Extract route diagnostics construction from `TunProxyService`.
  - [x] Extract shared rule resource initialization and background retry behavior.
  - [x] Extract packet-pipeline decisions that can be tested without a TUN device.
  - [x] Extract TCP connection target and failure classification decisions.
  - [x] Extract TCP payload sequence and initial-payload selection decisions.
  - [x] Extract proxy bypass route configuration from TUN lifecycle startup.
  - [x] Extract direct bypass route scheduling and eligibility checks.
  - [x] Extract shared periodic background task runner for cleanup and metrics loops.
  - [x] Extract pending relay idle cleanup rules from TUN runtime cleanup.
  - [x] Extract server-to-client relay response segmentation.
  - [x] Extract traffic metrics log snapshot construction.
- [x] Share upstream proxy connection logic between TUN mode and local proxy mode.
- [x] Keep `DnsResolutionStore` as the single owner of DNS cache and observed hostname snapshots.
- [x] Keep `IpCacheManager` focused on routing/IP state only.
- [x] Add tests for packet-pipeline decisions that can be isolated without real TUN devices.

## Phase 4 - Tray and Service UX

- [x] Split Tray Win32 shell, status polling, service control, installer, and system-proxy behavior.
- [x] Preserve stop-first/start-later restart ordering through `tunproxy.restart`.
- [x] Keep web lifecycle controls conservative: restart/stop when running; prompt when stopped/unreachable.
- [x] Add tests for service-state transitions where pure helper methods can be isolated.

## Phase 5 - Web Console

- [x] Split static pages into API client, state, and page modules while preserving no-build static deployment.
- [x] Embed `src/TunProxy.CLI/wwwroot` into the CLI assembly so release artifacts do not need a separate `wwwroot` directory.
- [x] Ensure every visible string flows through frontend localization.
- [x] Add lightweight UI smoke checks for config/status/DNS page rendering.

## Phase 6 - Open Source Readiness

- [x] Add contributor docs for architecture, build, tests, Windows service behavior, and release process.
- [x] Add CI coverage for CLI, Tray, tests, formatting, and publish artifacts.
- [x] Add issue/PR templates and a security policy.
- [x] Add release notes automation or a documented release checklist.
- [x] Document third-party packages and bundled `wintun.dll` policy review notes.
- [ ] Select a top-level open-source license before public release.
