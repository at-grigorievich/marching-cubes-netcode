using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Три Матки на трёх ярусах вылазки (M3.5). Общая основа — <see cref="CaveQueen"/>: щиты на
    /// 2/3 и 1/3, генераторы, лечение за время, рост живучести от момента пробуждения. Профиль
    /// добавляет масштаб, окраску, живучесть и атаки сверх базовых.
    ///
    /// Каталог в коде, а не ассеты: как у пушек, источник правды — код, а ассет, который всё равно
    /// переписывает «Починить свет», ничего бы не дал, кроме ещё одного места для расхождения
    /// (грабли №8). Выбор — по ярусу вылазки (<see cref="RunSession.Tier"/>, M4).
    /// </summary>
    public sealed class QueenProfile
    {
        public int Tier;

        /// <summary>Ключ имени — для объявления и полосы босса.</summary>
        public string NameKey;

        /// <summary>
        /// Во сколько раз крупнее самого крупного вида толпы. Зал логова высотой 9.5 (M3.4) это
        /// позволяет; прежние 1.6 стояли, пока логово было обычным узлом высотой 4.5.
        /// </summary>
        public float SizeScale;

        public Color Hue;

        /// <summary>Множитель живучести сверх роста за время стадии.</summary>
        public float HealthScale;

        /// <summary>Лужи кислоты по залу — зоны урона на полу.</summary>
        public bool AcidPools;

        /// <summary>Выводок из бомбардиров (M3.2): их облако бьёт и по свите Матки.</summary>
        public bool BomberBrood;

        /// <summary>Бросок через зал раз в 12 с с замахом.</summary>
        public bool Charge;

        /// <summary>Дождь со свода: засадники падают вокруг игрока.</summary>
        public bool CeilingRain;

        /// <summary>Третья фаза: гаснут все светильники и генераторы — бой при вспышках и выстрелах.</summary>
        public bool LightsOut;

        private static readonly QueenProfile Weaver = new QueenProfile
        {
            Tier = 1,
            NameKey = "queen.weaver",
            SizeScale = 2.4f,
            Hue = new Color(0.8f, 0.55f, 0.5f),
            HealthScale = 1f
        };

        private static readonly QueenProfile Acid = new QueenProfile
        {
            Tier = 2,
            NameKey = "queen.acid",
            SizeScale = 2.7f,
            Hue = new Color(0.7f, 1.05f, 0.45f),
            HealthScale = 1.6f,
            AcidPools = true,
            BomberBrood = true,
            Charge = true
        };

        private static readonly QueenProfile Primordial = new QueenProfile
        {
            Tier = 3,
            NameKey = "queen.primordial",
            SizeScale = 3f,
            Hue = new Color(1.15f, 0.42f, 0.48f),
            HealthScale = 2.4f,
            AcidPools = true,
            BomberBrood = true,
            Charge = true,
            CeilingRain = true,
            LightsOut = true
        };

        public static QueenProfile ForTier(int tier) => tier >= 3 ? Primordial : tier == 2 ? Acid : Weaver;
    }
}
