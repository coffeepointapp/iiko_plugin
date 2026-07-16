# Build & deploy on the iikoFront Windows VM

The plugin is split so that **only `Plugin.cs` depends on the iiko SDK**. Everything
else (API client, config loader, order orchestration, DTOs) is plain C# and already
compiles. Your job on the VM is: restore Newtonsoft, drop in the SDK DLLs, bind the
seams in `Plugin.cs`, build.

## 1. Prerequisites

- Visual Studio 2019/2022 or JetBrains Rider
- .NET Framework 4.7.2 targeting pack
- iikoFront installed (provides the Resto.Front.Api V9 SDK assemblies)

## 2. Copy the project to the VM

Copy the whole `Bonoos.iikoFront.LoyaltyPlugin` folder over.

## 3. Restore Newtonsoft.Json

`packages.config` pins `Newtonsoft.Json 13.0.3`. Restore it:

```
nuget restore    # or: right-click solution → Restore NuGet Packages in VS
```

This places the DLL at `..\packages\Newtonsoft.Json.13.0.3\lib\net45\`, matching the
`HintPath` in the `.csproj`. (If you prefer PackageReference, swap the `<Reference>`
for a `<PackageReference>` — see the comment in the csproj.)

## 4. Point at the iiko SDK DLLs

Copy from the iikoFront install dir into `..\iikoFrontSDK\` (sibling of the project),
or edit the `HintPath`s in the `.csproj`:

```
<iikoFront>\Resto.Front.Api.dll        →  ..\iikoFrontSDK\Resto.Front.Api.dll
<iikoFront>\Resto.Front.Api.Data.dll   →  ..\iikoFrontSDK\Resto.Front.Api.Data.dll
```

> Keep `<Private>False</Private>` — do not redistribute SDK DLLs. They're gitignored.

## 5. Bind the SDK seams — **the code work**

Two files touch the SDK; everything else compiles as-is. Bind each `⚠ SEAM` in:
- **`Plugin.cs`** — registers the payment system (#A) + observes order events.
- **`Services/BonoosPaymentProcessor.cs`** — the `IExternalPaymentProcessor` itself.

**`SDK_BINDING.md` is the full checklist.** Expect the biggest work at:
- **`IExternalPaymentProcessor` signatures** — scaffolded from the v6 contract; verify against V9.
- **#2 event subscription** (V9 uses Rx `IObservable`, not `event +=`)
- **#1 plugin entry type** (V9: `IFrontPlugin` + `[PluginImplementation]`)
- **#A RegisterPaymentSystem** and **#2b card-scan** subscription

## 6. Build & deploy

1. Build → `bin\Debug\Bonoos.iikoFront.LoyaltyPlugin.dll`
2. Copy to iikoFront's `Plugins` directory:
   - `Bonoos.iikoFront.LoyaltyPlugin.dll`
   - `Newtonsoft.Json.dll` (if not already present in the iikoFront dir)
   - `plugin.config`
   - `Bonoos.LoyaltyPlugin.config.json`  ← the runtime config (see step 7)
3. Restart iikoFront.

## 6a. iikoOffice — the External payment type

Once the plugin loads, it registers the **"Bonoos"** payment system. Then:
1. iikoOffice → **Retail sales → Payment types** → add.
2. Type = **Внешний тип оплаты** (External payment type).
3. **Безналичный тип** = **Bonoos** (now selectable — it's the registered system).
4. Leave it **non-fiscal** (points aren't revenue) and enable it on the test till.
5. Note the **GUID** iikoOffice assigns to this payment type and set it on the tenant:
   `Tenant.iiko_bonus_payment_type_id` (Bonoos admin) — this is what makes the backend
   exclude the bonus-paid portion from cashback.
6. Sync the config to the terminal.

## 7. Configure the plugin (no recompile)

Edit `Bonoos.LoyaltyPlugin.config.json` (next to the DLL, or at
`%PROGRAMDATA%\Bonoos\iikoFront\config.json`):

| Field | Description | Example |
|---|---|---|
| `baseUrl` | Bonoos gateway edge | `https://pos.bonoos.ru/iiko/` |
| `tenantId` | Bonoos Tenant UUID | `a8b3f6e0-…` |
| `serviceAccountToken` | Bonoos `ServiceAccount.token` (secret) | `…` |
| `timeoutSeconds` | HTTP timeout | `25` |

The plugin no longer needs the bonus payment-type GUID — the processor learns it from
iiko, and cashback classification is server-side (`Tenant.iiko_bonus_payment_type_id`).

The plugin logs `Bonoos: not configured …` and stays idle until `tenantId`,
`serviceAccountToken`, and `baseUrl` are present.

## 8. Debug

Attach the debugger to the running `iikoFront.exe`. With the seams still unbound the
project won't compile; once bound, plugin output goes through seam **#10** (`Log`) —
point it at the SDK logger to see it in iikoFront's logs.

## Indicative V9 type reference (confirm on your SDK)

```
Resto.Front.Api.IFrontPlugin / IRestoPlugin   → Init / Stop|Dispose
Resto.Front.Api.IPluginContext
  .Orders     → order event source (events or IObservable)
  .Operations → edit sessions (add/remove payment)
  .Notifier   → cashier/customer UI
  .Log        → logging
Resto.Front.Api.Data.Orders.IOrder        → Id(Guid), Number(string), Items, Payments
Resto.Front.Api.Data.Orders.IOrderItem    → Id, Product(IProduct: Id/Code/Name), Price(decimal), Quantity(decimal), Comment
Resto.Front.Api.Data.Orders.IPaymentItem  → Id, PaymentType(IPaymentType: Id/Name/Kind), Sum(decimal)
Resto.Front.Api.Data.Orders.PaymentTypeKind → Cash, Card, External, … (no Bonus — hence bonusPaymentTypeId)
```
