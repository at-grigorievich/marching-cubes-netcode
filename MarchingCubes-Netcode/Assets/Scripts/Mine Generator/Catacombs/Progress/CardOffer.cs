using System;
using System.Collections.Generic;

namespace MineGenerator.Catacombs
{
    /// <summary>Что игроку доступно для карточек: открытые пушки и тома (арсенал M4), пары эволюций.</summary>
    public sealed class CardPool
    {
        public readonly List<WeaponId> Weapons = new List<WeaponId>();
        public readonly List<TomeId> Tomes = new List<TomeId>();

        /// <summary>Парный том пушки и его уровень для эволюции — из описания пушки.</summary>
        public Func<WeaponId, (TomeId Tome, int Level)> PairOf = id => (TomeId.Might, 3);

        /// <summary>Удача: прибавка к шансу четвёртой карточки.</summary>
        public float Luck;

        /// <summary>Сколько лечения и хитина у запасных карточек.</summary>
        public float HealAmount = 30f;
        public float ChitinAmount = 25f;

        public static CardPool Everything()
        {
            var pool = new CardPool();

            for (var i = 0; i < (int)WeaponId.Count; i++) pool.Weapons.Add((WeaponId)i);
            for (var i = 0; i < (int)TomeId.Count; i++) pool.Tomes.Add((TomeId)i);

            return pool;
        }
    }

    /// <summary>
    /// Карточки на уровне (план M1.7): три, при удаче четыре.
    ///
    /// Виды: новая пушка (есть свободный слот, пушка открыта), уровень пушки (до 5), новый том
    /// (свободный слот из 4), уровень тома, эволюция (пушка 5-го уровня + парный том ≥ 3 —
    /// выпадает гарантированно в первом же экране), запасная (всё забито — лечение или хитин).
    ///
    /// Улучшения того, что уже есть, вдвое чаще новых: иначе к десятому уровню у игрока три пушки
    /// первого уровня и ни одной сильной. Изгнанное не предлагается до конца забега.
    ///
    /// Чистая логика на своём генераторе случайных чисел: один и тот же забег с тем же сидом
    /// даёт те же карточки — так их проверяют тесты и бот ProgressionSim (M6).
    /// </summary>
    public static class CardOffer
    {
        /// <summary>Шанс четвёртой карточки без удачи.</summary>
        public const float FourthChance = 0.1f;

        private struct Candidate
        {
            public Card Card;
            public float Weight;
        }

        public static List<Card> Make(RunSession run, CardPool pool, Random random, List<Card> into = null)
        {
            into ??= new List<Card>(4);
            into.Clear();

            var candidates = new List<Candidate>();

            // Эволюция — гарантированно и первой: это награда за сборку, и пропустить её
            // из-за неудачного броска значило бы обесценить пару «пушка + том».
            foreach (var owned in run.Weapons)
            {
                if (owned.Evolved || owned.Level < WeaponDef.MaxLevel) continue;

                var (tome, level) = pool.PairOf(owned.Id);
                if (run.TomeLevel(tome) < level) continue;

                var evolution = new Card { Type = CardType.Evolution, Weapon = owned.Id, ToLevel = owned.Level };

                if (!run.OfferedEvolutions.Contains(owned.Id))
                {
                    into.Add(evolution);
                    run.OfferedEvolutions.Add(owned.Id);
                }
                else
                {
                    candidates.Add(new Candidate { Card = evolution, Weight = 3f });
                }
            }

            foreach (var owned in run.Weapons)
            {
                if (owned.Level < WeaponDef.MaxLevel)
                {
                    candidates.Add(new Candidate
                    {
                        Card = new Card { Type = CardType.WeaponLevel, Weapon = owned.Id, ToLevel = owned.Level + 1 },
                        Weight = 2f
                    });
                }
            }

            if (run.Weapons.Count < RunSession.WeaponSlots)
            {
                foreach (var id in pool.Weapons)
                {
                    if (run.WeaponLevel(id) > 0 || run.BanishedWeapons.Contains(id)) continue;

                    candidates.Add(new Candidate { Card = new Card { Type = CardType.NewWeapon, Weapon = id, ToLevel = 1 }, Weight = 1f });
                }
            }

            foreach (var owned in run.Tomes)
            {
                if (owned.Level < Tomes.MaxLevel)
                {
                    candidates.Add(new Candidate
                    {
                        Card = new Card { Type = CardType.TomeLevel, Tome = owned.Id, ToLevel = owned.Level + 1 },
                        Weight = 2f
                    });
                }
            }

            if (run.Tomes.Count < Tomes.Slots)
            {
                foreach (var id in pool.Tomes)
                {
                    if (run.TomeLevel(id) > 0 || run.BanishedTomes.Contains(id)) continue;

                    candidates.Add(new Candidate { Card = new Card { Type = CardType.NewTome, Tome = id, ToLevel = 1 }, Weight = 1f });
                }
            }

            var count = random.NextDouble() < FourthChance + pool.Luck ? 4 : 3;

            while (into.Count < count && candidates.Count > 0)
            {
                var total = 0f;
                foreach (var candidate in candidates) total += candidate.Weight;

                var roll = (float)random.NextDouble() * total;
                var pick = candidates.Count - 1;

                for (var i = 0; i < candidates.Count; i++)
                {
                    roll -= candidates[i].Weight;
                    if (roll > 0f) continue;

                    pick = i;
                    break;
                }

                into.Add(candidates[pick].Card);
                candidates.RemoveAt(pick);
            }

            // Всё забито — запасные: лечение и хитин. Хотя бы одна карточка есть всегда.
            if (into.Count < count)
            {
                into.Add(new Card { Type = CardType.Heal, Amount = pool.HealAmount });
            }

            if (into.Count < count)
            {
                into.Add(new Card { Type = CardType.Chitin, Amount = pool.ChitinAmount });
            }

            return into;
        }

        /// <summary>
        /// Карточки кокона с элиты: гарантированная эволюция, если доступна, иначе улучшение пушки.
        /// Одна карточка — без выбора, как сундук VS, только без пятисекундной анимации.
        /// </summary>
        public static Card Cocoon(RunSession run, CardPool pool, Random random)
        {
            foreach (var owned in run.Weapons)
            {
                if (owned.Evolved || owned.Level < WeaponDef.MaxLevel) continue;

                var (tome, level) = pool.PairOf(owned.Id);
                if (run.TomeLevel(tome) >= level) return new Card { Type = CardType.Evolution, Weapon = owned.Id, ToLevel = owned.Level };
            }

            var upgradable = new List<OwnedWeapon>();
            foreach (var owned in run.Weapons) if (owned.Level < WeaponDef.MaxLevel) upgradable.Add(owned);

            if (upgradable.Count > 0)
            {
                var pick = upgradable[random.Next(upgradable.Count)];
                return new Card { Type = CardType.WeaponLevel, Weapon = pick.Id, ToLevel = pick.Level + 1 };
            }

            return new Card { Type = CardType.Chitin, Amount = pool.ChitinAmount * 2f };
        }
    }
}
