using System;
using System.Text;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Поле потока по ПОВЕРХНОСТИ породы: куда ползти из каждой точки, чтобы прийти к игроку.
    ///
    /// Почему по поверхности, а не по полу. Первая версия была полем высот: у каждой колонки
    /// одна высота пола, особь садилась на неё. Это работало, но намертво приковывало толпу
    /// к земле — паук не мог оказаться на стене или потолке, а именно этого от него и ждут,
    /// и именно так толпа заполняет тоннель, а не размазывается по его дну в одну линию.
    ///
    /// Здесь проходима любая пустая клетка, у которой рядом есть камень. Направление
    /// «от камня» берётся из градиента плотности, то есть это настоящая нормаль
    /// поверхности — по ней особь и ориентируется, и к ней же прижимается.
    ///
    /// Побочно это убрало целый класс ошибок. В поле высот слои сетки стоят на фиксированных
    /// высотах, а пол пандуса едет наклонно, и связность оказывалась заложницей того, куда
    /// попала сетка: этажи не связывались ни при каком размере клетки, ни при какой высоте
    /// шага, ни при каком числе слоёв. Здесь соседство трёхмерное и от разметки по высоте
    /// не зависит вовсе — вся та машинерия (перебор колонки по высоте пола, лазанье,
    /// шаг через клетку) оказалась не нужна и удалена.
    ///
    /// Почему не NavMesh и не поиск пути на особь: пауков сотни и бегут они все в одну точку.
    /// Путь считается ОДИН раз на всю толпу заливкой в ширину от игрока. Триста
    /// <c>NavMeshAgent</c> на WebGL не поедут, а запекание NavMesh по процедурной пещере
    /// в рантайме — секунды фриза на каждый новый уровень. И NavMesh это про пол,
    /// то есть ровно то, от чего здесь уходят.
    ///
    /// Проходимость берётся ИЗ ПОЛЯ ПЛОТНОСТИ, а не из физики: коллайдеры есть только
    /// на поверхностях ходов, внутри сплошной породы их нет вовсе, и любой запрос физики
    /// отвечает «свободно» посреди камня (грабли №1).
    ///
    /// Всё живёт в ЛОКАЛЬНЫХ координатах генерации. В мир переводит <see cref="SpiderCrowd"/>.
    /// </summary>
    public sealed class SpiderFlowField : IDisposable
    {
        /// <summary>Метка «сюда от игрока не доползти».</summary>
        public const ushort Unreachable = ushort.MaxValue;

        public int3 Dim { get; private set; }
        public float CellSize { get; private set; }

        /// <summary>1 — в центре клетки пустота.</summary>
        public NativeArray<byte> Open;

        /// <summary>1 — по клетке можно ползти: пустота и рядом есть камень, за который держаться.</summary>
        public NativeArray<byte> Walkable;

        /// <summary>Единичная нормаль поверхности, направленная ОТ камня. Ноль у непроходимых.</summary>
        public NativeArray<float3> Normal;

        /// <summary>Расстояние от центра клетки до поверхности вдоль нормали, юниты.</summary>
        public NativeArray<float> Depth;

        /// <summary>
        /// Открыта ли грань клетки к соседу: биты 0, 1, 2 — это +X, +Y, +Z.
        ///
        /// Нужно против протекания сквозь тонкую стенку. Две клетки по разные стороны
        /// перегородки тоньше клетки обе пустые и в сетке соседние — заливка прошла бы
        /// сквозь камень. Проба берётся в середине грани, то есть ровно там, где эта
        /// перегородка и стоит.
        /// </summary>
        public NativeArray<byte> FaceOpen;

        /// <summary>Единичное направление к игроку вдоль поверхности. Ноль там, где ползти некуда.</summary>
        public NativeArray<float3> Flow;

        /// <summary>Расстояние до игрока в шагах заливки. Им же отбираются места спавна.</summary>
        public NativeArray<ushort> Distance;

        private NativeArray<int> _queue;

        /// <summary>Клетки, годные под спавн: проходимые и на нужном удалении от игрока.</summary>
        public NativeList<int> SpawnCells;

        public bool IsCreated => Walkable.IsCreated;
        public int CellCount => Dim.x * Dim.y * Dim.z;

        /// <summary>Клетка, от которой шла последняя заливка. -1, если заливки ещё не было.</summary>
        public int SourceCell { get; private set; } = -1;

        public int WalkableCount { get; private set; }

        /// <summary>
        /// Строит поверхностную сетку по полю плотности мира.
        ///
        /// Плотности всех чанков сначала собираются в один сплошной массив, и это
        /// не расточительность: нормаль считается центральной разностью, то есть щупает
        /// соседние сэмплы, и на швах чанков поштучный разбор упирался бы в край массива.
        /// Массив временный, около двух мегабайт, живёт только внутри вызова.
        /// </summary>
        /// <param name="attachRange">Дальше этого от камня клетка непроходима: держаться не за что.</param>
        public void Build(CatacombWorld world, float cellSize, float attachRange)
        {
            Dispose();

            var settings = world.Settings;

            CellSize = math.max(0.25f, cellSize);

            var worldSize = (float3)settings.WorldSize;
            Dim = math.max(1, (int3)math.ceil(worldSize / CellSize));

            var count = CellCount;

            Open = new NativeArray<byte>(count, Allocator.Persistent);
            Walkable = new NativeArray<byte>(count, Allocator.Persistent);
            Normal = new NativeArray<float3>(count, Allocator.Persistent);
            Depth = new NativeArray<float>(count, Allocator.Persistent);
            FaceOpen = new NativeArray<byte>(count, Allocator.Persistent);
            Flow = new NativeArray<float3>(count, Allocator.Persistent);
            Distance = new NativeArray<ushort>(count, Allocator.Persistent);
            _queue = new NativeArray<int>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            SpawnCells = new NativeList<int>(math.max(64, count / 16), Allocator.Persistent);

            AttachRange = attachRange;

            var density = GatherDensity(world);

            try
            {
                new BuildCellsJob
                {
                    Density = density.Samples,
                    DensityDim = density.Dim,
                    DensityPad = CatacombSettings.SamplePadding,
                    VoxelSize = settings.VoxelSize,
                    IsoLevel = settings.IsoLevel,

                    Open = Open,
                    Walkable = Walkable,
                    Normal = Normal,
                    Depth = Depth,
                    FaceOpen = FaceOpen,

                    Dim = Dim,
                    CellSize = CellSize,
                    AttachRange = attachRange,
                    ProbeStep = math.min(0.25f, settings.VoxelSize * 0.5f)
                }.Schedule(count, 64).Complete();
            }
            finally
            {
                density.Dispose();
            }

            WalkableCount = 0;
            for (var i = 0; i < count; i++) WalkableCount += Walkable[i];

            for (var i = 0; i < count; i++) Distance[i] = Unreachable;
        }

        public float AttachRange { get; private set; } = 1.6f;

        /// <summary>Пересчитывает поток от точки игрока.</summary>
        /// <returns>false, если игрок оказался вне проходимых клеток.</returns>
        public bool Rebuild(float3 localTarget, int spawnMinSteps, int spawnMaxSteps, int maxSteps = 250)
        {
            if (!IsCreated) return false;

            var cell = FindNearestWalkable(localTarget, 4);
            if (cell < 0) return false;

            SourceCell = cell;

            new FlowJob
            {
                Walkable = Walkable,
                FaceOpen = FaceOpen,
                Distance = Distance,
                Flow = Flow,
                Queue = _queue,
                SpawnCells = SpawnCells,

                Dim = Dim,
                CellSize = CellSize,
                Start = cell,

                MaxSteps = (ushort)math.clamp(maxSteps, 4, Unreachable - 1),
                SpawnMin = (ushort)math.clamp(spawnMinSteps, 1, Unreachable - 1),
                SpawnMax = (ushort)math.clamp(spawnMaxSteps, spawnMinSteps + 1, Unreachable - 1)
            }.Schedule().Complete();

            return true;
        }

        public int CellIndex(int3 c) => (c.z * Dim.y + c.y) * Dim.x + c.x;

        public int3 CellOf(float3 localPosition) =>
            math.clamp((int3)math.floor(localPosition / CellSize), 0, Dim - 1);

        public bool InRange(int3 c) => math.all(c >= 0) && math.all(c < Dim);

        public float3 CellCentre(int index)
        {
            var c = new int3(index % Dim.x, index / Dim.x % Dim.y, index / (Dim.x * Dim.y));

            return (new float3(c.x, c.y, c.z) + 0.5f) * CellSize;
        }

        /// <summary>Точка на поверхности под клеткой, приподнятая на hover — туда и сажают особь.</summary>
        public float3 CellSurfacePoint(int index, float hover) =>
            CellCentre(index) - Normal[index] * (Depth[index] - hover);

        /// <summary>Ближайшая проходимая клетка к точке, поиском по расширяющемуся кубу.</summary>
        public int FindNearestWalkable(float3 localPosition, int maxRings)
        {
            var origin = CellOf(localPosition);

            for (var ring = 0; ring <= maxRings; ring++)
            {
                var best = -1;
                var bestDistance = float.MaxValue;

                for (var dz = -ring; dz <= ring; dz++)
                for (var dy = -ring; dy <= ring; dy++)
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (ring > 0 && math.abs(dx) != ring && math.abs(dy) != ring && math.abs(dz) != ring) continue;

                    var c = origin + new int3(dx, dy, dz);
                    if (!InRange(c)) continue;

                    var index = CellIndex(c);
                    if (Walkable[index] == 0) continue;

                    var distance = math.lengthsq(CellCentre(index) - localPosition);
                    if (distance >= bestDistance) continue;

                    best = index;
                    bestDistance = distance;
                }

                if (best >= 0) return best;
            }

            return -1;
        }

        public void Dispose()
        {
            if (Open.IsCreated) Open.Dispose();
            if (Walkable.IsCreated) Walkable.Dispose();
            if (Normal.IsCreated) Normal.Dispose();
            if (Depth.IsCreated) Depth.Dispose();
            if (FaceOpen.IsCreated) FaceOpen.Dispose();
            if (Flow.IsCreated) Flow.Dispose();
            if (Distance.IsCreated) Distance.Dispose();
            if (_queue.IsCreated) _queue.Dispose();
            if (SpawnCells.IsCreated) SpawnCells.Dispose();

            SourceCell = -1;
            WalkableCount = 0;
        }

        // ------------------------------------------------------------------ соседство

        /// <summary>
        /// Соседи: шесть по граням плюс двенадцать по рёбрам. Уголковые (все три оси разом)
        /// не берутся — они дают срезку через угол породы, а выигрыша в связности не дают:
        /// куда ведёт уголок, туда же ведёт пара рёберных шагов.
        /// </summary>
        public const int NeighbourCount = 18;

        public static int3 Neighbour(int n)
        {
            switch (n)
            {
                case 0: return new int3(1, 0, 0);
                case 1: return new int3(-1, 0, 0);
                case 2: return new int3(0, 1, 0);
                case 3: return new int3(0, -1, 0);
                case 4: return new int3(0, 0, 1);
                case 5: return new int3(0, 0, -1);
            }

            // Рёберные: две оси из трёх.
            var k = n - 6;
            var axis = k / 4;
            var sign = new int2((k & 1) == 0 ? 1 : -1, (k & 2) == 0 ? 1 : -1);

            return axis switch
            {
                0 => new int3(sign.x, sign.y, 0),
                1 => new int3(0, sign.x, sign.y),
                _ => new int3(sign.x, 0, sign.y)
            };
        }

        /// <summary>Правило перехода. Общее у заливки и у диагностики, чтобы они не разошлись.</summary>
        public struct Topology
        {
            [ReadOnly] public NativeArray<byte> Walkable;
            [ReadOnly] public NativeArray<byte> FaceOpen;

            public int3 Dim;

            public int Index(int3 c) => (c.z * Dim.y + c.y) * Dim.x + c.x;

            public bool InRange(int3 c) => math.all(c >= 0) && math.all(c < Dim);

            /// <summary>Открыта ли грань между клеткой и её соседом по одной оси.</summary>
            private bool FaceClear(int3 from, int3 axis)
            {
                // Биты хранятся у младшей из пары клеток: грань +X клетки c это та же
                // грань, что -X у c + (1,0,0).
                var positive = math.csum(axis) > 0;
                var owner = positive ? from : from + axis;

                if (!InRange(owner)) return false;

                var bit = axis.x != 0 ? 0 : axis.y != 0 ? 1 : 2;

                return (FaceOpen[Index(owner)] & (1 << bit)) != 0;
            }

            /// <summary>Куда приводит шаг, или -1.</summary>
            public int Step(int3 from, int3 offset)
            {
                var target = from + offset;

                if (!InRange(target)) return -1;

                var index = Index(target);
                if (Walkable[index] == 0) return -1;

                var axes = math.abs(offset);

                // По грани — проверяем саму грань.
                if (math.csum(axes) == 1) return FaceClear(from, offset) ? index : -1;

                // По ребру — обе составляющие грани должны быть открыты в обе стороны,
                // иначе это срезка угла сквозь породу.
                var a = new int3(offset.x, 0, 0);
                var b = new int3(0, offset.y, 0);

                if (offset.x == 0) { a = new int3(0, offset.y, 0); b = new int3(0, 0, offset.z); }
                else if (offset.y == 0) { a = new int3(offset.x, 0, 0); b = new int3(0, 0, offset.z); }

                if (!FaceClear(from, a) || !FaceClear(from, b)) return -1;

                return FaceClear(from + a, b) && FaceClear(from + b, a) ? index : -1;
            }
        }

        public Topology GetTopology() => new Topology
        {
            Walkable = Walkable,
            FaceOpen = FaceOpen,
            Dim = Dim
        };

        // ------------------------------------------------------------------ сбор плотности

        private struct WorldDensity : IDisposable
        {
            public NativeArray<float> Samples;
            public int3 Dim;

            public void Dispose()
            {
                if (Samples.IsCreated) Samples.Dispose();
            }
        }

        private static WorldDensity GatherDensity(CatacombWorld world)
        {
            var settings = world.Settings;

            var chunkDim = settings.SampleDim;
            var cells = settings.ChunkResolution;

            var chunks = settings.WorldSizeInChunks;
            var size = new int3(chunks.x, chunks.y, chunks.z);

            // Сэмплы соседних чанков перекрываются: каждый несёт кольцо запаса и общий
            // угол. Значения на наложении совпадают (плотность — функция от позиции),
            // поэтому разбирать наложение не нужно.
            var dim = size * cells + 3;

            var result = new WorldDensity
            {
                Dim = dim,
                Samples = new NativeArray<float>(dim.x * dim.y * dim.z, Allocator.TempJob)
            };

            foreach (var chunk in world.Chunks)
            {
                if (!chunk.HasDensity) continue;

                new GatherChunkJob
                {
                    Chunk = chunk.Density,
                    ChunkDim = chunkDim,
                    Base = chunk.Coord * cells,

                    World = result.Samples,
                    WorldDim = dim
                }.Schedule(chunk.Density.Length, 256).Complete();
            }

            return result;
        }

        [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
        private struct GatherChunkJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Chunk;
            public int ChunkDim;
            public int3 Base;

            [NativeDisableParallelForRestriction] public NativeArray<float> World;
            public int3 WorldDim;

            public void Execute(int index)
            {
                // Раскладка чанка задана CatacombDensityJob: x снаружи, z внутри,
                // то есть index = x * Dim^2 + y * Dim + z.
                var perSlice = ChunkDim * ChunkDim;

                var x = index / perSlice;
                var rest = index - x * perSlice;
                var y = rest / ChunkDim;
                var z = rest - y * ChunkDim;

                var g = Base + new int3(x, y, z);

                if (math.any(g < 0) || math.any(g >= WorldDim)) return;

                World[(g.z * WorldDim.y + g.y) * WorldDim.x + g.x] = Chunk[index];
            }
        }

        // ------------------------------------------------------------------ разметка

        /// <summary>
        /// Размечает клетки: пустая ли, есть ли рядом камень, куда смотрит его поверхность.
        /// </summary>
        [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
        private struct BuildCellsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Density;
            public int3 DensityDim;
            public int DensityPad;
            public float VoxelSize;
            public float IsoLevel;

            [WriteOnly] public NativeArray<byte> Open;
            [WriteOnly] public NativeArray<byte> Walkable;
            [WriteOnly] public NativeArray<float3> Normal;
            [WriteOnly] public NativeArray<float> Depth;
            [WriteOnly] public NativeArray<byte> FaceOpen;

            public int3 Dim;
            public float CellSize;
            public float AttachRange;
            public float ProbeStep;

            public void Execute(int index)
            {
                var x = index % Dim.x;
                var y = index / Dim.x % Dim.y;
                var z = index / (Dim.x * Dim.y);

                var centre = (new float3(x, y, z) + 0.5f) * CellSize;

                Open[index] = 0;
                Walkable[index] = 0;
                Normal[index] = float3.zero;
                Depth[index] = AttachRange;
                FaceOpen[index] = 0;

                var here = Sample(centre);

                // Соглашение проекта инвертировано: больше изоуровня — пустота.
                if (here <= IsoLevel) return;

                Open[index] = 1;

                // Грани — против протекания сквозь перегородку тоньше клетки.
                byte faces = 0;
                if (Sample(centre + new float3(CellSize * 0.5f, 0f, 0f)) > IsoLevel) faces |= 1;
                if (Sample(centre + new float3(0f, CellSize * 0.5f, 0f)) > IsoLevel) faces |= 2;
                if (Sample(centre + new float3(0f, 0f, CellSize * 0.5f)) > IsoLevel) faces |= 4;
                FaceOpen[index] = faces;

                // Нормаль — градиент плотности центральной разностью. Плотность растёт
                // в пустоту, значит градиент смотрит ОТ камня, то есть это и есть
                // внешняя нормаль поверхности.
                var h = math.max(VoxelSize * 0.5f, CellSize * 0.5f);

                var gradient = new float3(
                    Sample(centre + new float3(h, 0f, 0f)) - Sample(centre - new float3(h, 0f, 0f)),
                    Sample(centre + new float3(0f, h, 0f)) - Sample(centre - new float3(0f, h, 0f)),
                    Sample(centre + new float3(0f, 0f, h)) - Sample(centre - new float3(0f, 0f, h)));

                var length = math.length(gradient);

                // Плоский градиент означает «камня рядом нет»: середина зала, где
                // плотность ровная. Держаться там не за что.
                if (length < 1e-5f) return;

                var normal = gradient / length;

                // Расстояние до поверхности — трассировкой навстречу нормали, а не оценкой
                // по градиенту. Оценка врёт там, где переходная зона стены шире шага:
                // плотность в ней не линейна, и особь садилась бы то в камень, то в воздух.
                var steps = (int)math.ceil(AttachRange / ProbeStep);

                var previousT = 0f;
                var previousD = here;

                for (var i = 1; i <= steps; i++)
                {
                    var t = math.min(AttachRange, i * ProbeStep);
                    var d = Sample(centre - normal * t);

                    if (d > IsoLevel)
                    {
                        previousT = t;
                        previousD = d;
                        continue;
                    }

                    Walkable[index] = 1;
                    Normal[index] = normal;
                    Depth[index] = math.lerp(previousT, t,
                        (previousD - IsoLevel) / math.max(1e-5f, previousD - d));

                    return;
                }
            }

            private float Sample(float3 position)
            {
                var g = position / VoxelSize + DensityPad;

                var i0 = (int3)math.floor(g);
                var f = math.saturate(g - i0);

                var a = math.clamp(i0, 0, DensityDim - 1);
                var b = math.clamp(i0 + 1, 0, DensityDim - 1);

                var c000 = At(a.x, a.y, a.z);
                var c100 = At(b.x, a.y, a.z);
                var c010 = At(a.x, b.y, a.z);
                var c110 = At(b.x, b.y, a.z);
                var c001 = At(a.x, a.y, b.z);
                var c101 = At(b.x, a.y, b.z);
                var c011 = At(a.x, b.y, b.z);
                var c111 = At(b.x, b.y, b.z);

                var x0 = math.lerp(math.lerp(c000, c100, f.x), math.lerp(c010, c110, f.x), f.y);
                var x1 = math.lerp(math.lerp(c001, c101, f.x), math.lerp(c011, c111, f.x), f.y);

                return math.lerp(x0, x1, f.z);
            }

            private float At(int x, int y, int z) => Density[(z * DensityDim.y + y) * DensityDim.x + x];
        }

        // ------------------------------------------------------------------ заливка

        /// <summary>
        /// Заливка в ширину от игрока плюс направления потока.
        ///
        /// Один поток намеренно: заливка последовательна по природе, а на WebGL
        /// воркеров всё равно нет.
        /// </summary>
        [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
        private struct FlowJob : IJob
        {
            [ReadOnly] public NativeArray<byte> Walkable;
            [ReadOnly] public NativeArray<byte> FaceOpen;

            public NativeArray<ushort> Distance;
            public NativeArray<float3> Flow;
            public NativeArray<int> Queue;
            public NativeList<int> SpawnCells;

            public int3 Dim;
            public float CellSize;
            public int Start;

            public ushort MaxSteps;
            public ushort SpawnMin;
            public ushort SpawnMax;

            public void Execute()
            {
                var count = Dim.x * Dim.y * Dim.z;

                for (var i = 0; i < count; i++)
                {
                    Distance[i] = Unreachable;
                    Flow[i] = float3.zero;
                }

                SpawnCells.Clear();

                var topology = new Topology { Walkable = Walkable, FaceOpen = FaceOpen, Dim = Dim };

                Distance[Start] = 0;

                Queue[0] = Start;
                var head = 0;
                var tail = 1;

                while (head < tail)
                {
                    var current = Queue[head++];
                    var next = (ushort)(Distance[current] + 1);

                    // Дальше предела заливка не идёт: толпа рисуется на шесть десятков
                    // юнитов и спавнится ближе, а стоимость перестаёт зависеть
                    // от размера мира.
                    if (next > MaxSteps) continue;

                    var c = Decode(current);

                    for (var n = 0; n < NeighbourCount; n++)
                    {
                        var index = topology.Step(c, Neighbour(n));

                        if (index < 0 || Distance[index] != Unreachable) continue;

                        Distance[index] = next;
                        Queue[tail++] = index;
                    }
                }

                // Направления — вторым проходом: на момент снятия клетки с очереди
                // часть её соседей ещё не размечена.
                for (var i = 0; i < count; i++)
                {
                    if (Walkable[i] == 0 || Distance[i] == Unreachable) continue;

                    if (Distance[i] >= SpawnMin && Distance[i] <= SpawnMax) SpawnCells.Add(i);

                    var c = Decode(i);
                    var direction = float3.zero;

                    for (var n = 0; n < NeighbourCount; n++)
                    {
                        var offset = Neighbour(n);
                        var index = topology.Step(c, offset);

                        if (index < 0 || Distance[index] >= Distance[i]) continue;

                        // Складываем ВСЕ направления, которые ведут ближе, а не берём
                        // лучшее: лучшее даёт фиксированные румбы, и толпа идёт по ним
                        // углами; сумма усредняет их в непрерывное направление.
                        direction += math.normalizesafe((float3)offset) * (Distance[i] - Distance[index]);
                    }

                    Flow[i] = math.normalizesafe(direction);
                }
            }

            private int3 Decode(int index) =>
                new int3(index % Dim.x, index / Dim.x % Dim.y, index / (Dim.x * Dim.y));
        }

        // ------------------------------------------------------------------ чтение из джобов

        public FlowSampler Sampler => new FlowSampler
        {
            Walkable = Walkable,
            Normal = Normal,
            Depth = Depth,
            Flow = Flow,
            Dim = Dim,
            CellSize = CellSize
        };

        /// <summary>Чтение поля из джоба: в джоб нельзя передать класс.</summary>
        public struct FlowSampler
        {
            [ReadOnly] public NativeArray<byte> Walkable;
            [ReadOnly] public NativeArray<float3> Normal;
            [ReadOnly] public NativeArray<float> Depth;
            [ReadOnly] public NativeArray<float3> Flow;

            public int3 Dim;
            public float CellSize;

            public int Index(int3 c) => (c.z * Dim.y + c.y) * Dim.x + c.x;

            public int3 CellOf(float3 position) =>
                math.clamp((int3)math.floor(position / CellSize), 0, Dim - 1);

            public bool IsWalkable(int3 c) =>
                math.all(c >= 0) && math.all(c < Dim) && Walkable[Index(c)] != 0;

            /// <summary>
            /// Можно ли особи находиться в этой точке.
            ///
            /// Спрашивается по ГЛУБИНЕ, а не «проходима ли клетка под координатой».
            /// Второе кажется очевидным и неверно: особь прижата к камню вплотную,
            /// и клетка, в которую попадает её координата, сплошь и рядом имеет центр
            /// уже внутри породы — то есть непроходима. Проверка шага отвергала тогда
            /// любое движение и гасила скорость впятеро за кадр: замер показывал
            /// 0.25 юнита в секунду при заданных 3.2.
            ///
            /// Это та же грабля, что с клеткой стояния в поле высот, только в трёх
            /// измерениях. Глубина же берётся из той же интерполяции, которой особь
            /// прижимается к поверхности, поэтому движение и прижим не спорят.
            /// </summary>
            public bool CanStand(float3 position)
            {
                SampleAt(position, out _, out _, out var depth, out var valid);

                return valid && depth > 0f;
            }

            /// <summary>
            /// Поток, нормаль поверхности и расстояние до неё — трилинейно по восьми клеткам.
            ///
            /// Взвешивается проходимостью: непроходимая клетка в выборку не входит вовсе,
            /// иначе её нулевая нормаль тянула бы особь в камень ровно там, где она
            /// к камню ближе всего.
            /// </summary>
            public void SampleAt(float3 position, out float3 flow, out float3 normal, out float depth,
                out bool valid)
            {
                flow = float3.zero;
                normal = new float3(0f, 1f, 0f);
                depth = 0f;
                valid = false;

                var g = position / CellSize - 0.5f;

                var baseCell = (int3)math.floor(g);
                var t = math.saturate(g - baseCell);

                var sumFlow = float3.zero;
                var sumNormal = float3.zero;
                var sumDepth = 0f;
                var sumWeight = 0f;

                for (var dz = 0; dz <= 1; dz++)
                for (var dy = 0; dy <= 1; dy++)
                for (var dx = 0; dx <= 1; dx++)
                {
                    var c = baseCell + new int3(dx, dy, dz);

                    if (!IsWalkable(c)) continue;

                    var weight = (dx == 0 ? 1f - t.x : t.x) *
                                 (dy == 0 ? 1f - t.y : t.y) *
                                 (dz == 0 ? 1f - t.z : t.z);

                    if (weight <= 0f) continue;

                    var index = Index(c);

                    sumFlow += Flow[index] * weight;
                    sumNormal += Normal[index] * weight;

                    // Глубина приводится к точке запроса, а не берётся из центра клетки:
                    // иначе особь между клетками дёргается по нормали на полклетки.
                    //
                    // Знак ПЛЮС, и это не очевидно. Поверхность под клеткой лежит в точке
                    // centre - normal * Depth, значит расстояние от произвольной точки p
                    // до неё вдоль нормали равно dot(p - (centre - normal*Depth), normal),
                    // то есть Depth + dot(p - centre, normal). С минусом ошибка удваивается
                    // вместо того, чтобы гаситься, и прижим к поверхности загоняет особь
                    // внутрь камня — замер показывал 97% толпы в породе.
                    sumDepth += (Depth[index] + math.dot(position - CentreOf(c), Normal[index])) * weight;

                    sumWeight += weight;
                }

                if (sumWeight <= 1e-4f) return;

                flow = math.normalizesafe(sumFlow / sumWeight);
                normal = math.normalizesafe(sumNormal / sumWeight, new float3(0f, 1f, 0f));
                depth = sumDepth / sumWeight;
                valid = true;
            }

            private float3 CentreOf(int3 c) => (new float3(c.x, c.y, c.z) + 0.5f) * CellSize;
        }

        // ------------------------------------------------------------------ диагностика

        public string Describe()
        {
            if (!IsCreated) return "поле потока не построено";

            var reachable = 0;
            for (var i = 0; i < Distance.Length; i++)
            {
                if (Distance[i] != Unreachable) reachable++;
            }

            return $"сетка {Dim.x}x{Dim.y}x{Dim.z} по {CellSize:0.0} юнита, " +
                   $"поверхности {WalkableCount}, достижимо {reachable}, " +
                   $"мест под спавн {(SpawnCells.IsCreated ? SpawnCells.Length : 0)}";
        }

        /// <summary>
        /// Разбор недостижимого по полосам высоты: что отделяет каждый отрезанный кусок.
        /// Общая доля говорит только «плохо», а причина у разрыва бывает разная.
        /// </summary>
        public string DescribeFrontier()
        {
            if (!IsCreated) return "  граница: поле не построено";

            const int search = 6;
            const int band = 8;

            var bands = (Dim.y + band - 1) / band;

            var count = new int[bands];
            var gap = new float[bands];
            var where = new float3[bands];

            for (var i = 0; i < bands; i++) gap[i] = float.MaxValue;

            for (var i = 0; i < Walkable.Length; i++)
            {
                if (Walkable[i] == 0 || Distance[i] != Unreachable) continue;

                var c = new int3(i % Dim.x, i / Dim.x % Dim.y, i / (Dim.x * Dim.y));
                var slot = c.y / band;

                count[slot]++;

                var here = CellCentre(i);

                for (var dz = -search; dz <= search; dz++)
                for (var dy = -search; dy <= search; dy++)
                for (var dx = -search; dx <= search; dx++)
                {
                    var t = c + new int3(dx, dy, dz);

                    if (!InRange(t)) continue;

                    var index = CellIndex(t);
                    if (Walkable[index] == 0 || Distance[index] == Unreachable) continue;

                    var distance = math.distance(here, CellCentre(index));

                    if (distance >= gap[slot]) continue;

                    gap[slot] = distance;
                    where[slot] = here;
                }
            }

            var text = new StringBuilder("  чем отрезан каждый кусок (полоса: недостижимо, ближайший подход)");

            for (var i = 0; i < bands; i++)
            {
                if (count[i] == 0) continue;

                var low = i * band * CellSize;
                var high = math.min((i + 1) * band, Dim.y) * CellSize;

                text.AppendLine();
                text.Append($"    Y {low:0}-{high:0}: недостижимо {count[i]}, ");

                text.Append(gap[i] < float.MaxValue
                    ? $"ближе всего подходит на {gap[i]:0.00} юнита у ({where[i].x:0.0}, {where[i].y:0.0}, {where[i].z:0.0})"
                    : $"достижимого нет и на {search * CellSize:0.0} юнита вокруг");
            }

            return text.ToString();
        }

        /// <summary>Гизмо: где толпа может держаться и куда она оттуда поползёт.</summary>
        public void DrawGizmos(Matrix4x4 localToWorld, float3 around, float radius)
        {
            if (!IsCreated) return;

            var previous = Gizmos.matrix;
            Gizmos.matrix = localToWorld;

            var radiusSq = radius * radius;

            for (var i = 0; i < Walkable.Length; i++)
            {
                if (Walkable[i] == 0) continue;

                var point = CellSurfacePoint(i, 0f);
                if (math.lengthsq(point - around) > radiusSq) continue;

                var reachable = Distance[i] != Unreachable;

                Gizmos.color = reachable
                    ? new Color(0.2f, 0.9f, 0.4f, 0.5f)
                    : new Color(0.9f, 0.3f, 0.2f, 0.4f);

                Gizmos.DrawRay((Vector3)point, (Vector3)(Normal[i] * CellSize * 0.35f));

                if (!reachable) continue;

                Gizmos.color = new Color(0.9f, 0.9f, 0.3f, 0.5f);
                Gizmos.DrawRay((Vector3)point, (Vector3)(Flow[i] * CellSize * 0.5f));
            }

            Gizmos.matrix = previous;
        }
    }
}
