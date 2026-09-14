using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Считает поле плотности одного чанка из SDF планировки.
    ///
    /// Плотность &gt; IsoLevel — пустота, &lt; IsoLevel — порода.
    /// В <see cref="Boxes"/> приходят только те примитивы, чей bounds пересекает чанк:
    /// в первой версии каждая точка сетки перебирала все точки всех кривых, и время
    /// генерации росло как O(точки * примитивы) на весь мир сразу.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct CatacombDensityJob : IJobParallelFor
    {
        [WriteOnly] public NativeArray<float> Density;

        [ReadOnly] public NativeArray<CaveBox> Boxes;

        /// <summary>Позиция угла ячейки (0,0,0) чанка в координатах мира генерации.</summary>
        public float3 ChunkOrigin;
        public float VoxelSize;
        public int Dim;
        public int Pad;

        public float3 WorldSize;
        public float BoundaryThickness;

        public float NoiseScale;
        public float NoiseAmplitude;
        public int NoiseOctaves;
        public float3 NoiseOffset;

        public float SurfaceSoftness;
        public float JunctionBlend;

        public void Execute(int index)
        {
            var perSlice = Dim * Dim;
            var x = index / perSlice;
            var rest = index - x * perSlice;
            var y = rest / Dim;
            var z = rest - y * Dim;

            var cell = new int3(x, y, z) - Pad;
            var p = ChunkOrigin + (float3)cell * VoxelSize;

            var d = float.MaxValue;
            var blend = JunctionBlend;

            for (var i = 0; i < Boxes.Length; i++)
            {
                var box = Boxes[i];

                // Нижняя оценка расстояния до примитива. SmoothMin точно равен d, когда
                // кандидат дальше d + blend, поэтому такой примитив можно не считать вовсе.
                if (math.distance(p, box.Center) - box.BoundRadius >= d + blend) continue;

                d = SmoothMin(d, SdRoundBox(p, box), blend);
            }

            // Шум сдвигает границу не более чем на NoiseAmplitude, поэтому вдали от стены
            // результат заранее известен и октавы симплекс-шума считать незачем.
            var band = SurfaceSoftness + NoiseAmplitude;

            float density;
            if (d > band) density = 0f;
            else if (d < -band) density = 1f;
            else
            {
                if (NoiseAmplitude > 0f) d += Fbm(p * NoiseScale + NoiseOffset, NoiseOctaves) * NoiseAmplitude;

                // d < 0 — внутри полости. Мягкость края задаёт ширину переходной зоны.
                density = math.saturate(0.5f - 0.5f * d / SurfaceSoftness);
            }

            Density[index] = density * BoundaryFactor(p);
        }

        /// <summary>Запечатывает край мира сплошной породой, чтобы игрок не выкопался наружу.</summary>
        private float BoundaryFactor(float3 p)
        {
            if (BoundaryThickness <= 0f) return 1f;

            var edge = math.cmin(math.min(p, WorldSize - p));
            return math.saturate(edge / BoundaryThickness);
        }

        /// <summary>Параллелепипед со скруглёнными рёбрами — точная SDF, а не приближение.</summary>
        private static float SdRoundBox(float3 p, CaveBox box)
        {
            var local = math.mul(math.conjugate(box.Rotation), p - box.Center);

            var half = math.max(box.HalfExtents - box.Round, 0f);
            var q = math.abs(local) - half;

            return math.length(math.max(q, 0f)) + math.min(math.cmax(q), 0f) - box.Round;
        }

        private static float SmoothMin(float a, float b, float k)
        {
            if (k <= 1e-4f) return math.min(a, b);

            var h = math.saturate(0.5f + 0.5f * (b - a) / k);
            return math.lerp(b, a, h) - k * h * (1f - h);
        }

        private static float Fbm(float3 p, int octaves)
        {
            var sum = 0f;
            var amplitude = 1f;
            var frequency = 1f;
            var norm = 0f;

            for (var i = 0; i < octaves; i++)
            {
                sum += noise.snoise(p * frequency) * amplitude;
                norm += amplitude;

                amplitude *= 0.5f;
                frequency *= 2f;
            }

            return sum / math.max(norm, 1e-6f);
        }
    }

    /// <summary>
    /// Копает или заращивает породу в сфере.
    ///
    /// Поле не «доливается» на величину силы, а подтягивается к целевому полю сферы,
    /// посчитанному той же SDF и с той же мягкостью края, что и при генерации.
    /// Прошлая версия делала saturate(density + сила) и при силе 1 забивала всю сферу
    /// ровной единицей: внутри получалось плато с нулевым градиентом, переход к породе
    /// укладывался в один воксель, и каверна выходила из плоских граней по сетке
    /// с вырожденными нормалями.
    ///
    /// Побочный выигрыш: повторный удар в то же место больше ничего не меняет,
    /// потому что max с той же целью идемпотентен.
    ///
    /// Работает по тем же мировым координатам, что и генерация, поэтому соседние чанки,
    /// делящие граничный слой сэмплов, получают одинаковые значения и швов не появляется.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct DensityModifyJob : IJobParallelFor
    {
        public NativeArray<float> Density;

        public float3 ChunkOrigin;
        public float VoxelSize;
        public int Dim;
        public int Pad;

        public float3 Center;
        public float Radius;

        /// <summary>Ширина переходной зоны края — та же, что у генерации.</summary>
        public float Softness;

        /// <summary>&gt; 0 — копать, &lt; 0 — заращивать. Модуль — доля от полного эффекта.</summary>
        public float Amount;

        public float3 WorldSize;
        public float BoundaryThickness;

        public void Execute(int index)
        {
            var perSlice = Dim * Dim;
            var x = index / perSlice;
            var rest = index - x * perSlice;
            var y = rest / Dim;
            var z = rest - y * Dim;

            var cell = new int3(x, y, z) - Pad;
            var p = ChunkOrigin + (float3)cell * VoxelSize;

            var softness = math.max(Softness, 1e-3f);
            var sdf = math.distance(p, Center) - Radius;

            // За переходной зоной цель совпадает с текущим значением — считать нечего.
            if (sdf > softness) return;

            var target = math.saturate(0.5f - 0.5f * sdf / softness);
            var current = Density[index];

            var goal = Amount > 0f
                ? math.max(current, target)
                : math.min(current, 1f - target);

            var value = math.lerp(current, goal, math.saturate(math.abs(Amount)));

            if (BoundaryThickness > 0f)
            {
                var edge = math.cmin(math.min(p, WorldSize - p));
                value = math.min(value, math.saturate(edge / BoundaryThickness));
            }

            Density[index] = value;
        }
    }
}
