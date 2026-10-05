using System.Collections.Generic;
using UnityEngine;
using S = MineGenerator.Catacombs.WeaponStat;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Стартовые числа восьми пушек (план M2.5) — источник правды. «Починить свет» пишет их
    /// в ассеты Resources/Data/Weapons явно (грабли №8); без ассета пушка строится отсюда же.
    ///
    /// Урон — в HP (мелочь 10, тарантул 40). Числа стартовые: их правит баланс (M6) по
    /// таблице DPS из WeaponCheck, и в CLAUDE.md пишется замер, а не «подобрано».
    /// </summary>
    public static class WeaponCatalog
    {
        public const string ResourceFolder = "Data/Weapons";

        private static readonly Dictionary<WeaponId, WeaponDef> Cache = new Dictionary<WeaponId, WeaponDef>();

        /// <summary>
        /// Описание пушки: ассет из Resources, а без него — сборка из каталога. Кэш с ключом
        /// по пушке (грабли №2: кэш без ключа по аргументу отдавал бы чужое).
        /// </summary>
        public static WeaponDef Get(WeaponId id)
        {
            if (Cache.TryGetValue(id, out var cached) && cached != null) return cached;

            var def = Resources.Load<WeaponDef>($"{ResourceFolder}/{WeaponIds.Key(id)}");
            if (def == null) def = Build(id);

            Cache[id] = def;
            return def;
        }

        /// <summary>Сбросить кэш — после того как «Починить свет» переписал ассеты.</summary>
        public static void ClearCache() => Cache.Clear();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => Cache.Clear();

        /// <summary>Свежее описание из каталога, без ассета.</summary>
        public static WeaponDef Build(WeaponId id)
        {
            var def = ScriptableObject.CreateInstance<WeaponDef>();
            def.hideFlags = HideFlags.DontSave;
            Fill(def, id);
            return def;
        }

        /// <summary>Проставить каталожные числа в описание (в том числе в ассет).</summary>
        public static void Fill(WeaponDef def, WeaponId id)
        {
            def.Id = id;
            def.Key = WeaponIds.Key(id);
            def.name = def.Key;
            def.Base = new float[(int)S.Count];
            def.PairLevel = 3;

            switch (id)
            {
                case WeaponId.Launcher: Launcher(def); break;
                case WeaponId.Revolver: Revolver(def); break;
                case WeaponId.Shotgun: Shotgun(def); break;
                case WeaponId.Nailgun: Nailgun(def); break;
                case WeaponId.Rail: Rail(def); break;
                case WeaponId.Flamer: Flamer(def); break;
                case WeaponId.Tesla: Tesla(def); break;
                case WeaponId.PickHammer: Hammer(def); break;
            }
        }

        private static void Set(WeaponDef def, S stat, float value) => def.Base[(int)stat] = value;

        private static WeaponLevel Level(string key, params WeaponStatMod[] mods) =>
            new WeaponLevel { TextKey = key, Mods = mods };

        private static WeaponStatMod Plus(S stat, float add) => WeaponStatMod.Plus(stat, add);

        private static WeaponStatMod Pct(S stat, float share) => WeaponStatMod.Percent(stat, share);

        // ------------------------------------------------------------------ 1. Барабан

        /// <summary>
        /// Гранатомёт переехал без изменения поведения: 6 гранат, выстрел раз в 0.32 с, перезарядка
        /// 2.1 с, 30 юнитов в секунду, радиус 5, урон 30 HP (прежние 3 попадания).
        /// Extra1 — тяжесть гранаты, Extra2 — взрыватель по близости, юниты.
        /// </summary>
        private static void Launcher(WeaponDef def)
        {
            Set(def, S.Damage, 30f);
            Set(def, S.Rate, 1f / 0.32f);
            Set(def, S.Magazine, 6f);
            Set(def, S.Reload, 2.1f);
            Set(def, S.Speed, 30f);
            Set(def, S.Area, 5f);
            Set(def, S.Range, 3.5f);
            Set(def, S.AltCharges, 3f);
            Set(def, S.AltCooldown, 0.4f);
            Set(def, S.TempHealth, 1f);
            Set(def, S.Extra1, 9f);
            Set(def, S.Extra2, 0.9f);

            def.Levels = new[]
            {
                Level("weapon.launcher.l2", Plus(S.Magazine, 1f)),
                Level("weapon.launcher.l3", Pct(S.Area, 0.15f)),
                Level("weapon.launcher.l4", Pct(S.Damage, 0.2f)),
                Level("weapon.launcher.l5", Pct(S.Reload, -0.2f), Plus(S.AltCharges, 1f))
            };

            def.PairTome = TomeId.Reach;
            def.Evolution = Level("weapon.launcher.evo", Plus(S.Projectiles, 5f));
            def.StartsUnlocked = true;
            def.Tint = new Color(1f, 0.72f, 0.3f);
        }

        // ------------------------------------------------------------------ 2. Монета

        /// <summary>
        /// Револьвер: мгновенный луч, 45 HP, пробивает 3 (−25% каждому следующему), 3 в секунду
        /// (в плане 2.5), 6 в барабане, перезарядка 1.2 с. Монеты: 4, по одной за 2.5 с, рикошет ×2.5.
        /// Плюс «добивание рикошетом» (<c>RevolverWeapon.ChainKills</c>) — его в плане нет: без него
        /// бот прогона убивал 2–3 паука в секунду против 18–20 у гранатомёта и гиб до первого гребня,
        /// а револьвер — стартовая пушка класса «Стрелок» (M4.5).
        /// Extra1 — множитель рикошета, Extra2 — дальность поиска цели рикошетом.
        /// </summary>
        private static void Revolver(WeaponDef def)
        {
            Set(def, S.Damage, 45f);
            Set(def, S.Rate, 3f);
            Set(def, S.Magazine, 6f);
            Set(def, S.Reload, 1.2f);
            Set(def, S.Range, 80f);
            Set(def, S.Area, 0.18f);
            Set(def, S.Pierce, 3f);
            Set(def, S.Knockback, 2f);
            Set(def, S.AltCharges, 4f);
            Set(def, S.AltCooldown, 2.5f);
            Set(def, S.TempHealth, 1f);
            Set(def, S.Extra1, 2.5f);
            Set(def, S.Extra2, 30f);

            def.Levels = new[]
            {
                Level("weapon.revolver.l2", Pct(S.Damage, 0.2f)),
                Level("weapon.revolver.l3", Plus(S.Pierce, 1f)),
                Level("weapon.revolver.l4", Plus(S.AltCharges, 1f), Pct(S.Reload, -0.2f)),
                Level("weapon.revolver.l5", Plus(S.Crit, 0.1f))
            };

            def.PairTome = TomeId.Precision;
            def.Evolution = Level("weapon.revolver.evo", Plus(S.Projectiles, 3f));
            def.StartsUnlocked = true;
            def.Tint = new Color(0.9f, 0.9f, 0.75f);
        }

        // ------------------------------------------------------------------ 3. Ядро

        /// <summary>
        /// Дробовик: конус 20° на 12 юнитов (в плане 9°), полный урон до 6 юнитов (в плане 4),
        /// дальше спад к 20%; 80 HP в упор, отброс 9, оглушение 0.4 с, 1.9 выстрела в секунду
        /// (в плане 1.1), 8 патронов по одному за 0.35 с (в плане 0.45). Ядро: откат 4 с,
        /// радиус 4, 50 HP; выстрел по ядру — ×2 радиуса, ×1.5 урона.
        ///
        /// Плановые числа мерились таблицей урона, где пауки стоят в линию; в бою они идут по полу,
        /// стенам и своду, и конус в 9° на четырёх юнитах (ширина 1.3 при ходе 3.5) брал одного-двух.
        /// Бот прогона с плановыми числами убивал 2.9 паука в секунду и гиб до первого гребня;
        /// с нынешними — доживает в трёх прогонах из трёх.
        /// </summary>
        private static void Shotgun(WeaponDef def)
        {
            Set(def, S.Damage, 80f);
            Set(def, S.Rate, 1.9f);
            Set(def, S.Magazine, 8f);
            Set(def, S.Reload, 0.35f);
            Set(def, S.Range, 12f);
            Set(def, S.Area, 20f);
            Set(def, S.Projectiles, 10f);
            Set(def, S.Knockback, 9f);
            Set(def, S.Status, 0.4f);
            Set(def, S.Speed, 18f);
            Set(def, S.AltCooldown, 4f);
            Set(def, S.AltDamage, 50f);
            Set(def, S.AltArea, 4f);
            Set(def, S.TempHealth, 2f);

            def.Levels = new[]
            {
                Level("weapon.shotgun.l2", Plus(S.Projectiles, 2f), Plus(S.Area, 2f)),
                Level("weapon.shotgun.l3", Pct(S.Knockback, 0.3f)),
                Level("weapon.shotgun.l4", Pct(S.Reload, -0.2f), Pct(S.AltArea, 0.25f)),
                Level("weapon.shotgun.l5", Pct(S.Damage, 0.2f))
            };

            def.PairTome = TomeId.Carapace;
            def.Evolution = Level("weapon.shotgun.evo", Pct(S.Knockback, 0.6f), Plus(S.Extra1, 4f));
            def.Price = 300;
            def.Tint = new Color(1f, 0.5f, 0.35f);
        }

        // ------------------------------------------------------------------ 4. Гвоздомёт

        /// <summary>
        /// 14 гвоздей в секунду, скорость 60, урон 10 (в плане 6), каждый пробивает одного (в плане
        /// пробитие — только у эволюции), замедляет на 6% до −45% на 1.5 с. С плановыми числами
        /// в паре с гранатомётом гвоздомёт был балластом; стартовой пушкой ни одного класса он
        /// не бывает (M4.5) и проверяется в паре: в руках убивает в темпе ~30% гранатомёта.
        /// Нагрев +5 за гвоздь из 100, остывание 35 в секунду, перегрев — 1.5 с блокировки.
        /// Магнит: 6 с, до 2, захват 8 юнитов, орбита 2.5. Extra1 — радиус орбиты.
        /// </summary>
        private static void Nailgun(WeaponDef def)
        {
            Set(def, S.Damage, 10f);
            Set(def, S.Rate, 14f);
            Set(def, S.Pierce, 1f);
            Set(def, S.Magazine, 100f);
            Set(def, S.Reload, 1.5f);
            Set(def, S.Spread, 2.2f);
            Set(def, S.Speed, 60f);
            Set(def, S.Range, 1.5f);
            Set(def, S.Status, 0.06f);
            Set(def, S.Drain, 5f);
            Set(def, S.Refill, 35f);
            Set(def, S.AltCharges, 2f);
            Set(def, S.AltDuration, 6f);
            Set(def, S.AltArea, 8f);
            Set(def, S.AltCooldown, 1f);
            Set(def, S.TempHealth, 1f);
            Set(def, S.Extra1, 2.5f);

            def.Levels = new[]
            {
                Level("weapon.nailgun.l2", Pct(S.Rate, 0.2f)),
                Level("weapon.nailgun.l3", Pct(S.Damage, 0.2f), Plus(S.Status, 0.02f)),
                Level("weapon.nailgun.l4", Plus(S.AltDuration, 3f)),
                Level("weapon.nailgun.l5", Pct(S.Drain, -0.25f))
            };

            def.PairTome = TomeId.Haste;
            def.Evolution = Level("weapon.nailgun.evo", Plus(S.Pierce, 2f), Pct(S.Damage, 0.5f));
            def.Price = 400;
            def.Tint = new Color(0.7f, 0.75f, 0.85f);
        }

        // ------------------------------------------------------------------ 5. Прострел

        /// <summary>
        /// Рельса: заряд 0.9 с (держать можно), луч на 120 юнитов радиусом 1.6 (в плане 0.8) сквозь
        /// всех, 220 HP, откат 6 с. Роль по плану — «прочистить галерею», а луч 0.8 в ходе шириной
        /// 3.5 брал полсечения: в паре с гранатомётом рельса набирала 3–4% убийств. С 1.6 — 23%
        /// темпа гранатомёта, пока в руках (в паре, M4.5: стартовой рельса не бывает). Отвёртка: 60 HP в секунду 4 с, общий откат. Extra1 — время заряда,
        /// Extra2 — множитель перезаряда (держать 2 с), 1 — нет перезаряда.
        /// </summary>
        private static void Rail(WeaponDef def)
        {
            Set(def, S.Damage, 220f);
            Set(def, S.Rate, 1f);
            Set(def, S.Magazine, 1f);
            Set(def, S.Reload, 6f);
            Set(def, S.Range, 120f);
            Set(def, S.Area, 1.6f);
            Set(def, S.Knockback, 10f);
            Set(def, S.AltDamage, 60f);
            Set(def, S.AltDuration, 4f);
            Set(def, S.TempHealth, 1f);
            Set(def, S.Extra1, 0.9f);
            Set(def, S.Extra2, 1f);

            def.Levels = new[]
            {
                Level("weapon.rail.l2", Pct(S.Reload, -0.15f)),
                Level("weapon.rail.l3", Pct(S.Damage, 0.25f)),
                Level("weapon.rail.l4", Pct(S.Area, 0.5f), Pct(S.Extra1, -0.3f)),
                Level("weapon.rail.l5", Plus(S.Extra2, 0.5f))
            };

            def.PairTome = TomeId.Supply;
            def.Evolution = Level("weapon.rail.evo", Plus(S.Magazine, 1f));
            def.Price = 800;
            def.Tint = new Color(0.65f, 0.85f, 1f);
        }

        // ------------------------------------------------------------------ 6. Напалм

        /// <summary>
        /// Огнемёт: конус 22° (полуугол 11) на 7 юнитов, 10 ударов в секунду по 9 HP, горение
        /// 3 с по 12 HP/с (Status). Топливо 100, −20 в секунду, +15 без стрельбы. Огонь на полу
        /// раз в 0.25 с, радиус 1.6, живёт 5 с (AltDuration). Выдох: 40 топлива, откат 6 с,
        /// кольцо 4.5, отброс 12. Extra1 — длительность горения, Extra2 — пятен на полу максимум.
        /// </summary>
        private static void Flamer(WeaponDef def)
        {
            Set(def, S.Damage, 9f);
            Set(def, S.Rate, 10f);
            Set(def, S.Magazine, 100f);
            Set(def, S.Range, 7f);
            Set(def, S.Area, 11f);
            Set(def, S.Status, 12f);
            Set(def, S.Drain, 20f);
            Set(def, S.Refill, 15f);
            Set(def, S.AltCooldown, 6f);
            Set(def, S.AltArea, 4.5f);
            Set(def, S.AltDuration, 5f);
            Set(def, S.AltDamage, 20f);
            Set(def, S.Knockback, 12f);
            Set(def, S.TempHealth, 1.5f);
            Set(def, S.Extra1, 3f);
            Set(def, S.Extra2, 24f);

            def.Levels = new[]
            {
                Level("weapon.flamer.l2", Plus(S.Range, 1.5f)),
                Level("weapon.flamer.l3", Pct(S.Status, 0.3f)),
                Level("weapon.flamer.l4", Pct(S.Area, 0.25f), Pct(S.Magazine, 0.4f)),
                Level("weapon.flamer.l5", Plus(S.AltDuration, 2f))
            };

            def.PairTome = TomeId.Might;
            def.Evolution = Level("weapon.flamer.evo", Pct(S.Status, 0.5f));
            def.Price = 500;
            def.Tint = new Color(1f, 0.45f, 0.15f);
        }

        // ------------------------------------------------------------------ 7. Дуга

        /// <summary>
        /// Тесла: захват ближайшего в конусе 12° на 14 юнитов, прыжки ещё на 4 цели в 5 юнитах
        /// (−15% за прыжок), 8 ударов в секунду по 16 HP, ток 0.25 с (Status). Батарея 100,
        /// −18 в секунду, +25 заряда. Громоотвод: до 2, 8 с, бьёт трёх в 7 юнитах раз в 0.5 с.
        /// Extra1 — радиус прыжка.
        /// </summary>
        private static void Tesla(WeaponDef def)
        {
            Set(def, S.Damage, 16f);
            Set(def, S.Rate, 8f);
            Set(def, S.Magazine, 100f);
            Set(def, S.Range, 14f);
            Set(def, S.Area, 12f);
            Set(def, S.Projectiles, 4f);
            Set(def, S.Status, 0.25f);
            Set(def, S.Drain, 18f);
            Set(def, S.Refill, 25f);
            Set(def, S.AltCharges, 2f);
            Set(def, S.AltDuration, 8f);
            Set(def, S.AltArea, 7f);
            Set(def, S.AltDamage, 16f);
            Set(def, S.AltCooldown, 1f);
            Set(def, S.TempHealth, 1f);
            Set(def, S.Extra1, 5f);

            def.Levels = new[]
            {
                Level("weapon.tesla.l2", Plus(S.Projectiles, 1f)),
                Level("weapon.tesla.l3", Pct(S.Damage, 0.2f)),
                Level("weapon.tesla.l4", Plus(S.Range, 3f), Pct(S.Magazine, 0.3f)),
                Level("weapon.tesla.l5", Plus(S.AltDuration, 4f))
            };

            def.PairTome = TomeId.Magnet;
            def.Evolution = Level("weapon.tesla.evo", Plus(S.Extra2, 1f));
            def.Price = 600;
            def.Tint = new Color(0.5f, 0.7f, 1f);
        }

        // ------------------------------------------------------------------ 8. Забой

        /// <summary>
        /// Кирка-молот: три взмаха по 0.38 с, дуга 110° (полуугол 55), досягаемость 3, урон
        /// 40/30/55, рассечение до 6 целей (−15% каждой следующей), оглушение 0.3 с, отброс 5,
        /// временное здоровье ×2.5 (1.5 HP за убийство). Удар оземь: замах 0.6 с, радиус 4.5
        /// (Extra1), 90 HP в центре, отброс 14, воронка. Пробой: откат 8 с, рывок на 4 юнита,
        /// прокапывает до 3 юнитов породы; первые 0.2 с — парирование.
        /// </summary>
        private static void Hammer(WeaponDef def)
        {
            Set(def, S.Damage, 40f);
            Set(def, S.Rate, 1f / 0.38f);
            Set(def, S.Range, 3f);
            Set(def, S.Area, 55f);
            Set(def, S.Pierce, 6f);
            Set(def, S.Knockback, 5f);
            Set(def, S.Status, 0.3f);
            Set(def, S.AltCooldown, 8f);
            Set(def, S.AltDamage, 90f);
            Set(def, S.AltArea, 4f);
            Set(def, S.TempHealth, 2.5f);

            // Extra1 — радиус удара оземь; Extra2 — длина трещины эволюции «Землетряс» (0 — нет).
            Set(def, S.Extra1, 4.5f);
            Set(def, S.Extra2, 0f);

            def.Levels = new[]
            {
                Level("weapon.hammer.l2", Pct(S.Damage, 0.2f), Pct(S.AltDamage, 0.2f)),
                Level("weapon.hammer.l3", Plus(S.Pierce, 2f)),
                Level("weapon.hammer.l4", Plus(S.Extra1, 1f), Plus(S.AltCooldown, -2f)),
                Level("weapon.hammer.l5", Pct(S.TempHealth, 0.5f))
            };

            def.PairTome = TomeId.Vitality;
            def.Evolution = Level("weapon.hammer.evo", Plus(S.Extra2, 8f));
            def.Price = 700;
            def.Tint = new Color(0.85f, 0.7f, 0.55f);
        }
    }
}
