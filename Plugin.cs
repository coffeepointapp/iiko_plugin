using System;
using Bonoos.iikoFront.LoyaltyPlugin.Models;
using Bonoos.iikoFront.LoyaltyPlugin.Services;

using System.Threading.Tasks;
using Resto.Front.Api;
using Resto.Front.Api.Attributes;
using Resto.Front.Api.Attributes.JetBrains;
using Resto.Front.Api.Data.Common;
using Resto.Front.Api.Data.Orders;
using Resto.Front.Api.Data.View;
using Resto.Front.Api.UI;

namespace Bonoos.iikoFront.LoyaltyPlugin
{
    /// <summary>
    /// Лояльность по схеме Irbis LOYALTY.md:
    /// кнопка «Гость/бонусы» + скан → lookup;
    /// оплата: CollectData проверки → OnPaymentAdded UI → Pay API.
    /// </summary>
    [UsedImplicitly]
    [PluginLicenseModuleId(21016318)]
    public sealed class Plugin : IFrontPlugin
    {
        private BonoosApiClient _apiClient;
        private OrderTracker _orderTracker;
        private DiscountService _discountService;
        private GuestRefreshService _guestRefresh;
        private BonoosPaymentProcessor _paymentProcessor;
        private BonoosUiManager _uiManager;
        private LoyaltyPaymentScreenService _paymentScreenService;
        private readonly LoyaltyUi _ui = new LoyaltyUi();
        private IDisposable _paymentSystemRegistration;
        private IDisposable _orderChangedSub;
        private IDisposable _cardSlidedSub;
        private IDisposable _barcodeSub;
        private IDisposable _cafeSessionClosingSub;
        private PluginConfiguration _config;

        private IDisposable _statusBarItem;
        private string _statusBarOrderId;
        private readonly object _statusBarLock = new object();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _discountSyncing =
            new System.Collections.Concurrent.ConcurrentDictionary<Guid, byte>();

        public Plugin()
        {
            try
            {
                // Демо для клиента: после 25.07.2026 плагин не поднимается.
                if (!DemoLicense.EnsureActive(Log))
                    return;

                _config = ConfigLoader.Load(Log);
                Log($"Bonoos: config = {ConfigLoader.AppDataConfigPath}");
                if (!_config.IsConfigured)
                {
                    Log("Bonoos: not configured — plugin idle");
                    return;
                }

                _apiClient = new BonoosApiClient(_config, Log);
                ApiAuditStore.Configure(Log);
                OrderGuestStore.Configure(Log);
                Log($"Bonoos: plugin dir = {PluginPaths.PluginDirectory}");
                Log($"Bonoos: API audit = {ApiAuditStore.AuditFilePath}");
                Log($"Bonoos: guest JSON = {OrderGuestStore.FilePath}");
                _apiClient.OnRequestFailed += (_, detail) =>
                    Log($"Bonoos: [API] FAIL — {detail}");

                _orderTracker = new OrderTracker(_apiClient);
                _orderTracker.OnClientLookedUp += OnClientLookedUp;

                _discountService = new DiscountService(_config.FlexibleDiscountName, Log);
                Log($"Bonoos: flexible discount type = «{_discountService.DiscountTypeName}»");

                _paymentProcessor = new BonoosPaymentProcessor(_orderTracker, Log);
                _paymentSystemRegistration =
                    PluginContext.Operations.RegisterPaymentSystem(_paymentProcessor);

                _uiManager = new BonoosUiManager(_orderTracker, _discountService, Log, OnGuestUnbound);
                _uiManager.InitializeButtons(PluginContext.Operations);

                _paymentScreenService = new LoyaltyPaymentScreenService(_orderTracker, Log);
                _paymentScreenService.Start();

                _guestRefresh = new GuestRefreshService(_orderTracker, _discountService, Log);

                _orderChangedSub = PluginContext.Notifications.OrderChanged.Subscribe(
                    new ActionObserver<EntityChangedEventArgs<IOrder>>(OnOrderChanged));
                _cardSlidedSub = PluginContext.Notifications.OrderEditCardSlided.Subscribe(OnCardSlided);
                _barcodeSub = PluginContext.Notifications.OrderEditBarcodeScanned.Subscribe(OnBarcodeScanned);

                try
                {
                    _cafeSessionClosingSub = PluginContext.Notifications.CafeSessionClosing.Subscribe(OnCafeSessionClosing);
                    Log("Bonoos: CafeSessionClosing — очистка JSON при закрытии смены");
                }
                catch (Exception ex)
                {
                    Log($"Bonoos: CafeSessionClosing subscribe failed — {ex.Message}");
                }

                Log("Bonoos: plugin initialized (guest + discount + payment + refresh)");
            }
            catch (Exception ex)
            {
                Log($"Bonoos: init error — {ex}");
            }
        }

