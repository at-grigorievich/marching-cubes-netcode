using UnityEngine;

namespace MineGenerator.Catacombs
{
    public enum LevelLinkMode
    {
        /// <summary>Серпантин с площадками на разворотах — компактно и всегда проходимо.</summary>
        Stairwell = 0,
        /// <summary>Один длинный наклонный ход, если между залами хватает места.</summary>
        StraightRamp = 1
    }

    public enum ColliderMode
    {
        None = 0,
        All = 1
    }

    /// <summary>
    /// Все параметры генерации катакомб. Один ассет — один «биом»/этап игры.
    /// </summary>
    [CreateAssetMenu(fileName = "Catacomb Settings", menuName = "Mine Generator/Catacomb Settings", order = 1)]
    public sealed class CatacombSettings : ScriptableObject
    {
        /// <summary>
        /// Версия набора значений. Ассет переживает обновления генератора, и в нём остаются
        /// поля от прошлой версии — так уровень и оставался размытым, хотя примитивы уже
        /// стали коробками. Ноль означает «настройки старее текущего генератора».
        /// </summary>
        public const int CurrentPresetVersion = 4;

        [SerializeField, HideInInspector] private int presetVersion;

        public int PresetVersion => presetVersion;
        public bool IsPresetOutdated => presetVersion < CurrentPresetVersion;

        [Header("Чанк")]
        [Tooltip("Количество ячеек по стороне чанка. Плотностей хранится (Resolution + 3)^3.")]
        [SerializeField, Range(4, 64)] private int chunkResolution = 16;

        [Tooltip("Размер одной ячейки в юнитах. Предел детализации поверхности.")]
        [SerializeField, Min(0.05f)] private float voxelSize = 1f;

        [Header("Мир")]
        [SerializeField] private Vector3Int worldSizeInChunks = new Vector3Int(6, 3, 6);

        [Tooltip("Толщина сплошной породы по краю мира, в юнитах. Запечатывает уровень.")]
        [SerializeField, Min(0f)] private float boundaryThickness = 3f;

        [Tooltip("Шаг планировочной сетки. Залы и коридоры выравниваются по нему — от этого " +
                 "уровень читается как спроектированный, а не как случайные ходы.")]
        [SerializeField, Min(1f)] private float gridStep = 4f;

        [Header("Случайность")]
        [SerializeField] private int seed = 12345;
        [SerializeField] private bool randomizeSeed;

        [Header("Уровни")]
        [SerializeField, Range(1, 8)] private int levelCount = 3;
        [Tooltip("Узлов-развилок на этаже. Чем больше, тем гуще сеть ходов.")]
        [SerializeField] private Vector2Int roomsPerLevel = new Vector2Int(10, 14);

        [Header("Узлы развилок")]
        [Tooltip("Сторона узла в юнитах. Это не зал, а перекрёсток: места ровно столько, " +
                 "чтобы обкружить толпу, но не больше.")]
        [SerializeField] private Vector2 roomSize = new Vector2(6f, 10f);
        [SerializeField, Min(2f)] private float roomHeight = 4.5f;

        [Tooltip("Сколько узлов на этаже расширить до арены. 0 — сплошная теснота. " +
                 "Поднимите до 1, если кайтить окажется негде.")]
        [SerializeField, Range(0, 4)] private int arenasPerLevel;
        [Tooltip("Во сколько раз арена больше обычного узла.")]
        [SerializeField, Min(1f)] private float arenaScale = 2.2f;

        [Header("Коридоры")]
        [SerializeField, Min(1f)] private float corridorWidth = 3.5f;
        [SerializeField, Min(2f)] private float corridorHeight = 3.6f;
        [Tooltip("Доля дополнительных связей сверх остовного дерева. Даёт кольца для кайтинга.")]
        [SerializeField, Range(0f, 1f)] private float extraConnections = 0.6f;
        [Tooltip("Минимум ходов из каждого узла. 2 и выше означает отсутствие тупиков: " +
                 "у остовного дерева листья это и есть тупики.")]
        [SerializeField, Range(1, 4)] private int minConnections = 2;

