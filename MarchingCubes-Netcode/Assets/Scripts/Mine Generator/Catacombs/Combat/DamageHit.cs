using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>Чем ранен игрок — для дуг направления, звука и разбора смертей в аналитике.</summary>
    public enum DamageKind : byte
    {
        Other = 0,

        /// <summary>Укусы толпы, сложенные за кадр.</summary>
        Bite = 1,

        /// <summary>Ударная волна Матки.</summary>
        Shock = 2,

        /// <summary>Паутинный плевок Матки.</summary>
        Spit = 3,

        /// <summary>Кислота: плевун, лужи, облако бомбардира (M3).</summary>
        Acid = 4,

        /// <summary>Яд элиты, прижим ловчего — урон со временем (M3).</summary>
        Poison = 5,

        /// <summary>Свой взрыв у самых ног.</summary>
        Self = 6
    }

    /// <summary>
    /// Удар по игроку. Структура, а не набор параметров: по пути к здоровью удар проходит
    /// броню, потолок урона в секунду и временное здоровье, и каждый шаг хочет знать, откуда
    /// он пришёл (дуги направления на экране, разбор смертей в аналитике).
    /// </summary>
    public struct DamageHit
    {
        /// <summary>Урон в HP — до брони и потолка.</summary>
        public float Amount;

        /// <summary>
        /// Откуда пришёл удар, в мире. Для укусов толпы — сумма направлений «от паука к игроку»,
        /// отложенная от игрока: так дуга на экране показывает, с какой стороны грызут сильнее.
        /// </summary>
        public Vector3 From;

        public DamageKind Kind;

        /// <summary>Сколько укусов сложено в этот удар (для укусов), иначе 1.</summary>
        public int Count;

        /// <summary>Номер вида паука, укусившего сильнее всех, или -1 — для разбора смертей.</summary>
        public int SpiderKind;

        /// <summary>Броня вычитается из каждого укуса по отдельности, а не из суммы за кадр.</summary>
        public bool PerBiteArmor;

        public static DamageHit Simple(float amount, Vector3 from, DamageKind kind) => new DamageHit
        {
            Amount = amount,
            From = from,
            Kind = kind,
            Count = 1,
            SpiderKind = -1
        };
    }
}
