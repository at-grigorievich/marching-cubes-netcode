namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Когда можно показывать рекламу (M5.3). Чистая логика над счётчиками профиля — тесты вне Unity.
    ///
    /// Межстраничная — только на переходах, которые игрок и так ждёт: «Итоги → новый забег» и между
    /// ярусами; НЕ на кнопке «В лагерь» (навигация — CrazyGames такое отклоняет), не раньше третьего
    /// забега (первые забеги решают, останется ли игрок) и не чаще раза в три минуты. За награду —
    /// только по кнопке и раз на место: возрождение и переброс — раз за забег/уровень (их держат
    /// экраны), «ящик дня» — раз в день.
    /// </summary>
    public static class AdPolicy
    {
        /// <summary>С какого по счёту забега показывается межстраничная перед ним.</summary>
        public const int FirstInterstitialRun = 3;

        /// <summary>Не чаще раза в столько секунд реального времени.</summary>
        public const long InterstitialGap = 180;

        public const int DailyBoxChitin = 60;

        /// <param name="betweenRuns">Переход «итоги → новый забег»: действует порог третьего забега.</param>
        public static bool CanInterstitial(PlayerProfile profile, long now, bool betweenRuns)
        {
            if (profile == null) return false;
            if (betweenRuns && profile.Stats.Runs + 1 < FirstInterstitialRun) return false;

            return now - profile.Ads.LastInterstitialAt >= InterstitialGap;
        }

        public static void MarkInterstitial(PlayerProfile profile, long now)
        {
            profile.Ads.LastInterstitialAt = now;
            profile.Ads.RunsSinceInterstitial = 0;
        }

        public static bool CanClaimDailyBox(PlayerProfile profile, int today) => profile != null && profile.Ads.DailyBoxDay != today;

        /// <summary>Выдать ящик дня — только после досмотренной рекламы. Возвращает выданный хитин.</summary>
        public static float ClaimDailyBox(PlayerProfile profile, int today)
        {
            if (!CanClaimDailyBox(profile, today)) return 0f;

            profile.Ads.DailyBoxDay = today;
            profile.AddChitin(DailyBoxChitin);
            return DailyBoxChitin;
        }
    }
}
