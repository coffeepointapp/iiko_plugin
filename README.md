# Bonoos iikoFront Loyalty Plugin

A .NET Framework plugin for **iikoFront (RMS 9 / Resto.Front.Api V9)** that connects an
iiko POS terminal to the **Bonoos** loyalty backend: balance lookup, bonus spend
(as a registered external payment system), and cashback accrual on receipt close.

This is a Windows-only build (full .NET Framework, references the proprietary iiko SDK),
kept in its own repository. The Bonoos backend lives separately.

## Layout

```
Bonoos.iikoFront.LoyaltyPlugin.csproj   VS project (repo root)
Plugin.cs                                SDK seam #1 — registers the payment system, observes order events
Services/BonoosPaymentProcessor.cs       SDK seam #2 — IExternalPaymentProcessor (Pay / Cancel / Refund)
Services/BonoosApiClient.cs              HTTP client for the Bonoos API      ┐
Services/OrderTracker.cs                 per-order state + orchestration      │ SDK-free core
Services/ConfigLoader.cs                 reads the JSON sidecar config        │ (compiles anywhere)
Models/*.cs                              API DTOs + config                    ┘
Bonoos.LoyaltyPlugin.config.json         runtime config (fill in per terminal)
plugin.config                            iiko plugin manifest
iikoFrontSDK/  (gitignored)              you drop Resto.Front.Api.dll + Newtonsoft.Json.dll here
```

## Docs

- **[README_ON_VM.md](README_ON_VM.md)** — how to build on the Windows VM (toolchain, SDK refs, iikoOffice setup, deploy, configure).
- **[SDK_BINDING.md](SDK_BINDING.md)** — the checklist of SDK seams to bind against your V9 SDK.
- **[PLUGIN_SPEC.md](PLUGIN_SPEC.md)** — the Bonoos API contract the plugin speaks. Source of truth lives in the backend repo (`integrations/partners/iiko/`); this is a copy for self-contained reference.

## Quick start (on the VM)

1. Install VS with the **.NET desktop development** workload + a .NET Framework targeting pack (the project targets **v4.8**).
2. Create `iikoFrontSDK/` at the repo root; copy `Resto.Front.Api.dll` and `Newtonsoft.Json.dll` from the iikoFront install into it.
3. Bind the SDK seams in `Plugin.cs` and `Services/BonoosPaymentProcessor.cs` (see `SDK_BINDING.md`).
4. Build; deploy the DLL + `plugin.config` + `Bonoos.LoyaltyPlugin.config.json` to iikoFront's `Plugins` folder.
