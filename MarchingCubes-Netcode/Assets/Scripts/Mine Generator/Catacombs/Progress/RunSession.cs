using System;
using System.Collections.Generic;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Кривая опыта (план M1.7): до следующего уровня round(6 + 5·L + 0.6·L²) —
    /// L1: 12, L5: 46, L10: 116, L20: 346, L30: 696. Цель, по которой подбирается
    /// (ProgressionSim, M6): уровень каждые 15–25 с на первой минуте, 40–60 с к концу третьего яруса.
    /// </summary>
    public static class XpCurve
    {
        public static float Need(int level) => (float)Math.Round(6.0 + 5.0 * level + 0.6 * level * level);
    }

    /// <summary>Пушка в забеге: уровень и эволюция.</summary>
    [Serializable]
    public struct OwnedWeapon
    {
        public WeaponId Id;
        public int Level;
        public bool Evolved;
    }

    /// <summary>Том в забеге.</summary>
    [Serializable]
    public struct OwnedTome
    {
        public TomeId Id;
        public int Level;
    }

    /// <summary>Тип карточки на уровне.</summary>
    public enum CardType
    {
        NewWeapon,
        WeaponLevel,
        NewTome,
        TomeLevel,
        Evolution,

        /// <summary>Запасная, когда всё забито: лечение.</summary>
        Heal,

        /// <summary>Запасная: хитин.</summary>
        Chitin
    }

    /// <summary>Карточка выбора.</summary>
    public struct Card : IEquatable<Card>
    {
        public CardType Type;
        public WeaponId Weapon;
        public TomeId Tome;

        /// <summary>Каким станет уровень пушки или тома.</summary>
        public int ToLevel;

        /// <summary>Сколько лечения или хитина у запасной.</summary>
        public float Amount;

        public bool Equals(Card other) =>
            Type == other.Type && Weapon == other.Weapon && Tome == other.Tome && ToLevel == other.ToLevel;

        public override bool Equals(object obj) => obj is Card other && Equals(other);

        public override int GetHashCode() => ((int)Type * 397) ^ ((int)Weapon * 31) ^ (int)Tome;

        public bool IsWeapon => Type == CardType.NewWeapon || Type == CardType.WeaponLevel || Type == CardType.Evolution;

        public bool IsTome => Type == CardType.NewTome || Type == CardType.TomeLevel;
    }

    /// <summary>
    /// Состояние забега — чистые данные: уровень, опыт, пушки, тома, валюта, счётчики.
    /// Сериализуемо (JsonUtility, без словарей) — это и есть снимок контрольной точки (M4).
    /// </summary>
    [Serializable]
    public sealed class RunSession
    {
        public const int WeaponSlots = 3;

        public int Level = 1;
        public float Xp;
        public float Chitin;

        public int Kills;

        /// <summary>Убито особых пауков (M3) — для итогов и заданий (M4).</summary>
        public int SpecialKills;
        public int EliteKills;
        public int QueensKilled;
        public float Seconds;

        /// <summary>Ярус вылазки (1..3) и глубина (1..) — M4.</summary>
        public int Tier = 1;
        public int Depth = 1;

        public List<OwnedWeapon> Weapons = new List<OwnedWeapon>();
        public List<OwnedTome> Tomes = new List<OwnedTome>();

        /// <summary>Убийства по пушкам: индекс — WeaponId.</summary>
        public int[] KillsByWeapon = new int[(int)WeaponId.Count];

        public int Rerolls;
        public int Skips;
        public int Banishes;

        public List<WeaponId> BanishedWeapons = new List<WeaponId>();
        public List<TomeId> BanishedTomes = new List<TomeId>();

        /// <summary>Уровней, ещё не разобранных экраном карточек.</summary>
        public int PendingLevels;

        /// <summary>Эволюции, которые уже предлагались и были пропущены — повторно не навязываются первыми.</summary>
        public List<WeaponId> OfferedEvolutions = new List<WeaponId>();

        public float XpToNext => XpCurve.Need(Level);

        public float XpFraction => XpToNext > 0f ? Math.Min(1f, Xp / XpToNext) : 0f;

        /// <summary>Добавить опыт. Возвращает, сколько уровней набрано (встали в очередь).</summary>
        public int AddXp(float amount)
        {
            if (amount <= 0f) return 0;

            Xp += amount;

            var levels = 0;

            while (Xp >= XpToNext)
            {
                Xp -= XpToNext;
                Level++;
                levels++;
            }

            PendingLevels += levels;
            return levels;
        }

        public int WeaponLevel(WeaponId id)
        {
            foreach (var weapon in Weapons) if (weapon.Id == id) return weapon.Level;
            return 0;
        }

        public bool IsEvolved(WeaponId id)
        {
            foreach (var weapon in Weapons) if (weapon.Id == id) return weapon.Evolved;
            return false;
        }

        public int TomeLevel(TomeId id)
        {
            foreach (var tome in Tomes) if (tome.Id == id) return tome.Level;
            return 0;
        }

        /// <summary>Применить карточку к данным забега (пушки и тома; лечение и хитин — у контроллера).</summary>
        public void Apply(in Card card)
        {
            switch (card.Type)
            {
                case CardType.NewWeapon:
                    if (WeaponLevel(card.Weapon) == 0 && Weapons.Count < WeaponSlots)
                    {
                        Weapons.Add(new OwnedWeapon { Id = card.Weapon, Level = 1 });
                    }

                    break;

                case CardType.WeaponLevel:
                    for (var i = 0; i < Weapons.Count; i++)
                    {
                        if (Weapons[i].Id != card.Weapon) continue;

                        var weapon = Weapons[i];
                        weapon.Level = Math.Min(WeaponDef.MaxLevel, weapon.Level + 1);
                        Weapons[i] = weapon;
                    }

                    break;

                case CardType.Evolution:
                    for (var i = 0; i < Weapons.Count; i++)
                    {
                        if (Weapons[i].Id != card.Weapon) continue;

                        var weapon = Weapons[i];
                        weapon.Evolved = true;
                        Weapons[i] = weapon;
                    }

                    break;

                case CardType.NewTome:
                    if (TomeLevel(card.Tome) == 0 && Tomes.Count < Catacombs.Tomes.Slots)
                    {
                        Tomes.Add(new OwnedTome { Id = card.Tome, Level = 1 });
                    }

                    break;

                case CardType.TomeLevel:
                    for (var i = 0; i < Tomes.Count; i++)
                    {
                        if (Tomes[i].Id != card.Tome) continue;

                        var tome = Tomes[i];
                        tome.Level = Math.Min(Catacombs.Tomes.MaxLevel, tome.Level + 1);
                        Tomes[i] = tome;
                    }

                    break;

                case CardType.Chitin:
                    Chitin += card.Amount;
                    break;
            }
        }

        /// <summary>Вклад томов забега в статы.</summary>
        public void ApplyTomes(PlayerStats stats)
        {
            foreach (var tome in Tomes) Catacombs.Tomes.Apply(tome.Id, tome.Level, stats);
        }
    }
}