        [Header("Стрелковые галереи")]
        [Tooltip("Доля длинных связей, которые строятся прямым простреливаемым прогоном " +
                 "без колен и без сужений. Это те коридоры, по которым отступают с гранатомётом.")]
        [SerializeField, Range(0f, 1f)] private float galleryShare = 0.35f;

        [Tooltip("Короче этого связь галереей не станет: смысл галереи в дистанции.")]
        [SerializeField, Min(8f)] private float galleryMinLength = 20f;

        [Tooltip("Во сколько раз галерея шире обычного хода.")]
        [SerializeField, Range(1f, 2.5f)] private float galleryWidthScale = 1.35f;

        [Tooltip("Во сколько раз галерея выше обычного хода. Гранате нужна дуга.")]
        [SerializeField, Range(1f, 2.5f)] private float galleryHeightScale = 1.3f;

        [Header("Хаос планировки")]
        [Tooltip("Насколько ход виляет между узлами. 0 — строгая буква Г из двух пролётов, " +
                 "1 — лесенка из нескольких колен с обходами в сторону.")]
        [SerializeField, Range(0f, 1f)] private float pathChaos = 0.6f;

        [Tooltip("Доля коридоров, у которых в середине ход сужается до щели.")]
        [SerializeField, Range(0f, 1f)] private float pinchChance = 0.3f;

        [Tooltip("Во сколько раз щель уже обычного хода. Снизу ограничено проходимостью: " +
                 "щель уже двух юнитов шум стен может закрыть совсем.")]
        [SerializeField, Range(0.2f, 1f)] private float pinchWidth = 0.55f;

        [Tooltip("Разброс высоты пола внутри одного этажа, в юнитах. 0 — этаж идеально плоский, " +
                 "как было. Уклон ходов при этом всё равно ограничен уклоном пандуса.")]
        [SerializeField, Min(0f)] private float floorWave = 6f;

        [Tooltip("Масштаб волн пола. Меньше значение — длиннее волна, плавнее перепады.")]
        [SerializeField, Min(0.001f)] private float floorWaveScale = 0.02f;

        [Header("Форма")]
        [Tooltip("Радиус скругления рёбер. 0 — острые углы, половина ширины коридора — труба.")]
        [SerializeField, Min(0f)] private float cornerRounding = 0.9f;
        [Tooltip("Сглаживание на стыках примитивов. Держите небольшим, иначе углы поплывут.")]
        [SerializeField, Min(0f)] private float junctionBlend = 0.6f;

        [Header("Связь между уровнями")]
        [SerializeField] private LevelLinkMode levelLinkMode = LevelLinkMode.Stairwell;
        [SerializeField, Range(1, 6)] private int linksBetweenLevels = 2;
        [Tooltip("Длина одного пролёта серпантина в юнитах.")]
        [SerializeField, Min(4f)] private float rampRun = 16f;
        [Tooltip("Уклон пандуса: 0.5 — примерно 27 градусов. Выше 1 игрок уже не поднимется.")]
        [SerializeField, Range(0.1f, 1f)] private float rampSlope = 0.5f;

        [Header("Шум стен")]
        [SerializeField, Min(0.001f)] private float noiseScale = 0.12f;
        [Tooltip("0 — идеально ровные стены. Небольшие значения дают фактуру камня, " +
                 "не разрушая прямоугольную форму.")]
        [SerializeField, Min(0f)] private float noiseAmplitude = 0.35f;
        [SerializeField, Range(1, 4)] private int noiseOctaves = 2;

        [Header("Меш")]
        [SerializeField, Range(0.05f, 0.95f)] private float isoLevel = 0.5f;
        [Tooltip("Ширина переходной зоны стены в юнитах. Меньше — резче край.")]
        [SerializeField, Min(0.05f)] private float surfaceSoftness = 0.7f;
        [SerializeField] private Material material;
        [SerializeField] private ColliderMode colliderMode = ColliderMode.All;

        [Header("Бюджет генерации")]
        [Tooltip("Сколько миллисекунд в кадре разрешено тратить на генерацию. Для WebGL 4-8.")]
        [SerializeField, Range(1f, 100f)] private float millisecondsPerFrame = 8f;

