# Bonoos iikoFront — как работает плагин

Полное описание поведения плагина лояльности **Bonoos** для **iikoFront V9** (Resto.Front.Api).  
Контракт API с бэкендом — в [PLUGIN_SPEC.md](PLUGIN_SPEC.md) и [openapi.yaml](openapi.yaml).  
Сборка/деплой на ВМ — [README_ON_VM.md](README_ON_VM.md).

---

## 1. Что это

Плагин — тонкий клиент на кассе:

1. Привязывает гостя (QR / карта / штрихкод).
2. Для карт **бонусов** (`LOYALTY_COINS`) — списывает бонусы как внешний тип оплаты **Bonoos**.
3. Для карт **скидки** (`DISCOUNT`) — вешает в заказ скидку «свободная сумма» по % с сервера; бонусы и начисление запрещены.
4. На пречек (Bill) один раз шлёт `precheck`, при закрытии чека — `confirm` (кроме DISCOUNT).

Вся математика лояльности на сервере Bonoos. Плагин не считает кэшбэк сам.

---

## 2. Запуск и демо-срок

При создании `Plugin`:

1. **`ConfigLoader`** — читает/создаёт JSON в AppData (не рядом с DLL):
   `%AppData%\iiko\CashServer\PluginConfigs\Bonoos\Bonoos.LoyaltyPlugin.config.json`
2. Регистрация внешней ПС, кнопки «Гость», подписки на события, таймер refresh гостя.

Поля конфига: `baseUrl`, `tenantId`, `serviceAccountToken`, `timeoutSeconds`, `flexibleDiscountName` (по умолчанию «Discount Bonoos»), `fullLogs`.

---

## 3. Карта файлов

| Файл | Роль |
|------|------|
| `Plugin.cs` | Точка входа: init, скан, OrderChanged, Bill→precheck, Closed→confirm, status bar |
| `Services/BonoosPaymentProcessor.cs` | `IPaymentProcessor`: CollectData / OnPaymentAdded / Pay / Cancel |
| `Services/BonoosApiClient.cs` | HTTP к Bonoos + логи REQUEST/RESPONSE |
| `Services/OrderTracker.cs` | Состояние гостя по заказу в памяти + вызовы API |
| `Services/DiscountService.cs` | % → сумма → `AddFlexibleSumDiscount` / снятие |
| `Services/BonoosUiManager.cs` | Кнопка «Гость»: инфо / отвязка |
| `Services/OrderGuestStore.cs` | JSON-снимок гостей рядом с DLL |
| `Services/ApiAuditStore.cs` | JSON-аудит API рядом с DLL |
| `Services/GuestRefreshService.cs` | Раз в ~5 мин: lookup, если гость старше 1 ч |
| `Services/LoyaltyPaymentScreenService.cs` | Экран оплаты (gate / подсказки) |
| `Models/*` | DTO + конфиг |

Рядом с DLL (не в AppData): `Bonoos_order_guests.json`, `Bonoos_loyalty_audit.json`.  
При **закрытии смены** (`CafeSessionClosing`) оба JSON очищаются.

Папка `tools/` в репозиторий **не** входит (эмулятор сканера для отладки).

---

## 4. Жизненный цикл заказа

```
Скан QR / карта / штрихкод
        │
        ▼
 POST /client/lookup/     → карточка гостя, status bar, JSON-снимок
        │
        ├─ card_type = DISCOUNT
        │     → скидка из конфига (flexibleDiscountName) = FullSum × % / 100 (округление валюты кассы)
        │     → при смене состава (New) — пересчёт
        │     → бонусы платить нельзя; confirm всё равно уходит при закрытии
        │
        └─ card_type = LOYALTY_COINS (или пусто)
              → баланс в копейках; оплата типом Bonoos

Переход в Bill (пречек)
        │
        ▼
 POST /order/precheck/    → один раз на order_id (не на каждый OrderChanged)

Оплата бонусами (только cashback-карты)
        │
        ▼
 CollectData → OnPaymentAdded (сумма) → Pay → POST /order/pay-by-bonus/
 Отмена строки → POST /order/pay-by-bonus/cancel/

Закрытие чека (Closed)
        │
        ▼
 POST /order/confirm/     → всегда (card опционален; без гостя тоже)
        + guest JSON хранится до закрытия смены (для возвратов)

Возврат с типом оплаты Bonoos
        │
        ▼
 POST /order/pay-by-bonus/cancel/  (сумма возврата, card + items)```

---

## 5. Привязка гостя

### Вход
- `OrderEditCardSlided` / `OrderEditBarcodeScanned` → разбор через `ScannerInput` → `CardInfo` (`track` и/или `number`).
- Кнопка **«Гость»** не ищет гостя: только показывает карточку или «Гость не привязан».

