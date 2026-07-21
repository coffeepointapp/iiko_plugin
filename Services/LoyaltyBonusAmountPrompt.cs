using Resto.Front.Api;
using Resto.Front.Api.Data.View;
using Resto.Front.Api.UI;
using System;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>Как Irbis LoyaltyBonusAmountPrompt — ShowInputDialog суммы списания.</summary>
    internal static class LoyaltyBonusAmountPrompt
    {
        public static bool TryPromptBonusAmount(
            IViewManager viewManager,
            decimal maxAllowed,
            decimal balance,
            string guestName,
            decimal suggestedAmount,
            out decimal chosen,
            bool balanceAlreadyShown = false)
        {
            chosen = 0m;
            if (viewManager == null || maxAllowed <= 0)
                return false;

            var initial = suggestedAmount > 0m && suggestedAmount <= maxAllowed
                ? (int)Math.Floor(suggestedAmount)
                : (int)Math.Floor(maxAllowed);
            if (initial < 1 && maxAllowed >= 1m)
                initial = 1;

            var guestLine = string.IsNullOrWhiteSpace(guestName) ? string.Empty : $"Гость: {guestName}\n";
            var balanceBlock = balanceAlreadyShown
                ? string.Empty
                : $"Баланс: {balance:N2}\nМожно списать: {maxAllowed:N2}\n\n";
            var prompt =
                guestLine +
                balanceBlock +
                $"Сумма списания (макс. {maxAllowed:N0}):";

            var dialog = viewManager.ShowInputDialog(
                prompt,
                InputDialogTypes.Number,
                initial,
                "Списать",
                "Отмена");

            if (dialog == null)
            {
                PluginContext.Log.Info("Bonoos: [LOYALTY] диалог суммы: отмена (null).");
                return false;
            }

            if (!(dialog is NumberInputDialogResult numberResult))
            {
                PluginContext.Log.Info($"Bonoos: [LOYALTY] диалог суммы: не число ({dialog.GetType().Name}).");
                return false;
            }

            chosen = numberResult.Number;
            return true;
        }
    }
}