        public int ChunkResolution => chunkResolution;
        public float VoxelSize => voxelSize;

        /// <summary>Сэмплов плотности по стороне чанка: ячейки + общий угол + кольцо запаса для нормалей.</summary>
        public int SampleDim => chunkResolution + 3;
        public const int SamplePadding = 1;

        public int SampleCount => SampleDim * SampleDim * SampleDim;

        /// <summary>Размер чанка в юнитах. Чанки стыкуются ровно по этой сетке.</summary>
        public float ChunkWorldSize => chunkResolution * voxelSize;

        public Vector3Int WorldSizeInChunks => worldSizeInChunks;
        public int ChunkCount => worldSizeInChunks.x * worldSizeInChunks.y * worldSizeInChunks.z;

        public Vector3 WorldSize => new Vector3(
            worldSizeInChunks.x * ChunkWorldSize,
            worldSizeInChunks.y * ChunkWorldSize,
            worldSizeInChunks.z * ChunkWorldSize);

        public float BoundaryThickness => boundaryThickness;
        public float GridStep => gridStep;

        public int Seed => seed;
        public bool RandomizeSeed => randomizeSeed;

        public int LevelCount => levelCount;
        public Vector2Int RoomsPerLevel => roomsPerLevel;

        public Vector2 RoomSize => roomSize;
        public float RoomHeight => roomHeight;

        public int ArenasPerLevel => arenasPerLevel;
        public float ArenaScale => arenaScale;

        public float CorridorWidth => corridorWidth;
        public float CorridorHeight => corridorHeight;
        public float ExtraConnections => extraConnections;
        public int MinConnections => minConnections;

        public float CornerRounding => cornerRounding;
        public float JunctionBlend => junctionBlend;

        public float GalleryShare => galleryShare;
        public float GalleryMinLength => galleryMinLength;
        public float GalleryWidthScale => galleryWidthScale;
        public float GalleryHeightScale => galleryHeightScale;

        public float PathChaos => pathChaos;
        public float PinchChance => pinchChance;
        public float PinchWidth => pinchWidth;
        public float FloorWave => floorWave;
        public float FloorWaveScale => floorWaveScale;

        public LevelLinkMode LevelLinkMode => levelLinkMode;
        public int LinksBetweenLevels => linksBetweenLevels;
        public float RampRun => rampRun;
        public float RampSlope => rampSlope;

        public float NoiseScale => noiseScale;
        public float NoiseAmplitude => noiseAmplitude;
        public int NoiseOctaves => noiseOctaves;

        public float IsoLevel => isoLevel;
        public float SurfaceSoftness => surfaceSoftness;
        public Material Material => material;
        public ColliderMode ColliderMode => colliderMode;

        public float MillisecondsPerFrame => millisecondsPerFrame;

        /// <summary>Насколько далеко примитив может «дотянуться» за свои габариты из-за шума и мягкости края.</summary>
        public float PrimitiveMargin => noiseAmplitude + surfaceSoftness + junctionBlend;

        /// <summary>Память под поля плотности всех чанков, в байтах.</summary>
        public long DensityMemoryBytes => (long)SampleCount * ChunkCount * sizeof(float);

        public void ApplyRuntimeSeed(int value) => seed = value;

        private void OnValidate()
        {
            worldSizeInChunks = new Vector3Int(
                Mathf.Max(1, worldSizeInChunks.x),
                Mathf.Max(1, worldSizeInChunks.y),
                Mathf.Max(1, worldSizeInChunks.z));

            roomsPerLevel = new Vector2Int(
                Mathf.Max(1, roomsPerLevel.x),
                Mathf.Max(roomsPerLevel.x, roomsPerLevel.y));

            roomSize = new Vector2(
                Mathf.Max(gridStep, roomSize.x),
                Mathf.Max(roomSize.x, roomSize.y));

            // Скругление больше половины габарита превращает коробку в капсулу.
            cornerRounding = Mathf.Min(cornerRounding, Mathf.Min(corridorWidth, corridorHeight) * 0.5f);
        }
    }
}
