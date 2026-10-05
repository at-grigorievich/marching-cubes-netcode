namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Множители сложности сверх времени стадии: ярус вылазки, глубина, мягкий старт новичка.
    ///
    /// Ни один не зависит от мета-покупок (решение плана №5): иначе покупки ощущались бы
    /// бесполезными — купил броню, а орда стала кусать сильнее.
    /// </summary>
    public struct DifficultyScale
    {
        /// <summary>Живучесть особей, элиты и Матки.</summary>
        public float Health;

        /// <summary>Укусы и удары Матки.</summary>
        public float Bite;

        /// <summary>Население и досыл.</summary>
        public float Population;

        /// <summary>Хитин за убийства — награда за глубину.</summary>
        public float Chitin;

        public static DifficultyScale One => new DifficultyScale
        {
            Health = 1f,
            Bite = 1f,
            Population = 1f,
            Chitin = 1f
        };

        public static DifficultyScale operator *(DifficultyScale a, DifficultyScale b) => new DifficultyScale
        {
            Health = a.Health * b.Health,
            Bite = a.Bite * b.Bite,
            Population = a.Population * b.Population,
            Chitin = a.Chitin * b.Chitin
        };
    }
}
