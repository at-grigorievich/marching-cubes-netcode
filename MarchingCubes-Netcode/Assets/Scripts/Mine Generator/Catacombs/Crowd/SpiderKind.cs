using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Какой клип играет особь. Порядок фиксирован: он же индекс в
    /// <see cref="SpiderKind.Clips"/>, и джобы кладут в состояние паука именно его.
    ///
    /// Три штуки, а не шесть, которые есть в паке. Idle выброшен намеренно: в игре про
    /// орду паук либо бежит на игрока, либо бьёт, либо мёртв, а стоящий на месте паук —
    /// это просто медленно бегущий. Каждый лишний клип это лишние строки в текстуре
    /// анимации, то есть лишние килобайты в загрузке WebGL, и платить за стояние
    /// на месте не за что. Второй удар (attack_2) и второе стояние (idle_2) выброшены
    /// по той же причине.
    /// </summary>
    public enum SpiderClip
    {
        Walk = 0,
        Attack = 1,
        Dead = 2
    }

    /// <summary>Где в текстуре анимации лежит один клип.</summary>
    [System.Serializable]
    public struct SpiderClipRange
    {
        public string Name;

        /// <summary>Первая строка клипа в текстуре.</summary>
        public int StartRow;

        public int FrameCount;

        /// <summary>Длительность клипа в секундах на исходной скорости.</summary>
        public float Length;

        /// <summary>
        /// Кольцевой ли клип. Влияет на то, как шейдер выбирает второй кадр для смешения:
        /// у кольцевого последний смыкается с нулевым, у одноразового упирается в конец.
        /// </summary>
        public bool Loop;
    }

    /// <summary>
    /// Один вид паука, подготовленный к отрисовке толпой: меш, материал и запечённая
    /// в текстуру анимация.
    ///
    /// Создаётся не руками, а пунктом меню «Пауки: запечь анимацию в текстуры»
    /// (см. SpiderVatBaker). Руками здесь правятся только числа поведения внизу —
    /// скорость, радиус, урон; всё остальное перезаписывается при следующей запечке.
    /// </summary>
    [CreateAssetMenu(fileName = "Spider Kind", menuName = "Mine Generator/Spider Kind", order = 2)]
    public sealed class SpiderKind : ScriptableObject
    {
        /// <summary>
        /// Версия набора чисел поведения. Тот же приём, что у <see cref="CatacombSettings"/>:
        /// ассет переживает правку умолчаний в коде, и без версии повторная запечка
        /// не имеет права их трогать (вдруг их подбирали руками), а с версией знает,
        /// что её значения устарели, и пересчитывает.
        /// </summary>
        public const int CurrentTuningVersion = 3;

        [SerializeField, HideInInspector] private int tuningVersion;

        public bool IsTuningOutdated => tuningVersion < CurrentTuningVersion;

        public void MarkTuned() => tuningVersion = CurrentTuningVersion;

        [Header("Запечённое (правит SpiderVatBaker)")]
        [Tooltip("Меш из префаба плюс UV1 со столбцом вершины в текстуре анимации.")]
        public Mesh Mesh;

        [Tooltip("Материал на шейдере Mine Generator/Cave Crowd.")]
        public Material Material;

        [Tooltip("Позиции вершин по кадрам. Ширина — вершины, высота — кадры всех клипов подряд.")]
        public Texture2D Positions;

        [Tooltip("Нормали вершин по кадрам, той же раскладки.")]
        public Texture2D Normals;

        [Tooltip("Клипы в порядке SpiderClip.")]
        public SpiderClipRange[] Clips;

        [Tooltip("Габариты меша в покое — по ним считается радиус тела и высота посадки.")]
        public Bounds RestBounds;

        [Header("Поведение")]
        /// <summary>
        /// Во сколько раз особь крупнее исходного меша.
        ///
        /// Разный у разных видов намеренно. В натуральную величину паук из пака около
        /// юнита поперёк, а коридор — три с половиной: в кадре это читается как мышь,
        /// а не как угроза. Но и единый крупный размер плох: при поперечнике в три юнита
        /// одна особь перекрывает ход целиком, и толпа вырождается в очередь по одному.
        /// Разброс по видам даёт и крупных, и мелких — крупные читаются, мелкие заполняют.
        /// </summary>
        [Tooltip("Во сколько раз особь крупнее исходного меша.")]
        [Min(0.01f)] public float Scale = 1f;

        /// <summary>
        /// На сколько центр особи отстоит от поверхности камня.
        ///
        /// Мал потому, что начало координат у мешей пака стоит в лапах, а не в теле:
        /// точка привязки и есть точка касания. Ненулевой, чтобы лапы не тонули в породе
        /// на выпуклостях, где поверхность уходит из-под них.
        /// </summary>
        [Tooltip("На сколько приподнять особь над камнем, в единицах меша.")]
        [Min(0f)] public float Hover = 0.06f;

        /// <summary>
        /// Смотрит ли модель в минус Z. У пака Spiders — да.
        ///
        /// Проверено съёмкой сверху, а не на глаз по инспектору: паук ставился в ноль
        /// с палочкой-указателем вдоль +Z, и палочка упиралась в БРЮШКО. Голова
        /// и хелицеры смотрят в противоположную сторону. Разворот делается здесь,
        /// а не правкой меша при запечке: меш — это данные пака, и трогать его
        /// ради соглашения об осях значит прятать факт, который потом придётся
        /// выяснять заново для следующего пака.
        /// </summary>
        [Tooltip("Модель смотрит в минус Z (у пака Spiders — да, проверено съёмкой).")]
        public bool FacesMinusZ = true;

        [Tooltip("Разброс размера между особями. 0.2 означает от 0.8 до 1.2 от Scale.")]
        [Range(0f, 0.6f)] public float ScaleJitter = 0.18f;

        [Tooltip("Скорость бега в юнитах в секунду.")]
        [Min(0.1f)] public float MoveSpeed = 3.2f;

        [Tooltip("Разброс скорости между особями. Без него толпа идёт стеной и не растягивается.")]
        [Range(0f, 0.6f)] public float SpeedJitter = 0.25f;

        [Tooltip("Насколько быстро особь доворачивает на цель, градусов в секунду.")]
        [Min(30f)] public float TurnSpeed = 540f;

        [Tooltip("Радиус тела для расталкивания, юниты. Меньше половины ширины меша: " +
                 "пауки должны наползать друг на друга, а не выстраиваться решёткой.")]
        [Min(0.05f)] public float BodyRadius = 0.35f;

        [Tooltip("С какого расстояния до игрока особь бьёт.")]
        [Min(0.2f)] public float AttackRange = 1.1f;

        [Tooltip("Сколько юнитов проходит особь за один цикл бега. От этого зависит, " +
                 "не скользят ли лапы: фаза анимации ведётся пройденным путём, а не временем.")]
        [Min(0.05f)] public float StrideLength = 0.9f;

        [Tooltip("Сколько попаданий держит особь.")]
        [Min(1)] public int Health = 1;

        [Tooltip("Сколько секунд труп лежит после конца клипа смерти, прежде чем место " +
                 "освободится под новую особь.")]
        [Min(0f)] public float CorpseLinger = 2.5f;

        public bool IsReady =>
            Mesh != null && Material != null && Positions != null && Normals != null &&
            Clips != null && Clips.Length >= 3;

        public SpiderClipRange GetClip(SpiderClip clip)
        {
            var index = (int)clip;

            if (Clips == null || index < 0 || index >= Clips.Length) return default;

            return Clips[index];
        }
    }
}
