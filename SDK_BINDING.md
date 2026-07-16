# SDK binding checklist — the work left on the Windows VM

The project is split into an SDK-free core (compiles today, no changes) and two
SDK seam files you bind against **RMS 9 / Resto.Front.Api V9**:

```
SDK-FREE CORE (do not need changes):
  Models/*.cs                    ← API DTOs + config shape
  Services/ConfigLoader.cs       ← reads the JSON sidecar
  Services/BonoosApiClient.cs    ← HTTP calls to Bonoos
  Services/OrderTracker.cs       ← per-order state + orchestration (thread-safe)

SDK SEAM FILES (bind these):
  Plugin.cs                          ← registers the payment system; observes order events
  Services/BonoosPaymentProcessor.cs ← the payment system itself (Pay / Cancel / Refund)
```

## Integration model (Path B — external payment system)

"Bonoos" registers as an **external payment system** (`IExternalPaymentProcessor`),
so it appears in iikoOffice under **Внешний тип оплаты → Безналичный тип**. iikoFront
then calls the processor at the payment lifecycle points, and drives the read-only /
close hooks through `Plugin.cs`:

| iiko calls | our method | Bonoos API |
|---|---|---|
| card scanned | `Plugin.OnSdkCardScanned` | `/client/lookup/` |
| order items change | `Plugin.OnSdkOrderChanged` | `/order/precheck/` (quote) |
| cashier applies Bonoos tender | `BonoosPaymentProcessor.Pay` | `/order/pay-by-bonus/` (debit) |
| bonus payment removed / unclosed cancel | `…EmergencyCancelPayment` | `/order/pay-by-bonus/cancel/` |
| return on closed order | `…ReturnPayment` | refund — **Phase 1.5, throws for now** |
| order closes | `Plugin.OnSdkBeforeOrderClosed` | `/order/confirm/` (cashback accrual) |

There is **no** BeforePaymentAdded/veto code — the processor owns the tender natively.

> Reality check for V9: plugins implement **`IFrontPlugin` + `[PluginImplementation]`**
> (not `IRestoPlugin`), order events are **`IObservable<T>`** you `.Subscribe(...)`, and
> `IExternalPaymentProcessor` method signatures may differ from the ones scaffolded
> here. Confirm all of it against the SDK/samples on the machine. The SDK-free calls
> (`_orderTracker.*`, `_tracker.*`) do **not** change even when signatures do.

## Seams in `Plugin.cs`

| Seam | Location | Bind |
|---|---|---|
| **#0** | `using` at top | SDK namespaces. |
| **#1** | `class Plugin : IRestoPlugin` | Real plugin entry type/attribute; keep `Init`/`Stop`. |
| **#A** | `RegisterPaymentSystem` | `_context.Operations.RegisterPaymentSystem(_paymentProcessor, false)` — verify the call + that it returns an `IDisposable` to unregister on Stop. |
| **#2** | `SubscribeToOrderEvents` | Subscribe Created / Changed / BeforeOrderClosed (Rx `Subscribe` if that's your API). |
| **#2b** | inside `SubscribeToOrderEvents` | Subscribe the barcode/card-scan stream → `OnSdkCardScanned`. Without it, lookup/precheck never fire. |
| **#3** | `GetOrderFromEvent` | Return the `IOrder` from each event's args. |
| **#7** | `MapOrderItems` | Written against `IOrder.Items`; verify property names/types. |
| **#8** | `MapPayments` | Written against `IOrder.Payments`; relays native `kind`. Verify names + `PaymentTypeKind`. |
| **#9** | `ShowCashierNotification` / `ShowCustomerNotification` | Real UI (`_context.Notifier…`). |
| **#10** | `Log` | SDK logger (`_context.Log…`). |

## Seams in `Services/BonoosPaymentProcessor.cs`

The whole file is a seam — it implements `IExternalPaymentProcessor`. Verify:
- the **interface name + every method signature** against the V9 SDK (they follow the
  published v6 contract; V9 may reorder/rename params or the interface itself);
- the SDK namespaces in the `using` block;
- `PaymentActionFailedException` / `PaymentActionCancelledException` types;
- that `PaymentSystemName` ("Bonoos") is what you want shown in iikoOffice.

The bodies already call the right `OrderTracker` methods — keep those; only re-shape
the SDK-facing parameters/types.

## Correctness notes baked in
- **Bonus vs fiscal split (server-side):** the backend identifies the bonus tender by
  `payment_type.id`, matched against `Tenant.iiko_bonus_payment_type_id`. Set that to the
  GUID iikoOffice assigns to the External payment type linked to "Bonoos", or the
  bonus-paid portion will wrongly earn cashback. The plugin sends iiko's native payload.
- **Decimals:** all money is formatted with `InvariantCulture` (a ru-RU Windows would
  otherwise emit `"900,00"` and break the JSON decimal).
- **Idempotency:** `order_id` = `IOrder.Id`; a retried confirm returns `duplicate:true`.
- **Config:** `Bonoos.LoyaltyPlugin.config.json` next to the DLL (or
  `%PROGRAMDATA%\Bonoos\iikoFront\config.json`). Only baseUrl / tenantId / token / timeout.
