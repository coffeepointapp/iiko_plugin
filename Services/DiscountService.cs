using System;
using System.Linq;
using System.Threading;
using Resto.Front.Api;
using Resto.Front.Api.Data.Orders;
using Resto.Front.Api.Editors;
using Resto.Front.Api.Editors.Stubs;
using Resto.Front.Api.Extensions;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// Скидка «свободная сумма» в открытом заказе.
    /// Важно (Irbis / iiko SDK #224):
    /// — не вызывать GetOrderById на заказ, который уже в edit session;
    /// — использовать IOperationService с экрана заказа (скан / TryEditCurrentOrder);
    /// — stub = сам IOrder из сессии, без повторного GetOrderById.
    /// </summary>
    internal sealed class DiscountService
    {
        private readonly string _discountTypeName;
        private readonly Action<string> _log;

        public DiscountService(string discountTypeName, Action<string> log)
        {
            _discountTypeName = string.IsNullOrWhiteSpace(discountTypeName)
                ? "Свободная сумма"
                : discountTypeName.Trim();
            _log = log ?? (_ => { });
        }

        public string DiscountTypeName => _discountTypeName;

        public IDiscountType FindFlexibleDiscountType(IOperationService os)
        {
            if (os == null)
                return null;

            var types = os.GetDiscountTypes();
            if (types == null)
                return null;

            return types.FirstOrDefault(t =>
                t != null &&
                !t.Deleted &&
                t.IsActive &&
                t.DiscountByFlexibleSum &&
                string.Equals(t.Name?.Trim(), _discountTypeName, StringComparison.OrdinalIgnoreCase));
        }

        public static decimal CalculateAmount(IOrder order, int discountPercent, IOperationService os = null)
        {
            if (order == null || discountPercent <= 0)
                return 0m;

            var baseSum = order.FullSum > 0 ? order.FullSum : order.ResultSum;
            if (baseSum <= 0)
                return 0m;

            var raw = baseSum * discountPercent / 100m;
            var amount = RoundToCurrency(os, raw);
            if (amount <= 0)
                return 0m;
            return amount > baseSum ? RoundToCurrency(os, baseSum) : amount;
        }

        /// <summary>
        /// Тенге и др. валюты без копеек: 22.5 → 23 (или 22 по Midpoint).
        /// Источник: GetHostRestaurant().Currency.FractionalPartLength / MinimumDenomination.
        /// </summary>
        public static decimal RoundToCurrency(IOperationService os, decimal amount)
        {
            if (amount <= 0)
                return 0m;

            try
            {
                var ops = os ?? PluginContext.Operations;
                var currency = ops?.GetHostRestaurant()?.Currency;
                if (currency == null)
                {
                    // Fallback: без дробной части (как KZT в этом зале)
                    return Math.Round(amount, 0, MidpointRounding.AwayFromZero);
                }

                var digits = currency.FractionalPartLength;
                if (digits < 0) digits = 0;
                if (digits > 4) digits = 4;

                var rounded = Math.Round(amount, digits, MidpointRounding.AwayFromZero);

                var min = currency.MinimumDenomination;
                if (min > 0m)
                {
                    var steps = Math.Round(rounded / min, MidpointRounding.AwayFromZero);
                    rounded = steps * min;
                    // на всякий случай ещё раз обрежем до digits
                    rounded = Math.Round(rounded, digits, MidpointRounding.AwayFromZero);
                }

                return rounded;
            }
            catch
            {
                return Math.Round(amount, 0, MidpointRounding.AwayFromZero);
            }
        }

        /// <summary>
        /// Навесить скидку в текущей сессии редактирования заказа.
        /// <paramref name="sessionOrder"/> — заказ из скана / TryEditCurrentOrder (не через GetOrderById).
        /// </summary>
        public bool ApplyPercentDiscount(
            IOrder sessionOrder,
            int discountPercent,
            IOperationService os,
            decimal? previousAmount = null)
        {
            if (sessionOrder == null || os == null || discountPercent <= 0)
                return false;

            var baseSum = sessionOrder.FullSum > 0 ? sessionOrder.FullSum : sessionOrder.ResultSum;
            if (baseSum <= 0)
            {
                _log($"Bonoos: [DISCOUNT] skip — пустой заказ #{sessionOrder.Number} (FullSum/ResultSum=0), скидку не трогаем");
                return false;
            }

            _log($"Bonoos: [DISCOUNT] apply start order=#{sessionOrder.Number} status={sessionOrder.Status} " +
                 $"%={discountPercent} FullSum={sessionOrder.FullSum:N2} ResultSum={sessionOrder.ResultSum:N2} " +
                 $"prev={previousAmount?.ToString("N2") ?? "null"} discounts={sessionOrder.Discounts?.Count ?? 0}");

            if (sessionOrder.Status != OrderStatus.New)
            {
                _log($"Bonoos: [DISCOUNT] skip — нужен New, сейчас {sessionOrder.Status}");
                return false;
            }

            var discountType = FindFlexibleDiscountType(os);
            if (discountType == null)
            {
                _log($"Bonoos: [DISCOUNT] тип «{_discountTypeName}» не найден " +
                     $"(проверь IsActive + DiscountByFlexibleSum в iikoOffice).");
                LogAvailableFlexibleTypes(os);
                return false;
            }

            var amount = CalculateAmount(sessionOrder, discountPercent, os);
            try
            {
                var currency = os.GetHostRestaurant()?.Currency;
                _log($"Bonoos: [DISCOUNT] type ok id={discountType.Id} name=«{discountType.Name}» " +
                     $"amount={amount:N2} currency={currency?.IsoName ?? "?"} " +
                     $"fracDigits={currency?.FractionalPartLength} minDenom={currency?.MinimumDenomination}");
            }
            catch
            {
                _log($"Bonoos: [DISCOUNT] type ok id={discountType.Id} amount={amount:N2}");
            }

            if (amount <= 0)
            {
                // Не вызываем Remove на пустом/нулевом — только если наша скидка уже висит.
                if (HasOurDiscount(sessionOrder))
                {
                    _log("Bonoos: [DISCOUNT] amount=0 — снимаем ранее повешенную скидку");
                    RemoveOurDiscount(sessionOrder, os);
                }
                else
                {
                    _log("Bonoos: [DISCOUNT] amount=0 — нечего применять");
                }
                return false;
            }

            if (previousAmount.HasValue &&
                Math.Abs(previousAmount.Value - amount) < 0.005m &&
                HasOurDiscount(sessionOrder))
            {
                _log($"Bonoos: [DISCOUNT] already applied {amount:N2} — skip");
                return true;
            }

            try
            {
                var credentials = os.GetDefaultCredentials();
                var session = os.CreateEditSession();
                // Irbis: stub = session order, БЕЗ GetOrderById
                var stub = (IOrderStub)(object)sessionOrder;

                var removed = 0;
                foreach (var item in (sessionOrder.Discounts ?? Enumerable.Empty<IDiscountItem>()).ToList())
                {
                    var name = item?.DiscountType?.Name?.Trim();
                    if (!string.Equals(name, _discountTypeName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    session.DeleteDiscount(item, stub);
                    removed++;
                }

                session.AddFlexibleSumDiscount(amount, discountType, stub);
                _log($"Bonoos: [DISCOUNT] session: delete={removed}, add amount={amount:N2}, HasChanges={session.HasChanges}");

                if (session.HasChanges)
                    os.SubmitChanges(session, credentials);

                _log($"Bonoos: [DISCOUNT] OK «{_discountTypeName}» {discountPercent}% → {amount:N2} " +
                     $"(база {baseSum:N2}) order=#{sessionOrder.Number}");
                return true;
            }
            catch (Exception ex)
            {
                _log($"Bonoos: [DISCOUNT] Apply failed ({ex.GetType().Name}): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Применить через TryEditCurrentOrder — когда заказ открыт на экране, а своего os нет.
        /// </summary>
        public bool ApplyViaTryEditCurrentOrder(
            Guid orderId,
            int discountPercent,
            decimal? previousAmount,
            out decimal? appliedAmount)
        {
            appliedAmount = null;
            var ok = false;
            decimal? got = null;

            try
            {
                PluginContext.Operations.TryEditCurrentOrder(args =>
                {
                    if (args.order == null || args.order.Id != orderId)
                    {
                        _log($"Bonoos: [DISCOUNT] TryEditCurrentOrder — другой заказ " +
                             $"(want={orderId}, got={args.order?.Id})");
                        return;
                    }

                    _log($"Bonoos: [DISCOUNT] TryEditCurrentOrder hit order=#{args.order.Number} status={args.order.Status}");
                    ok = ApplyPercentDiscount(args.order, discountPercent, args.os, previousAmount);
                    if (ok)
                        got = CalculateAmount(args.order, discountPercent, args.os);
                });
            }
            catch (Exception ex)
            {
                _log($"Bonoos: [DISCOUNT] TryEditCurrentOrder failed: {ex.Message}");
                return false;
            }

            appliedAmount = got;
            return ok;
        }

        public bool HasOurDiscount(IOrder order)
        {
            if (order?.Discounts == null)
                return false;
            return order.Discounts.Any(d =>
                string.Equals(d?.DiscountType?.Name?.Trim(), _discountTypeName, StringComparison.OrdinalIgnoreCase));
        }

        public void RemoveOurDiscount(IOrder sessionOrder, IOperationService os)
        {
            if (sessionOrder == null || os == null)
                return;

            try
            {
                // Bill: снять нельзя через SDK — только New.
                if (sessionOrder.Status != OrderStatus.New)
                {
                    _log($"Bonoos: [DISCOUNT] remove skip — статус {sessionOrder.Status} (нужен New)");
                    return;
                }

                var all = (sessionOrder.Discounts ?? Enumerable.Empty<IDiscountItem>()).ToList();
                var names = string.Join(", ", all.Select(d => $"«{d?.DiscountType?.Name ?? "?"}»"));
                _log($"Bonoos: [DISCOUNT] remove scan order=#{sessionOrder.Number} discounts=[{names}] looking=«{_discountTypeName}»");

                var ours = all.Where(d => IsOurDiscountItem(d, os)).ToList();

                if (ours.Count == 0)
                {
                    _log("Bonoos: [DISCOUNT] remove — наша скидка не найдена в order.Discounts");
                    return;
                }

                var credentials = os.GetDefaultCredentials();

                // 1) Прямой DeleteDiscount (extension) — надёжнее из кнопки «Гость»
                try
                {
                    foreach (var item in ours)
                        os.DeleteDiscount(item, sessionOrder, credentials);

                    _log($"Bonoos: [DISCOUNT] снята «{_discountTypeName}» x{ours.Count} (direct) order=#{sessionOrder.Number}");
                    return;
                }
                catch (Exception exDirect)
                {
                    _log($"Bonoos: [DISCOUNT] direct DeleteDiscount failed: {exDirect.Message} — try EditSession");
                }

                // 2) Fallback EditSession
                var session = os.CreateEditSession();
                var stub = (IOrderStub)(object)sessionOrder;
                foreach (var item in ours)
                    session.DeleteDiscount(item, stub);

                if (session.HasChanges)
                    os.SubmitChanges(session, credentials);

                _log($"Bonoos: [DISCOUNT] снята «{_discountTypeName}» (EditSession) order=#{sessionOrder.Number}");
            }
            catch (Exception ex)
            {
                _log($"Bonoos: [DISCOUNT] Remove failed: {ex.Message}");
            }
        }

        private bool IsOurDiscountItem(IDiscountItem item, IOperationService os)
        {
            if (item?.DiscountType == null)
                return false;

            var name = item.DiscountType.Name?.Trim();
            if (string.Equals(name, _discountTypeName, StringComparison.OrdinalIgnoreCase))
                return true;

            // Fallback: тот же тип «свободная сумма», что мы ищем в справочнике
            if (item.DiscountType.DiscountByFlexibleSum)
            {
                var ours = FindFlexibleDiscountType(os);
                if (ours != null && item.DiscountType.Id == ours.Id)
                    return true;
            }

            return false;
        }

        private void LogAvailableFlexibleTypes(IOperationService os)
        {
            try
            {
                var flex = os.GetDiscountTypes()?
                    .Where(t => t != null && !t.Deleted && t.DiscountByFlexibleSum)
                    .Select(t => $"«{t.Name}» active={t.IsActive}")
                    .ToList();
                _log($"Bonoos: [DISCOUNT] flexible types in iiko: " +
                     (flex == null || flex.Count == 0 ? "(none)" : string.Join("; ", flex)));
            }
            catch (Exception ex)
            {
                _log($"Bonoos: [DISCOUNT] list types failed: {ex.Message}");
            }
        }

        /// <summary>Фоновый retry через TryEditCurrentOrder (после модалки / скана).</summary>
        public void ScheduleApplyViaTryEdit(
            Guid orderId,
            int discountPercent,
            Func<decimal?> getPreviousAmount,
            Action<decimal?> onApplied)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (var attempt = 1; attempt <= 8; attempt++)
                {
                    try
                    {
                        Thread.Sleep(attempt == 1 ? 200 : 250 * attempt);

                        // Не долбим TryEdit на пустом/уже Bill заказе — отсюда был «зависон».
                        IOrder snap = null;
                        try { snap = PluginContext.Operations.GetOrderById(orderId); }
                        catch { /* заказ занят — попробуем TryEdit ниже */ }

                        if (snap != null)
                        {
                            if (snap.Status != OrderStatus.New)
                            {
                                _log($"Bonoos: [DISCOUNT] scheduled stop — status={snap.Status}");
                                return;
                            }

                            var baseSum = snap.FullSum > 0 ? snap.FullSum : snap.ResultSum;
                            if (baseSum <= 0)
                            {
                                _log($"Bonoos: [DISCOUNT] scheduled skip attempt {attempt} — пустой заказ");
                                continue;
                            }

                            var want = CalculateAmount(snap, discountPercent);
                            if (want <= 0)
                            {
                                _log($"Bonoos: [DISCOUNT] scheduled stop — want amount=0");
                                return;
                            }
                        }

                        var prev = getPreviousAmount?.Invoke();
                        if (ApplyViaTryEditCurrentOrder(orderId, discountPercent, prev, out var applied) &&
                            applied.HasValue && applied.Value > 0)
                        {
                            onApplied?.Invoke(applied);
                            _log($"Bonoos: [DISCOUNT] scheduled OK attempt={attempt}");
                            return;
                        }

                        _log($"Bonoos: [DISCOUNT] scheduled attempt {attempt}/8 — not applied yet");
                    }
                    catch (Exception ex)
                    {
                        _log($"Bonoos: [DISCOUNT] scheduled attempt {attempt}/8: {ex.Message}");
                    }
                }

                _log($"Bonoos: [DISCOUNT] FAILED after retries — скидка не встала на заказ {orderId}");
            });
        }
    }
}