### После успешного lookup
- Память: `OrderTracker` (`Card`, `CardType`, `DiscountPercent`, баланс, имя…).
- Файл: `OrderGuestStore` (восстановление после перезапуска Host / soft-deny оплаты).
- Status bar на заказе с кратким текстом.
- DISCOUNT → `SyncDiscountForOrder` (через `os` сессии скана или `TryEditCurrentOrder`).

### Отвязка (кнопка «Гость» → Отвязать)
1. Снять скидку из конфига (`DeleteDiscount` на `os` кнопки).
2. Удалить строки оплаты Bonoos.
3. В фоне — cancel резерва `pay-by-bonus`, если был.
4. Очистить память + JSON + status bar.

Важно: внутри обработчика кнопки **не** вызывать второй `ShowOkPopup` и не вкладывать лишний `TryEditCurrentOrder` вместе с модалками — Host зависает («сторонний плагин»).

---

## 6. Скидка (DISCOUNT)

1. Сервер отдаёт `discount_percent`.
2. Плагин считает сумму от **FullSum** (база до скидок): `amount = FullSum × % / 100`, округление по валюте ресторана (`FractionalPartLength`, `MinimumDenomination`) — для KZT часто целые тенге (например 15% от 150 → **23**, не 22.50).
3. Тип скидки в iikoOffice: активный, **DiscountByFlexibleSum**, имя = `flexibleDiscountName` (по умолчанию «Discount Bonoos»).
4. Применять только пока заказ в статусе **New**. На Bill состав через SDK уже не правят.
5. Не вызывать `GetOrderById` на заказ, который уже в edit-session (EntityAlreadyInUse / SDK #224) — stub = `IOrder` из сессии.
6. Пустой заказ (`FullSum = 0`) — скидку не вешать и не спамить retry.

При отвязке / смене типа карты скидка снимается.

---

## 7. Оплата бонусами (LOYALTY_COINS)

Внешняя ПС: `PaymentSystemKey = bonoos-loyalty`, имя **Bonoos**.

### CollectData
Только проверки, **без диалогов** (иначе зависание «Сбор данных»).

Бизнес-отказы (нет гостя, DISCOUNT, баланс 0) — **soft-return без throw**, чтобы:
- Visual Studio не «прерывал» на `PaymentActionFailedException`;
- Host не залипал на «Сбор данных».

### OnPaymentAdded
Все UI-диалоги:
- отказ → удалить строку оплаты + `ShowErrorPopup` (без throw для soft-deny);
- спросить сумму списания (не больше баланса и остатка к оплате);
- `ChangePaymentItemSum` + зафиксировать `LockedBonusPaymentSum` (нумпад потом сумму не меняет).

### Pay
`POST /order/pay-by-bonus/` с `order_id`, позициями, картой, суммой.

### Cancel / удаление строки
`POST /order/pay-by-bonus/cancel/` + сброс lock суммы.

Восстановление гостя из JSON, если память пуста — до проверок оплаты.

---

## 8. Precheck и confirm

| Момент | API | Условие |
|--------|-----|---------|
| Первый переход заказа в **Bill** | `POST /order/precheck/` | Есть привязанная карта; **один раз** на `order_id` |
| **Closed** | `POST /order/confirm/` | **Всегда** (guest optional; без гостя / без бонусов тоже) |
| Возврат оплаты Bonoos | `POST /order/pay-by-bonus/cancel/` | `ReturnPayment*` — сумма возврата + card из JSON/памяти |

`OrderChanged` на каждый чих **не** дергает precheck.

---

## 9. Refresh гостя (1 час)

`GuestRefreshService`: таймер ~каждые 5 минут.

Если в JSON/памяти гость с `LastLookupAtUtc` старше **1 часа** и заказ ещё жив → повторный `lookup`.  
Для DISCOUNT при статусе New — пересчёт скидки.

---

## 10. Логи и диагностика

- Host-лог iiko: строки `Bonoos: …`, `>>> REQUEST` / `<<< RESPONSE` (URL, статус, мс, тело — длина зависит от `fullLogs`).
- `Bonoos_loyalty_audit.json` — журнал запросов рядом с DLL.
- `Bonoos_order_guests.json` — привязки гостей по заказам.

Типичные ловушки:
- Диалоги в `CollectData` → зависание «Сбор данных».
- `GetOrderById` + правка открытого заказа → EntityAlreadyInUse.
- `PaymentActionFailedException` в отладчике VS при Break when thrown — штатный отказ Host, не обязательно «краш».
- Скидка не снимается после отвязки — смотреть лог `[DISCOUNT] remove scan` (имя типа, статус New, ошибка DeleteDiscount).

---

## 11. Настройка в iikoOffice (кратко)

1. Внешний тип оплаты → система **Bonoos** / ключ `bonoos-loyalty`, привязать к безналу.
2. Скидка «Discount Bonoos» (или имя из конфига): активна, свободная сумма, доступна на терминале.
3. DLL + `Manifest.xml` в папку плагинов Front; конфиг править в AppData после первого запуска.

---
