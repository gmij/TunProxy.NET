# TunProxy.NET Engineering TODO

This checklist tracks the path from a working utility to a maintainable open-source product. Keep changes small, tested, and behavior-preserving unless a task explicitly changes product behavior.

## Phase 1 - Product Foundations

- [x] Centralize product constants such as service name, API URL, restart marker, and default ports.
- [x] Centralize app paths for config, logs, restart markers, bundled resources, and generated files.
- [x] Introduce a single config store for load/save/create/default behavior.
- [ ] Centralize Windows service state/control helpers used by CLI API, service install flow, and Tray.
- [x] Centralize system proxy manager creation so service-mode registry targeting stays consistent.
- [ ] Add `.editorconfig` and shared build settings after the current warning baseline is known.

## Phase 2 - Application Services

- [ ] Move config-save workflow out of HTTP endpoint lambdas into a `ConfigWorkflowService`.
- [ ] Move rule resource status/download/prepare behavior into a `RuleResourceService`.
- [ ] Move restart marker/helper behavior into a `RestartCoordinator`.
- [ ] Keep API endpoints thin: validate request, call application service, translate response.
- [ ] Add focused tests around config save, resource preparation, and restart marker behavior.

## Phase 3 - Runtime Decomposition

- [ ] Split `TunProxyService` into lifecycle coordinator, packet pipeline, TCP relay coordinator, route coordinator, rule initializer, and diagnostics provider.
- [ ] Share upstream proxy connection logic between TUN mode and local proxy mode.
- [ ] Keep `DnsResolutionStore` as the single owner of DNS cache and observed hostname snapshots.
- [ ] Keep `IpCacheManager` focused on routing/IP state only.
- [ ] Add tests for packet-pipeline decisions that can be isolated without real TUN devices.

## Phase 4 - Tray and Service UX

- [ ] Split Tray Win32 shell, status polling, service control, installer, and system-proxy behavior.
- [ ] Preserve stop-first/start-later restart ordering through `tunproxy.restart`.
- [ ] Keep web lifecycle controls conservative: restart/stop when running; prompt when stopped/unreachable.
- [ ] Add tests for service-state transitions where pure helper methods can be isolated.

## Phase 5 - Web Console

- [ ] Split static pages into API client, state, and page modules while preserving no-build static deployment.
- [ ] Keep `src/TunProxy.CLI/wwwroot` and `dist/wwwroot` synchronized through a repeatable build/copy step.
- [ ] Ensure every visible string flows through frontend localization.
- [ ] Add lightweight UI smoke checks for config/status/DNS page rendering.

## Phase 6 - Open Source Readiness

- [ ] Add contributor docs for architecture, build, tests, Windows service behavior, and release process.
- [ ] Add CI coverage for CLI, Tray, tests, formatting, and publish artifacts.
- [ ] Add issue/PR templates and a security policy.
- [ ] Add release notes automation or a documented release checklist.
- [ ] Review license, third-party notices, and bundled binary policy for `wintun.dll`.
