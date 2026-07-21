using System;
using System.Linq;
using Resto.Front.Api;
using Resto.Front.Api.Data.Orders;
using Resto.Front.Api.Data.Payments;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>Лимиты списания — как Irbis LoyaltyPaymentHelper, для ПС Bonoos.</summary>
    internal static class LoyaltyPaymentHelper
    {
        public static bool IsBonusPayment(IPaymentItem payment)
        {
            if (payment?.Type == null)
                return false;

            try
            {
                var systemKey = PluginContext.Operations.GetPaymentSystemKey(payment.Type);
                if (!string.IsNullOrWhiteSpace(systemKey) &&
                    string.Equals(systemKey, "bonoos-loyalty", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // не внешний тип
            }

            return string.Equals(payment.Type.Name, "Bonoos", StringComparison.OrdinalIgnoreCase)
                || string.Equals(payment.Type.Name, "BOONOS", StringComparison.OrdinalIgnoreCase);
        }

        public static decimal SumBonusPayments(IOrder order, IPaymentItem ignore = null)
        {
            if (order?.Payments == null)
                return 0m;

            return order.Payments
                .Where(p => IsBonusPayment(p) && (ignore == null || p.Id != ignore.Id))
                .Sum(p => p.Sum);
        }

        public static decimal SumNonBonusPayments(IOrder order, IPaymentItem ignore = null)
        {
            if (order?.Payments == null)
                return 0m;

            return order.Payments
                .Where(p => !IsBonusPayment(p) && (ignore == null || p.Id != ignore.Id))
                .Sum(p => p.Sum);
        }

        /// <summary>min(баланс, остаток чека после не-бонусных оплат) минус уже добавленные бонусы.</summary>
        public static decimal GetMaxBonusAllowed(IOrder order, decimal balanceRubles, IPaymentItem paymentToIgnore = null)
        {
            if (order == null || balanceRubles <= 0)
                return 0m;

            var existingBonus = SumBonusPayments(order, paymentToIgnore);
            var nonBonus = SumNonBonusPayments(order, paymentToIgnore);
            var orderResult = order.ResultSum;
            var remainingAfterNonBonus = Math.Max(0m, orderResult - nonBonus);
            var totalBonusAllowed = Math.Min(balanceRubles, remainingAfterNonBonus);
            var newAllowed = Math.Max(0m, totalBonusAllowed - existingBonus);
            return Math.Floor(newAllowed * 100m) / 100m;
        }
    }
}