        private void OnCafeSessionClosing((IReceiptPrinter printer, IViewManager vm) args)
        {
            try
            {
                Log("Bonoos: CafeSessionClosing — очищаем guest JSON + API audit");
                OrderGuestStore.ClearForCashSessionClose();
                ApiAuditStore.ClearForCashSessionClose();
                try
                {
                    _orderTracker?.CleanupAllAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                }
                catch { /* best-effort */ }
            }
            catch (Exception ex)
            {
                Log($"Bonoos: CafeSessionClosing cleanup — {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                if (_orderTracker != null)
                    _orderTracker.CleanupAllAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch { /* best-effort */ }

            _uiManager?.Dispose();
            _guestRefresh?.Dispose();
            _paymentScreenService?.Dispose();
            _orderChangedSub?.Dispose();
            _cardSlidedSub?.Dispose();
            _barcodeSub?.Dispose();
            _cafeSessionClosingSub?.Dispose();
            _statusBarItem?.Dispose();
            _paymentSystemRegistration?.Dispose();
            _paymentProcessor?.Dispose();
            _apiClient?.Dispose();
        }

        private bool OnCardSlided((CardInputDialogResult card, IOrder order, IOperationService os, IViewManager vm) args)
        {
            var raw = ScannerInput.ExtractCardPayload(args.card);
            Log($"Bonoos: [QR] card slide raw='{raw}' order={args.order?.Id}");
            if (args.order == null)
                return false;
            var card = ScannerInput.TryParseToCard(raw, out var reject);
            if (card == null)
            {
                Log($"Bonoos: [QR] ignored — {reject}");
                return false;
            }
            BindCardWithModal(args.order, card, args.vm, args.os);
            return true;
        }

        private bool OnBarcodeScanned((string barcode, IOrder order, IOperationService os, IViewManager vm) args)
        {
            Log($"Bonoos: [QR] barcode raw='{args.barcode}' order={args.order?.Id}");
            if (args.order == null)
                return false;
            var card = ScannerInput.TryParseToCard(args.barcode, out var reject);
            if (card == null)
            {
                Log($"Bonoos: [QR] pass-through — {reject}");
                return false;
            }
            BindCardWithModal(args.order, card, args.vm, args.os);
            return true;
        }

        private void BindCardWithModal(IOrder order, CardInfo card, IViewManager vm, IOperationService os)
        {
            var oid = order.Id.ToString();
            var number = order.Number.ToString();

            ClientLookupResponse lookup;
            try
            {
                lookup = _orderTracker.LookupClientAsync(oid, number, card)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log($"Bonoos: lookup error — {ex.Message}");
                _ui.ShowError(vm, "Лояльность", "Не удалось связаться с системой лояльности.");
                return;
            }

            if (lookup == null)
            {
                _ui.ShowError(vm, "Лояльность", "Нет ответа от системы лояльности.");
                return;
            }

            _ui.ShowClientCard(vm, lookup);

            if (!lookup.Found)
                return;

            // JSON снимок гостя по заказу (AppData, как Irbis)
            var sum = order.FullSum > 0 ? order.FullSum : order.ResultSum;
            OrderGuestStore.Save(OrderGuestStore.FromLookup(oid, number, sum, card, lookup));

            // Скидку — через os экрана заказа (скан) или TryEditCurrentOrder.
            SyncDiscountForOrder(order, os, scheduleRetry: true);
        }

        private void OnGuestUnbound(string orderId)
        {
            // Скидка/оплаты уже сняты в BonoosUiManager через os сессии кнопки.
            ClearClientStatusBar(orderId);
            if (_orderTracker.TryGetOrder(orderId, out var state))
                state.AppliedFlexibleDiscountSum = null;
        }

        /// <summary>
        /// DISCOUNT → % × FullSum → «Свободная сумма» в сессии редактирования заказа.
        /// </summary>
        private void SyncDiscountForOrder(IOrder sessionOrder, IOperationService os = null, bool scheduleRetry = false)
        {
            if (sessionOrder == null || _discountService == null)
                return;

            if (!_discountSyncing.TryAdd(sessionOrder.Id, 0))
            {
                Log($"Bonoos: [DISCOUNT] sync skip — already in progress order=#{sessionOrder.Number}");
                return;
            }

            try
            {
                var oid = sessionOrder.Id.ToString();

                if (!_orderTracker.TryGetOrder(oid, out var state) || state.Card == null)
                {
                    if (os != null)
                        _discountService.RemoveOurDiscount(sessionOrder, os);
                    return;
                }

                if (!state.IsDiscountCard || !(state.DiscountPercent is int pct) || pct <= 0)
                {
                    if (os != null && _discountService.HasOurDiscount(sessionOrder))
                        _discountService.RemoveOurDiscount(sessionOrder, os);
                    state.AppliedFlexibleDiscountSum = null;
                    return;
                }

                var baseSum = sessionOrder.FullSum > 0 ? sessionOrder.FullSum : sessionOrder.ResultSum;
                if (baseSum <= 0)
                {
                    Log($"Bonoos: [DISCOUNT] sync skip — пустой заказ #{sessionOrder.Number}");
                    return;
                }

                Log($"Bonoos: [DISCOUNT] sync order=#{sessionOrder.Number} status={sessionOrder.Status} " +
                    $"%{pct} os={(os != null ? "session" : "null→TryEdit")}");

                bool ok = false;
                decimal? applied = null;

                if (os != null)
                {
                    ok = _discountService.ApplyPercentDiscount(
                        sessionOrder, pct, os, state.AppliedFlexibleDiscountSum);
                    if (ok)
                        applied = DiscountService.CalculateAmount(sessionOrder, pct, os);
                }

                if (!ok)
                {
                    ok = _discountService.ApplyViaTryEditCurrentOrder(
                        sessionOrder.Id, pct, state.AppliedFlexibleDiscountSum, out applied);
                }

                if (ok && applied.HasValue && applied.Value > 0)
                {
                    state.AppliedFlexibleDiscountSum = applied;
                    return;
                }

                // Retry только если есть что вешать (сумма > 0).
                var want = DiscountService.CalculateAmount(sessionOrder, pct, os);
                if (scheduleRetry && want > 0)
                {
                    Log("Bonoos: [DISCOUNT] immediate apply failed — schedule TryEditCurrentOrder retries");
                    var orderId = sessionOrder.Id;
                    _discountService.ScheduleApplyViaTryEdit(
                        orderId,
                        pct,
                        () => _orderTracker.TryGetOrder(oid, out var s) ? s.AppliedFlexibleDiscountSum : null,
                        sum =>
                        {
                            if (_orderTracker.TryGetOrder(oid, out var s) && sum.HasValue)
                                s.AppliedFlexibleDiscountSum = sum;
                        });
                }
                else
                {
                    if (want <= 0)
                        Log("Bonoos: [DISCOUNT] no schedule — want amount=0");
                    state.AppliedFlexibleDiscountSum = null;
                }
            }
            catch (Exception ex)
            {
                Log($"Bonoos: [DISCOUNT] sync — {ex.Message}");
            }
            finally
            {
                _discountSyncing.TryRemove(sessionOrder.Id, out _);
            }
        }

        private void OnOrderChanged(EntityChangedEventArgs<IOrder> e)
        {
            var order = e.Entity;
            if (order == null)
                return;

            var oid = order.Id.ToString();

            if (order.Status == OrderStatus.Closed)
            {
                if (!_orderTracker.TryMarkConfirmSent(oid))
                    return;

                var card = _orderTracker.TryGetOrder(oid, out var state) ? state.Card : null;
                var isDiscount = state != null && state.IsDiscountCard;

                if (!isDiscount && card != null)
                {
                    RunSync(_orderTracker.ConfirmAsync(
                        oid, order.Number.ToString(), SdkMap.Items(order), SdkMap.Payments(order),
                        card, DateTimeOffset.Now.ToString("O")));
                }
                else if (isDiscount)
                {
                    Log($"Bonoos: [DISCOUNT] confirm skipped (accrual blocked) order=#{order.Number}");
                }

                _orderTracker.RemoveOrder(oid);
                OrderGuestStore.Remove(oid);
                ClearClientStatusBar(oid);
                return;
            }

            // DISCOUNT: пересчёт при изменении состава — через TryEditCurrentOrder (заказ на экране).
            if (order.Status == OrderStatus.New)
            {
                if (_orderTracker.TryGetOrder(oid, out _))
                    OrderGuestStore.UpdateOrderSum(oid, order.Number, order.FullSum > 0 ? order.FullSum : order.ResultSum);

                if (!_orderTracker.TryGetOrder(oid, out var state) || state.Card == null)
                    return;
                if (!state.IsDiscountCard || !(state.DiscountPercent is int pct) || pct <= 0)
                    return;

                var want = DiscountService.CalculateAmount(order, pct);
                if (want <= 0)
                    return;

                if (state.AppliedFlexibleDiscountSum.HasValue &&
                    Math.Abs(state.AppliedFlexibleDiscountSum.Value - want) < 0.005m)
                    return;

                Log($"Bonoos: [DISCOUNT] OrderChanged New — recalc want={want:N2} " +
                    $"had={state.AppliedFlexibleDiscountSum?.ToString("N2") ?? "null"}");

                if (_discountService.ApplyViaTryEditCurrentOrder(
                        order.Id, pct, state.AppliedFlexibleDiscountSum, out var applied) &&
                    applied.HasValue)
                {
                    state.AppliedFlexibleDiscountSum = applied;
                }

                return;
            }

            if (order.Status == OrderStatus.Bill)
            {
                if (!_orderTracker.TryGetOrder(oid, out var state) || state.Card == null)
                    return;

                if (!_orderTracker.TryMarkPrecheckSent(oid))
                    return;

                Log($"Bonoos: [API] precheck once on Bill order=#{order.Number}");
                FireAndForget(_orderTracker.PrecheckAsync(
                    oid, order.Number.ToString(), SdkMap.Items(order), state.Card));
            }
        }

        private static void RunSync(Task task)
        {
            try { task.ConfigureAwait(false).GetAwaiter().GetResult(); }
            catch { /* best-effort */ }
        }

        private void FireAndForget(Task task)
        {
            if (task == null) return;
            task.ContinueWith(
                t => Log($"Bonoos: background failed — {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        private void OnClientLookedUp(string orderId, ClientLookupResponse resp)
        {
            if (resp == null || !resp.Found)
                return;
            var name = !string.IsNullOrWhiteSpace(resp.FirstName)
                ? $"{resp.FirstName} {resp.LastName}".Trim()
                : (string.IsNullOrWhiteSpace(resp.Username) ? "клиент" : resp.Username);

            string text = resp.IsDiscountCard
                ? $"Bonoos: {name} • скидка {resp.DiscountPercent}%"
                : $"Bonoos: {name} • {resp.BalanceDisplay} ₽ • кэшбэк {resp.CashbackPercent}%";

            SetClientStatusBar(orderId, text);
        }

        private void SetClientStatusBar(string orderId, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_statusBarLock)
            {
                try
                {
                    _statusBarItem?.Dispose();
                    _statusBarItem = PluginContext.Operations.AddStatusBarInfo(text, false, 200, 2.0);
                    _statusBarOrderId = orderId;
                }
                catch (Exception ex) { Log($"Bonoos: status bar — {ex.Message}"); }
            }
        }

        private void ClearClientStatusBar(string orderId = null)
        {
            lock (_statusBarLock)
            {
                if (orderId != null && _statusBarOrderId != orderId)
                    return;
                try { _statusBarItem?.Dispose(); }
                catch { /* best-effort */ }
                _statusBarItem = null;
                _statusBarOrderId = null;
            }
        }

        private void Log(string message)
        {
            try { PluginContext.Log.Info(message); }
            catch { System.Diagnostics.Debug.WriteLine(message); }
        }
    }
}
