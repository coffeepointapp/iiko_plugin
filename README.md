# Bonoos iikoFront Loyalty Plugin

Плагин **.NET Framework (net472)** для **iikoFront / Resto.Front.Api V9**: лояльность Bonoos на кассе — lookup гостя, скидка или списание бонусов, precheck/confirm.

Бэкенд Bonoos — отдельный репозиторий. Этот репозиторий — только клиент кассы.

## Документация

| Документ | Содержание |
|----------|------------|
| **[HOW_IT_WORKS.md](HOW_IT_WORKS.md)** | Как работает плагин целиком (потоки, скидка, оплата, демо-срок) |
| [PLUGIN_SPEC.md](PLUGIN_SPEC.md) | Контракт REST API |
| [openapi.yaml](openapi.yaml) | OpenAPI |
| [README_ON_VM.md](README_ON_VM.md) | Сборка и деплой на Windows VM |
| [SDK_BINDING.md](SDK_BINDING.md) | Привязка к SDK V9 |

## Структура

```
Plugin.cs                          Точка входа, события заказа
Services/BonoosPaymentProcessor.cs Внешняя ПС Bonoos
Services/BonoosApiClient.cs        HTTP
Services/OrderTracker.cs           Состояние по заказу
Services/DiscountService.cs        Скидка «свободная сумма»
Services/BonoosUiManager.cs        Кнопка «Гость»
Models/                            DTO + конфиг
Manifest.xml                       Манифест плагина
```

Конфиг runtime: `%AppData%\iiko\CashServer\PluginConfigs\Bonoos\Bonoos.LoyaltyPlugin.config.json`  
(создаётся при первом запуске).

Папка `tools/` в git не входит.

## Быстрый старт

1. VS / Rider, workload .NET desktop, target **net472**.
2. NuGet: `Resto.Front.Api.V9` (уже в `.csproj`).
3. Build → DLL + `Manifest.xml` в Plugins iikoFront.
4. Настроить тип оплаты Bonoos и скидку «Discount Bonoos» (имя из конфига) в iikoOffice.
5. Подробности — [HOW_IT_WORKS.md](HOW_IT_WORKS.md) и [README_ON_VM.md](README_ON_VM.md).
