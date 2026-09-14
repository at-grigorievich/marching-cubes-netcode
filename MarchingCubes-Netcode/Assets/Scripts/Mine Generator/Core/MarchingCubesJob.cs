using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MineGenerator.Core
{
    /// <summary>
    /// Полигонизация скалярного поля плотности методом Marching Cubes.
    ///
    /// Отличия от первой версии джоба:
    /// * таблицы лежат в нативной памяти, поэтому джоб реально компилируется Burst-ом;
    /// * вершины склеиваются по рёбрам сетки (одна вершина на пересечённое ребро вместо
    ///   трёх вершин на треугольник) — примерно в 6 раз меньше вершин;
    /// * нормали берутся из градиента поля плотности, а не из <c>position.normalized</c>,
    ///   как было раньше (там получался мусор, освещение «плыло» вместе с положением чанка).
    ///
    /// Поле плотности: <c>Density[(x*Dim + y)*Dim + z]</c>, плотность &gt; IsoLevel — пустота,
    /// плотность &lt; IsoLevel — порода. Угол ячейки (cx,cy,cz) читается из сэмпла
    /// (cx+Pad, cy+Pad, cz+Pad): <c>Pad = 1</c> даёт кольцо запаса, на котором точно
    /// считается центральная разность для нормалей на границе чанка.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct MarchingCubesJob : IJob
    {
        [ReadOnly] public NativeArray<float> Density;
        [ReadOnly] public NativeArray<sbyte> TriTable;
        [ReadOnly] public NativeArray<int> EdgeCornerA;
        [ReadOnly] public NativeArray<int> EdgeCornerB;

        /// <summary>Число сэмплов плотности по каждой оси.</summary>
        public int Dim;
        /// <summary>Сколько колец сэмплов идёт до первого угла ячейки (0 или 1).</summary>
        public int Pad;
        public float VoxelSize;
        public float IsoLevel;

        public NativeList<float3> Vertices;
        public NativeList<float3> Normals;
        public NativeList<int> Triangles;

        /// <summary>Кэш «ребро сетки -> индекс вершины», размер <c>(Cells+1)^3 * 3</c>.</summary>
        public NativeArray<int> EdgeVertexIndex;

        private int _cells;
        private int _ownerDim;

        public static int EdgeCacheLength(int cells) => (cells + 1) * (cells + 1) * (cells + 1) * 3;

        public void Execute()
        {
            _cells = Dim - 1 - 2 * Pad;
            if (_cells <= 0) return;

            _ownerDim = _cells + 1;

            var cacheLength = EdgeCacheLength(_cells);
            for (var i = 0; i < cacheLength; i++) EdgeVertexIndex[i] = -1;

            for (var x = 0; x < _cells; x++)
            for (var y = 0; y < _cells; y++)
            for (var z = 0; z < _cells; z++)
            {
                var cubeIndex = 0;
                for (var corner = 0; corner < 8; corner++)
                {
                    var o = CornerOffset(corner);
                    if (Sample(x + o.x, y + o.y, z + o.z) < IsoLevel) cubeIndex |= 1 << corner;
                }

                // Полностью внутри породы или полностью в пустоте — грань не пересекается.
                if (cubeIndex == 0 || cubeIndex == 255) continue;

                var row = cubeIndex * MarchingCubesLookup.TriTableStride;

                for (var i = 0; i < MarchingCubesLookup.TriTableStride && TriTable[row + i] >= 0; i += 3)
                {
                    var i0 = VertexForEdge(x, y, z, TriTable[row + i]);
                    var i1 = VertexForEdge(x, y, z, TriTable[row + i + 1]);
                    var i2 = VertexForEdge(x, y, z, TriTable[row + i + 2]);

                    // Вырожденные треугольники ломают запекание коллайдера — выбрасываем.
                    if (i0 == i1 || i1 == i2 || i0 == i2) continue;

                    // В неоднозначных конфигурациях таблицы порядок вершин изредка даёт
                    // треугольник изнанкой наружу. Отсечение задних граней такой треугольник
                    // выкидывает, и сквозь поверхность видно фон. Нормали из градиента
                    // заведомо смотрят в пустоту, поэтому сверяемся с ними и разворачиваем.
                    var geometric = math.cross(Vertices[i1] - Vertices[i2], Vertices[i0] - Vertices[i2]);
                    var shading = Normals[i0] + Normals[i1] + Normals[i2];

                    if (math.dot(geometric, shading) < 0f)
                    {
                        Triangles.Add(i0);
                        Triangles.Add(i1);
                        Triangles.Add(i2);
                        continue;
                    }

                    Triangles.Add(i2);
                    Triangles.Add(i1);
                    Triangles.Add(i0);
                }
            }
        }

        private int VertexForEdge(int cx, int cy, int cz, int edge)
        {
            var ca = EdgeCornerA[edge];
            var cb = EdgeCornerB[edge];

            var oa = CornerOffset(ca);
            var ob = CornerOffset(cb);

            // Ребро принадлежит углу с минимальными координатами; ось — та, по которой углы различаются.
            var owner = math.min(oa, ob);
            var delta = math.abs(oa - ob);
            var axis = delta.x != 0 ? 0 : delta.y != 0 ? 1 : 2;

            var key = (((cx + owner.x) * _ownerDim + (cy + owner.y)) * _ownerDim + (cz + owner.z)) * 3 + axis;

            var cached = EdgeVertexIndex[key];
            if (cached >= 0) return cached;

            var sa = new int3(cx, cy, cz) + oa;
            var sb = new int3(cx, cy, cz) + ob;

            var da = Sample(sa.x, sa.y, sa.z);
            var db = Sample(sb.x, sb.y, sb.z);

            var denom = db - da;
            var t = math.abs(denom) < 1e-6f ? 0.5f : math.saturate((IsoLevel - da) / denom);

            var position = math.lerp((float3)sa, (float3)sb, t) * VoxelSize;

            var normal = math.lerp(Gradient(sa), Gradient(sb), t);
            var lengthSq = math.lengthsq(normal);
            normal = lengthSq > 1e-12f ? normal * math.rsqrt(lengthSq) : new float3(0f, 1f, 0f);

            Vertices.Add(position);
            Normals.Add(normal);

            var index = Vertices.Length - 1;
            EdgeVertexIndex[key] = index;
            return index;
        }

        /// <summary>Градиент плотности растёт в сторону пустоты, то есть наружу от породы.</summary>
        private float3 Gradient(int3 cell)
        {
            var s = cell + Pad;
            return new float3(
                SampleRaw(s.x + 1, s.y, s.z) - SampleRaw(s.x - 1, s.y, s.z),
                SampleRaw(s.x, s.y + 1, s.z) - SampleRaw(s.x, s.y - 1, s.z),
                SampleRaw(s.x, s.y, s.z + 1) - SampleRaw(s.x, s.y, s.z - 1));
        }

        /// <summary>Плотность в углу ячейки (координаты ячейки, без учёта <see cref="Pad"/>).</summary>
        private float Sample(int cx, int cy, int cz) => SampleRaw(cx + Pad, cy + Pad, cz + Pad);

        private float SampleRaw(int x, int y, int z)
        {
            var last = Dim - 1;
            x = math.clamp(x, 0, last);
            y = math.clamp(y, 0, last);
            z = math.clamp(z, 0, last);
            return Density[(x * Dim + y) * Dim + z];
        }

        /// <summary>Смещения углов куба, порядок совпадает с <c>MarchingCubesTables.cubeCorners</c>.</summary>
        private static int3 CornerOffset(int corner)
        {
            switch (corner)
            {
                case 0: return new int3(0, 0, 1);
                case 1: return new int3(1, 0, 1);
                case 2: return new int3(1, 0, 0);
                case 3: return new int3(0, 0, 0);
                case 4: return new int3(0, 1, 1);
                case 5: return new int3(1, 1, 1);
                case 6: return new int3(1, 1, 0);
                default: return new int3(0, 1, 0);
            }
        }
    }
}
