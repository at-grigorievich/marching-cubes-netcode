using System;
using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>Аффиксы элиты (M3.3). Порядок — номер в окраске и в задании «убить N ядовитых».</summary>
    public enum EliteAffix
    {
        /// <summary>Спереди получает 40% урона — обходить или бить молотом (оглушение разворачивает).</summary>
        Armored,

        /// <summary>Аура 3 юнита, 6 HP/с.</summary>
        Poison,

        /// <summary>Раз в 6 с — пятеро мелких вокруг, с потолком, как выводок Матки.</summary>
        Brood,

        /// <summary>Рывок к игроку раз в 5 с.</summary>
        Leaper,

        /// <summary>Толпа в 10 юнитах быстрее на 20%.</summary>
        Leader,

        Count
    }

    /// <summary>
    /// Аффиксы элиты: окраска, ореол-подсказка и действие. Элита — особь толпы (ссылка
    /// <see cref="SpiderHandle"/>), и это остаётся: то, чему нужна своя логика (аура, рывок,
    /// кладка, вожак), ведётся отсюда с главного потока по ссылке — их единицы.
    ///
    /// Чистая логика выбора аффиксов — <see cref="Pick"/>: без повторов, своим генератором.
    /// </summary>
    public sealed class EliteAffixes
    {
        private const float PoisonRadius = 3f;
        private const float PoisonDps = 6f;
        private const float BroodEvery = 6f;
        private const int BroodCount = 5;

        /// <summary>Выводок не плодится сверх этого числа особей в 15 юнитах: без потолка кладка
        /// Матки за полминуты выращивала стену хитина (раздел про Матку).</summary>
        private const int BroodCap = 40;

        private const float LeapEvery = 5f;
        private const float LeapSpeed = 16f;
        private const float LeaderRadius = 10f;
        private const float LeaderHaste = 1.2f;

        /// <summary>Окраска элиты по главному аффиксу — читается издали, раньше любых чисел.</summary>
        public static readonly Color[] Hues =
        {
            new Color(0.75f, 0.85f, 1.15f), // панцирная — стальная
            new Color(0.6f, 1.35f, 0.5f),   // ядовитая — зелёная
            new Color(1.15f, 0.6f, 1.3f),   // кладка — лиловая
            new Color(1.45f, 0.85f, 0.4f),  // прыгучая — рыжая
            new Color(1.4f, 1.2f, 0.45f)    // вожак — золотая
        };

        private sealed class Tracked
        {
            public SpiderHandle Handle;
            public EliteAffix[] Affixes;
            public float Brood;
            public float Leap;
        }

        private readonly List<Tracked> _tracked = new List<Tracked>();

        /// <summary>Сколько раз сработал каждый аффикс — для прогона.</summary>
        public readonly int[] Fired = new int[(int)EliteAffix.Count];

        /// <summary>Аффиксы новой элиты: один на ярусе 1, два с яруса 3, без повторов.</summary>
        public static EliteAffix[] Pick(int tier, System.Random random)
        {
            var count = tier >= 3 ? 2 : 1;
            var pool = new List<EliteAffix>();
            for (var i = 0; i < (int)EliteAffix.Count; i++) pool.Add((EliteAffix)i);

            var picked = new EliteAffix[count];

            for (var i = 0; i < count; i++)
            {
                var index = random.Next(pool.Count);
                picked[i] = pool[index];
                pool.RemoveAt(index);
            }

            return picked;
        }

        public void Clear()
        {
            _tracked.Clear();
            Array.Clear(Fired, 0, Fired.Length);
        }

        /// <summary>Повесить аффиксы на только что заведённую элиту.</summary>
        public void Attach(SpiderCrowd crowd, SpiderHandle handle, EliteAffix[] affixes)
        {
            if (crowd == null || affixes == null || affixes.Length == 0) return;

            crowd.SetHue(handle, (Vector4)Hues[(int)affixes[0]]);

            foreach (var affix in affixes)
            {
                if (affix == EliteAffix.Armored) crowd.SetArmor(handle, 0.4f);
            }

            _tracked.Add(new Tracked { Handle = handle, Affixes = affixes, Brood = BroodEvery, Leap = LeapEvery });
        }

        /// <summary>Аффиксы элиты по ссылке — для объявления и прогона.</summary>
        public EliteAffix[] Of(SpiderHandle handle)
        {
            foreach (var tracked in _tracked)
            {
                if (tracked.Handle.Slot == handle.Slot && tracked.Handle.Serial == handle.Serial) return tracked.Affixes;
            }

            return null;
        }

        /// <summary>Шаг: аура, кладка, рывок, вожак. Мёртвых — забыть.</summary>
        public void Tick(SpiderCrowd crowd, IPlayerBody player, float deltaTime)
        {
            for (var i = _tracked.Count - 1; i >= 0; i--)
            {
                var tracked = _tracked[i];

                if (crowd == null || !crowd.TryGetSpider(tracked.Handle, out var position, out _))
                {
                    _tracked.RemoveAt(i);
                    continue;
                }

                foreach (var affix in tracked.Affixes)
                {
                    switch (affix)
                    {
                        case EliteAffix.Poison:
                            if (player != null && !player.IsDashing &&
                                Vector3.Distance(player.BodyCentre, position) < PoisonRadius + 0.8f)
                            {
                                player.Hurt(DamageHit.Over(PoisonDps * deltaTime, position, DamageKind.Poison));
                                Fired[(int)affix]++;
                            }

                            break;

                        case EliteAffix.Brood:
                            tracked.Brood -= deltaTime;

                            if (tracked.Brood <= 0f)
                            {
                                tracked.Brood = BroodEvery;

                                if (crowd.CountNear(position, 15f) < BroodCap && crowd.SpawnAround(position, BroodCount, 3, 1) > 0)
                                {
                                    Fired[(int)affix]++;
                                }
                            }

                            break;

                        case EliteAffix.Leaper:
                            tracked.Leap -= deltaTime;

                            if (tracked.Leap <= 0f && player != null)
                            {
                                var toward = player.BodyCentre - position;

                                if (toward.magnitude < 14f && toward.magnitude > 2.5f)
                                {
                                    tracked.Leap = LeapEvery;
                                    crowd.Lunge(tracked.Handle, toward.normalized * LeapSpeed);
                                    Fired[(int)affix]++;
                                }
                            }

                            break;

                        case EliteAffix.Leader:
                            // Раз в кадр — короткое ускорение: ушёл из радиуса — через треть секунды обычный.
                            if (crowd.Haste(position, LeaderRadius, LeaderHaste, 0.35f) > 0) Fired[(int)affix]++;
                            break;
                    }
                }
            }
        }
    }
}
