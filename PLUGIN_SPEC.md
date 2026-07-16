# Bonoos iikoFront Loyalty Plugin — Build Specification

> **Audience:** an LLM/developer building the **client-side iikoFront plugin** on a
> **Windows** machine. This document is the complete contract between the plugin and
> the Bonoos backend. The backend half already exists and is tested
> (`integrations/partners/iiko/` in the Django repo); **do not** rebuild it — build
> only the plugin described here, and make it speak exactly the JSON below.
>
> **Source of truth:** this spec is derived from the backend serializers
> (`serializers.py`), services (`services.py`), Frontol response dataclasses
> (`integrations/partners/frontol/data.py`), and the passing test fixtures. If this
> doc and the backend code ever disagree, the backend code wins — regenerate this doc.

---

## 1. What you are building

A **.NET plugin DLL loaded by iikoFront** (iiko's cashier terminal application). It
hooks the order lifecycle, captures the customer's loyalty card, and calls the Bonoos
REST API to: look up balance, quote bonus/cashback, let the customer spend bonus, and
commit the closed receipt so cashback is earned.

The plugin is a **thin client**. All loyalty math (cashback %, brand-card sync, dedup,
Telegram fanout, wallet-pass refresh) happens server-side. The plugin's job is:
capture card → call endpoints at the right lifecycle moments → render the returned
`cashierInformation` / `customerInformation` text → respect the returned amounts.

**Integration model (chosen: external payment system).** The plugin registers "Bonoos"
as an iiko external payment system (`IExternalPaymentProcessor`), so it appears in
iikoOffice under *Внешний тип оплаты → Безналичный тип*. iikoFront then calls the
processor natively: `Pay` → `/order/pay-by-bonus/`, cancel → `/order/pay-by-bonus/cancel/`.
The read-only quotes and cashback-on-close go through order-event observation
(`/client/lookup/`, `/order/precheck/`, `/order/confirm/`). The wire contract below is
unchanged by this choice.

### What it must NOT do
- No loyalty math locally. Never compute cashback or balances yourself — display what the API returns.
- No bonus accrual on the bonus-paid portion (the server already excludes it).
- No retries that change `order_id` (idempotency depends on a stable `order_id`).

---

## 2. Build environment & toolchain (Windows only)

iikoFront plugins are **full .NET Framework**, Windows-only, built against iiko's
proprietary SDK. This cannot be built or run on macOS/Linux.

| Item | Value / guidance |
|---|---|
| OS | Windows (same machine that has iikoFront/iikoRMS installed, for SDK refs + debugging) |
| Toolchain | Visual Studio 2019/2022 (or JetBrains Rider) + MSBuild |
| Target framework | **.NET Framework 4.x** — match the version the installed iikoFront expects. Verify against the SDK; do **not** use .NET Core / .NET 5+. |
| Plugin SDK | **Resto.Front.Api V9** (`Resto.Front.Api.dll`, `Resto.Front.Api.Data.dll`, and related assemblies). |
| SDK reference DLLs | Provided by the installed iiko product (typically under the iikoFront install dir) and/or the iiko plugin SDK package. **Reference them locally; never commit them** — they are proprietary and machine-specific. |
| HTTP client | `System.Net.Http.HttpClient` (built-in) or RestSharp. JSON via `Newtonsoft.Json` (Json.NET) — Resto plugins almost always already ship Json.NET. |
| Output | A signed plugin DLL + its manifest/config, dropped into iikoFront's `Plugins` folder. Confirm the exact deployment layout and plugin-registration mechanism against the **Resto.Front.Api V9 plugin samples** for the installed version. |

### Build/run loop
1. Open the solution in Visual Studio/Rider on the Windows box.
2. Reference the Resto.Front.Api assemblies from the local iiko install (set **Copy Local = False** so you don't redistribute them).
3. Build → produces the plugin DLL.
4. Deploy the DLL into iikoFront's plugins directory (per the SDK sample layout).
5. Start iikoFront; **attach the debugger to the running `iikoFront.exe`** to step through lifecycle events.

> ⚠️ The exact Resto.Front.Api **type and method names** for subscribing to order
> events, reading the order, adding a bonus payment, and rendering cashier UI must be
> taken from the **Resto.Front.Api V9 SDK / official plugin samples present on this
> machine**. Treat any API names in this doc as *indicative of intent*, not verbatim
> signatures — confirm them against the SDK.

---

## 3. Order lifecycle → endpoint mapping

This is the heart of the integration. Fire each endpoint at the right moment:

```
Cashier scans / types the loyalty card
        │
        ▼
  POST /client/lookup/          → show balance + cashback% to cashier      [read-only]
        │
   (items on the order change)
        ▼
  POST /order/precheck/         → quote: bonus available + cashback to earn [read-only, no state change]
        │
   ┌────┴─────────────────────────────┐
   │ customer wants to pay with bonus  │
   ▼                                   │
  POST /order/pay-by-bonus/      → reserve N₽ off the card  ── DEBIT (spend)│
   │   the configured bonus tender carries the reserved amount on the order │
   │                                   │
   │ cashier removes the bonus payment │
   ▼                                   │
  POST /order/pay-by-bonus/cancel/ → release the reservation               │
   └────┬──────────────────────────────┘
        │
   Cashier closes the check
        ▼
  POST /order/confirm/          → commit receipt; earn cashback ── CREDIT   [writes IikoFiscalReceipt]
```

**Debit vs credit happen on different calls:** the spend is committed at
`pay-by-bonus`; the earn happens at `confirm` (cashback is credited only on the
**fiscal** — cash/card — portion, never on the bonus-paid portion).

**Critical idempotency / cleanup rules**
- Use the iiko order GUID as `order_id` on **every** call for one order. Keep it stable across retries.
- `confirm` is deduped server-side on `order_id` — a double-close returns `{"duplicate": true}` and does **not** double-credit. Safe to retry.
- If a `pay-by-bonus` succeeds but the order is then **abandoned without** a `cancel` or a `confirm`, **the bonus stays spent.** The plugin MUST guarantee a `cancel` on order abandonment / bonus-payment removal.

---

## 4. Transport, base URL & auth

### Base URL (per terminal, configured per tenant)
```
https://pos.bonoos.ru/iiko/<tenant_id>/
```
The Yandex API Gateway (`pos.bonoos.ru`) forwards to the Django backend:
```
pos.bonoos.ru/iiko/<tenant_id>/<path>  →  bonoos.ru/api/v1/pos/partners/iiko/<tenant_id>/<path>
```
The direct backend form `https://bonoos.ru/api/v1/pos/partners/iiko/<tenant_id>/...`
also works if you bypass the gateway.

- `<tenant_id>` is the Bonoos Tenant UUID (per store/terminal). Configurable.
- All requests are **`POST`**, body **`application/json; charset=utf-8`**, trailing slash **required**.

### Auth
Send the tenant's Bonoos `ServiceAccount.token` as a Bearer token:
```
Authorization: Bearer <SERVICE_ACCOUNT_TOKEN>
```
The Yandex gateway strips `Authorization` on some configurations, so **also** send the
same value under the fallback header:
```
X-Bonoos-Authorization: Bearer <SERVICE_ACCOUNT_TOKEN>
```
The backend reads `X-Bonoos-Authorization` first, then `Authorization`. Sending both
is safe and recommended. The token string after `Bearer ` is what's compared.

- Missing/invalid token → **403 Forbidden**.
- Unknown `<tenant_id>` → **403 Forbidden**.

### Plugin configuration (expose these as settings)
| Setting | Example | Notes |
|---|---|---|
| `BaseUrl` | `https://pos.bonoos.ru/iiko/` | gateway edge |
| `TenantId` | `a8b3f6e0-ec83-4a24-b080-0f02f0096f51` | Bonoos Tenant UUID |
| `ServiceAccountToken` | (secret) | Bonoos `ServiceAccount.token` |
| `TimeoutSeconds` | `25` | stay under iikoFront's own UI timeout |

---

## 5. Money & units conventions (read carefully — there is an inversion)

| Context | Unit | Example |
|---|---|---|
| **Request** item `price` / `quantity` / `sum`, and `amount` | **Rubles**, decimal as string | `"900.00"`, `"2.000"` |
| **Response** `balance_kopecks`, `fiscal_amount`, `bonus_credit_amount`, `bonus_debit_amount` | **Kopecks**, integer | `90000` = 900.00 ₽ |
| **Response** `document.positions[].paidAmount` / `discountAmount` (precheck/pay-by-bonus) | **Kopecks**, number | `4500` = 45.00 ₽ |
| **Response** `client.availableAmount` (precheck/pay-by-bonus) | **Display value (rubles)** — for display only | use `/client/lookup` `balance_kopecks` when you need an exact integer |

> **⚠️ Field-name inversion in `confirm` and on the stored receipt.** The names are
> from the *system's bonus-ledger* perspective, not the card's:
> - **`bonus_credit_amount`** = bonuses **SPENT** by the customer (debited from the card). Russian verbose: *«Сумма потраченных бонусов»*.
> - **`bonus_debit_amount`** = bonuses **ACCRUED** to the customer (cashback credited to the card). Russian verbose: *«Сумма начисленных бонусов»*.
>
> So in a `confirm` response, the cashback the customer just **earned** is
> `bonus_debit_amount`, and what they **spent** is `bonus_credit_amount`. Do not invert these in the cashier UI.

1 coin / 1 bonus = 1 kopeck throughout the Bonoos system.

---

## 6. Endpoint contract

All paths are relative to `https://pos.bonoos.ru/iiko/<tenant_id>/`.

### Common request building blocks

**`card`** — customer identifier (send whichever the cashier captured; at least one field):
```json
{ "track": "<Bonoos ClientKPass UUID from scanned Wallet QR>", "number": "<phone / TG chat-id digits, manual entry>" }
```
The Bonoos Apple/Google Wallet QR encodes `ClientKPass.pk` (a UUID) directly — put it
in `track`. Manual keypad entry of a phone/number goes in `number`. `track` wins if both present.

**`items[]`** — order line items (iiko `IOrderItem` → this shape):
```json
{
  "id": "<iiko order-item GUID>",
  "product": { "id": "<iiko product GUID>", "code": "34", "name": "ФЛЭТ УАЙТ" },
  "price": "450.00",      // rubles, per unit
  "quantity": "2.000",    // decimal
  "sum": "900.00",        // rubles, line total
  "comment": ""           // optional
}
```

**`payments[]`** (confirm only) — iiko `IPaymentItem` → this shape:
```json
{
  "id": "<iiko payment GUID>",
  "payment_type": { "id": "<guid>", "name": "Наличные", "kind": "cash" },
  "sum": "900.00"         // rubles
}
```
Send `payment_type.id`/`name`/`kind` exactly as iiko reports them — **no special
tagging**. The backend splits payments into spent-bonus vs fiscal by matching
`payment_type.id` against the tenant's configured bonus tender GUID
(`Tenant.iiko_bonus_payment_type_id`), server-side:
- `payment_type.id` ∈ tenant's configured bonus GUID(s) → spent-bonus (excluded from cashback)
- everything else → **fiscal** (cashback earned on this)

(An explicit `kind == "bonus"` is also honoured if present, but the id match is the
authoritative rule and does not depend on the plugin.)

---

### 6.1 `POST /client/lookup/` — identify customer, show balance

**Request**
```json
{ "card": { "track": "443a5235-7a6f-40a7-968e-07d6a4063f81" } }
```

**Response 200 — found**
```json
{
  "found": true,
  "card_id": "443a5235-7a6f-40a7-968e-07d6a4063f81",
  "client_profile_id": "9df2bf17-52f8-4d9c-9e5e-dcb644280ae5",
  "username": "...",
  "first_name": "",
  "last_name": "",
  "balance_kopecks": 20000,
  "balance_display": "200",
  "cashback_percent": 5
}
```

**Response 200 — not found / no card**
```json
{ "found": false, "message": "Клиент не найден." }
```

**Response 400** — `card` block missing entirely (validation error, DRF field errors).
**Response 403** — bad/missing token or unknown tenant.

---

### 6.2 `POST /order/precheck/` — quote bonus + cashback (no state change)

**Request**
```json
{
  "order_id": "5d4c6e8a-1111-4222-8333-444455556666",
  "order_number": "27",
  "items": [
    { "id": "item-abc-001",
      "product": { "id": "prod-flat-white", "code": "34", "name": "ФЛЭТ УАЙТ" },
      "price": "450.00", "quantity": "2.000", "sum": "900.00" }
  ],
  "card": { "track": "443a5235-7a6f-40a7-968e-07d6a4063f81" }
}
```

**Response 200** — pass-through of the Frontol `PreCheckResponse` (serialized from a
dataclass). Shape:
```json
{
  "code": 0,
  "error": "",
  "client": {
    "availableAmount": 200,          // display value (rubles) — see §5
    "mobilePhone": "",
    "email": "",
    "validationCode": "",
    "validationMessage": ""
  },
  "validationCode": "",
  "validationMessage": "",
  "document": {                       // null = no per-position discounts
    "positions": [
      { "index": 1, "discountAmount": 0, "paidAmount": null }
    ]
  },
  "cashierInformation":  [ { "text": "Баланс клиента: 200 \nУровень кэшбэка: ..." } ],
  "customerInformation": [ { "text": "Ваш баланс: 200" } ],
  "printingInformation": []
}
```
Render `cashierInformation[*].text` on the cashier screen and
`customerInformation[*].text` on the customer-facing display. `document.positions`
amounts (when present) are in **kopecks**.

> Note: if the card is unknown or the brand mismatches, the backend may return `null`
> from the underlying service; treat a null/empty body or `code != 0` as "no loyalty
> for this order" and continue the sale normally.

---

### 6.3 `POST /order/pay-by-bonus/` — DEBIT (reserve/spend bonus)

**Request** (adds `amount`, in **rubles**, = how much the customer wants to pay with bonus)
```json
{
  "order_id": "5d4c6e8a-1111-4222-8333-444455556666",
  "order_number": "27",
  "items": [ { "id": "item-abc-001", "product": { "id": "prod-flat-white", "code": "34", "name": "ФЛЭТ УАЙТ" }, "price": "450.00", "quantity": "2.000", "sum": "900.00" } ],
  "card": { "track": "443a5235-7a6f-40a7-968e-07d6a4063f81" },
  "amount": "150.00"
}
```

**Response 200** — pass-through of `PayByBonusResponse`:
```json
{
  "code": 0,
  "error": "",
  "client": { "availableAmount": 50, "mobilePhone": "", "email": "", "validationCode": "", "validationMessage": "" },
  "validationCode": "",
  "validationMessage": "",
  "document": { "positions": [ { "index": 1, "discountAmount": null, "paidAmount": 15000 } ] },
  "cashierInformation":  [ { "text": "..." } ],
  "customerInformation": [ { "text": "..." } ],
  "printingInformation": []
}
```
`document.positions[].paidAmount` is the bonus applied per position, in **kopecks**.
After a successful reservation, the **configured bonus tender** (the iiko payment type
whose GUID is the tenant's bonus payment type) carries the reserved amount on the order.
No special `kind` is needed — the backend recognises the tender by its id.

**Response 400** — `{ "ok": false, "message": "Не удалось зарезервировать бонусы." }`
(backend returned null — e.g. insufficient balance / invalid card).

---

### 6.4 `POST /order/pay-by-bonus/cancel/` — release a reservation

**Request** — identical shape to `pay-by-bonus` (`order_id`, `items`, `card`, `amount`).
Send the amount that was previously reserved.

**Response 200** — pass-through of `CancelPayByBonusResponse` (same envelope as above,
no `document`). **Response 400** — `{ "ok": false, "message": "Не удалось отменить резервирование." }`.

**Call this whenever** the cashier removes the bonus payment OR the order is abandoned
after a successful `pay-by-bonus` but before `confirm`.

---

### 6.5 `POST /order/confirm/` — commit receipt, CREDIT cashback

**Request**
```json
{
  "order_id": "5d4c6e8a-1111-4222-8333-444455556666",
  "order_number": "27",
  "items": [ { "id": "item-abc-001", "product": { "id": "prod-flat-white", "code": "34", "name": "ФЛЭТ УАЙТ" }, "price": "450.00", "quantity": "2.000", "sum": "900.00" } ],
  "payments": [
    { "id": "pay-cash-001", "payment_type": { "id": "cash-pt-guid", "name": "Наличные", "kind": "cash" }, "sum": "900.00" }
  ],
  "card": { "track": "443a5235-7a6f-40a7-968e-07d6a4063f81" },
  "closed_at": "2026-06-22T12:00:00+03:00",
  "order_type": "order",
  "reference_order_id": ""
}
```
- `closed_at` — ISO-8601 **with timezone offset** (the cashier station's local tz, e.g. `+03:00`). Required.
- `order_type` — `"order"` (sale) or `"refund"` (return). Defaults to `"order"`. **Refunds are NOT yet implemented server-side** (see §8).
- `reference_order_id` — for refunds, the original confirmed `order_id`. Empty for sales.
- `card` is **optional** on confirm — a receipt with no loyalty card is valid (just no cashback).
- Include **all** payment lines, including the bonus tender from 6.3, with iiko's native `kind`.

**Response 200 — committed**
```json
{
  "ok": true,
  "duplicate": false,
  "fiscal_amount": 90000,          // kopecks; sum of non-bonus payments
  "bonus_credit_amount": 0,        // kopecks SPENT by customer (see §5 inversion)
  "bonus_debit_amount": 4500,      // kopecks cashback EARNED/credited (see §5 inversion)
  "message": "Чек зарегистрирован в системе лояльности."
}
```

**Response 200 — duplicate** (same `order_id` already confirmed; safe, no double credit)
```json
{ "ok": true, "duplicate": true, "message": "Чек уже зарегистрирован в системе лояльности." }
```

**Response 200 — refund (not yet supported)**
```json
{ "ok": false, "message": "Возврат пока не поддерживается." }
```

**Response 200 — server error during registration** (do not block the cashier)
```json
{ "ok": false, "message": "Ошибка регистрации чека. АйТи знает, баллы не начислены, но чек можно закрывать." }
```

**Response 400** — validation error. **Response 403** — auth.

---

## 7. Error-handling & UX expectations for the plugin

- **Never block closing a check** because the loyalty call failed. If `confirm` returns
  `ok: false` (or times out), show the returned `message` (or a generic "баллы не
  начислены") and **let the cashier close the receipt anyway**. Cashback is best-effort.
- **Always send the bonus `kind`** on confirm payments so the server excludes that
  portion from cashback.
- **Retry-safe:** `confirm` is idempotent on `order_id`. On a network timeout, you may
  safely re-POST the same `confirm`; a `duplicate: true` response means it already landed.
- **Guarantee a `cancel`** for any `pay-by-bonus` that doesn't reach `confirm`.
- Treat any `code != 0` / `null` body from precheck/pay-by-bonus as "no loyalty this
  order" and proceed with a normal (non-loyalty) sale.
- Show `cashierInformation` to the cashier and `customerInformation` on the customer
  display verbatim — they are pre-localized Russian strings.

---

## 8. Scope / not-yet-supported (Phase boundaries)

| Feature | Status |
|---|---|
| Sale (`order_type: "order"`) full flow | ✅ supported server-side |
| Idempotent confirm / dedup | ✅ |
| `X-Bonoos-Authorization` gateway fallback | ✅ |
| **Refund** (`order_type: "refund"`) | ❌ server returns "Возврат пока не поддерживается" — Phase 1.5. Plugin may send it, but expect `ok: false`. |
| Tenant Telegram ops-group fanout on iiko path | ❌ Phase 2 |

Target only `config_version == 2` / TenantBrand tenants (the brand-aware loyalty path).

---

## 9. Build acceptance checklist

- [ ] Plugin loads in iikoFront without errors (verify against SDK sample manifest layout).
- [ ] On card scan, `POST /client/lookup/` succeeds and cashier sees balance + cashback%.
- [ ] On item change, `POST /order/precheck/` quote renders in cashier + customer info.
- [ ] Pay-with-bonus adds the configured bonus tender for the reserved kopecks; removing it fires `cancel`.
- [ ] Abandoning an order after `pay-by-bonus` fires `cancel` (no orphaned reservation).
- [ ] Closing the check fires `confirm` with **all** payments incl. the bonus line + `closed_at` with tz.
- [ ] Re-closing / network-retry yields `duplicate: true`, not a second credit.
- [ ] Both `Authorization` and `X-Bonoos-Authorization` headers are sent.
- [ ] All money in requests is rubles; all money read from responses is kopecks (and the credit/debit field names are interpreted per §5).
- [ ] SDK reference DLLs are **not** committed to source control; `.gitignore` covers `bin/ obj/ *.user packages/`.

---

## 10. Quick reference

| Endpoint | Method | Fires when | Writes state? | Money in req | Key response fields |
|---|---|---|---|---|---|
| `/client/lookup/` | POST | card captured | no | — | `found`, `balance_kopecks`, `cashback_percent` |
| `/order/precheck/` | POST | items change | no | items (₽) | `client.availableAmount`, `cashierInformation` |
| `/order/pay-by-bonus/` | POST | pay w/ bonus | **yes (reserve)** | items + `amount` (₽) | `document.positions[].paidAmount` (kopecks) |
| `/order/pay-by-bonus/cancel/` | POST | bonus removed / abandon | yes (release) | items + `amount` (₽) | envelope `code`/`error` |
| `/order/confirm/` | POST | check closed | **yes (commit)** | items + payments (₽) | `ok`, `duplicate`, `fiscal_amount`, `bonus_debit_amount` (earned) |
