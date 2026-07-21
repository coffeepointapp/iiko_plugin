using System;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>Path Geometry для кнопки на экране заказа (как Sagi ButtonGeometry).</summary>
    internal static class BonoosButtonGeometry
    {
        // Простая иконка «человек» 1000×1000, F0 = EvenOdd
        public const string ForToolbar =
            "F0 M1000,1000z M0,0z " +
            "M 500,180 A 140,140 0 1 1 499.9,180 Z " +
            "M 280,520 C 280,400 720,400 720,520 L 720,820 L 280,820 Z";
    }
}
