using System;
using System.Collections.Generic;
using System.Text;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Поле потока: куда бежать из каждой точки уровня, чтобы прийти к игроку.
    ///
    /// Почему не NavMesh и не поиск пути на особь. Пауков в кадре сотни, и все они бегут
    /// в одну точку — к игроку. Это ровно тот случай, ради которого поле потока
    /// и придумано: путь считается ОДИН раз на всю толпу заливкой в ширину от игрока,
    /// а особь потом просто читает готовое направление в своей клетке. Триста
    /// NavMeshAgent на WebGL без воркеров не поедут, а запекание NavMesh по
    /// процедурной пещере в рантайме это секунды фриза на каждый новый уровень.
    ///
    /// Проходимость берётся ИЗ ПОЛЯ ПЛОТНОСТИ, а не из физики, и это не вкусовщина:
    /// коллайдеры в катакомбах есть только на поверхностях ходов, внутри сплошной породы
    /// их нет вовсе, и любой запрос физики отвечает «свободно» посреди камня. На этом
    /// в проекте уже обжигались — заливка на физике вытекала сквозь стены и заливала
    /// больше ячеек, чем есть в мире.
    ///
    /// Всё живёт в ЛОКАЛЬНЫХ координатах генерации, тех же, в которых лежат чанки.
    /// Перевод в мир делает <see cref="SpiderCrowd"/> на границе.
    /// </summary>
    public sealed class SpiderFlowField : IDisposable
    {
        /// <summary>Метка «сюда от игрока не дойти».</summary>
        public const ushort Unreachable = ushort.MaxValue;

        public int3 Dim { get; private set; }
        public float CellSize { get; private set; }

        /// <summary>1 — по клетке можно идти, 0 — нельзя.</summary>
        public NativeArray<byte> Walkable;

        /// <summary>Высота пола в клетке. Считается один раз при постройке, из плотности.</summary>
        public NativeArray<float> FloorY;

        /// <summary>Единичное направление к игроку. Ноль там, где идти некуда.</summary>
        public NativeArray<float3> Flow;

        /// <summary>
        /// Куда клетку отжимает от ближайшей породы, и насколько сильно.
        ///
        /// Нужно потому, что клетка сетки крупнее, чем точность, с которой особь должна
        /// держаться хода: коридор шириной 3.5 юнита это две клетки с хвостиком, и
        /// половина краевой клетки лежит внутри камня. Одной проходимости мало —
        /// по ней особь имеет право стоять где угодно внутри клетки, в том числе
        /// в стене. Здесь же замер идёт по самой плотности, лучами из точки пола,
        /// то есть с точностью вокселя, а не клетки.
        ///
        /// Считается при постройке и от игрока не зависит: порода не движется.
        /// </summary>
        public NativeArray<float3> WallPush;

        /// <summary>
        /// 1 — в центре клетки пустота. Не то же самое, что проходимость: проходима
        /// клетка, стоящая на полу, а открыта — любая, где нет породы.
        ///
        /// Нужна для переходов вверх-вниз: спуск с уступа или подъём по стене разрешён
        /// только если между двумя площадками пусто. Проверять это физикой нельзя
        /// (коллайдеров внутри породы нет), а плотность к моменту заливки уже выброшена —
        /// поэтому ответ снимается один раз, при постройке.
        /// </summary>
        public NativeArray<byte> Open;

        /// <summary>Расстояние до игрока в шагах заливки. Им же отбираются места спавна.</summary>
        public NativeArray<ushort> Distance;

        private NativeArray<int> _queue;

        /// <summary>Клетки, годные под спавн: проходимые и на нужном удалении от игрока.</summary>
        public NativeList<int> SpawnCells;

        public bool IsCreated => Walkable.IsCreated;
        public int CellCount => Dim.x * Dim.y * Dim.z;

        /// <summary>Клетка, от которой шла последняя заливка. -1, если заливки ещё не было.</summary>
        public int SourceCell { get; private set; } = -1;

        /// <summary>Сколько клеток оказалось проходимыми — это и есть «объём», где живёт толпа.</summary>
        public int WalkableCount { get; private set; }

        /// <summary>
        /// Строит сетку проходимости по полю плотности мира.
        ///
        /// Плотности всех чанков сначала собираются в один сплошной массив, и это
        /// не расточительность, а единственный способ не получить разрывы на швах.
        /// Клетка сетки потока не совпадает с границей чанка, а щуп пола из неё уходит
        /// вниз на пару юнитов и запросто пересекает шов. Если считать по чанку
        /// отдельно, щуп упирается в край массива и клетка объявляется непроходимой —
        /// и так по горизонтальной полосе на каждой границе чанков через весь уровень,
        /// то есть связность рвётся там, где в уровне ничего нет.
        ///
        /// Массив временный: 100x51x100 сэмплов это около двух мегабайт, и они живут
        /// только внутри этого вызова.
        /// </summary>
        public void Build(CatacombWorld world, float cellSize, float headroom, float agentStep,
            float floorReach = 1.05f)
        {
            Dispose();

            var settings = world.Settings;

            CellSize = math.max(0.25f, cellSize);

            var worldSize = (float3)settings.WorldSize;
            Dim = math.max(1, (int3)math.ceil(worldSize / CellSize));

            var count = CellCount;

            Walkable = new NativeArray<byte>(count, Allocator.Persistent);
            FloorY = new NativeArray<float>(count, Allocator.Persistent);
            Flow = new NativeArray<float3>(count, Allocator.Persistent);
            WallPush = new NativeArray<float3>(count, Allocator.Persistent);
            Open = new NativeArray<byte>(count, Allocator.Persistent);
            Distance = new NativeArray<ushort>(count, Allocator.Persistent);
            _queue = new NativeArray<int>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            SpawnCells = new NativeList<int>(math.max(64, count / 16), Allocator.Persistent);

            var density = GatherDensity(world);

            try
            {
                var job = new BuildCellsJob
                {
                    Density = density.Samples,
                    DensityDim = density.Dim,
                    DensityPad = CatacombSettings.SamplePadding,
                    VoxelSize = settings.VoxelSize,
                    IsoLevel = settings.IsoLevel,

                    Walkable = Walkable,
                    FloorY = FloorY,
                    WallPush = WallPush,
                    Open = Open,

                    Dim = Dim,
                    CellSize = CellSize,

                    // Щуп стен чуть длиннее половины клетки: короче — особь успевает
                    // войти в камень раньше, чем её начнёт отжимать, длиннее — узкий
                    // ход отжимает с обеих сторон разом и толпа в нём встаёт.
                    WallProbe = CellSize * 0.8f,
                    WallProbeHeight = headroom * 0.5f,

                    // Ровно клетка: на один пол — один слой проходимых клеток.
                    //
                    // Больше пробовалось (до четырёх слоёв) и оказалось вредно. Лишние
                    // слои одной колонки описывают ОДНО И ТО ЖЕ место — у них общий пол,
                    // щуп находит ту же поверхность, — но заливка считает их разными
                    // клетками и доходит только до одной. Достижимость от этого падала
                    // втрое: проходимых клеток втрое больше, а дошли всё те же.
                    //
                    // Связность от числа слоёв и не должна зависеть: переход между
                    // колонками ищется перебором по высоте пола, а не по индексу слоя,
                    // см. Topology.Step.
                    MaxDrop = CellSize * math.max(1.05f, floorReach),
                    ProbeStep = math.min(0.25f, settings.VoxelSize * 0.5f),
                    Headroom = headroom
                };

                job.Schedule(count, 64).Complete();
            }
            finally
            {
                density.Dispose();
            }

            WalkableCount = 0;
            for (var i = 0; i < count; i++) WalkableCount += Walkable[i];

            AgentStep = agentStep;

            // До первой заливки поток пуст, а расстояние — «не дойти»: пока игрок
            // не назван, бежать некуда, и толпа должна честно стоять, а не мчаться
            // в угол мира по мусору в неинициализированной памяти.
            for (var i = 0; i < count; i++) Distance[i] = Unreachable;
        }

        /// <summary>Максимальный перепад пола между соседними клетками, который особь берёт шагом.</summary>
        public float AgentStep { get; private set; } = 1.2f;

        /// <summary>
        /// Пересчитывает поток от точки игрока. Зовётся не каждый кадр, а когда игрок
        /// сменил клетку — см. <see cref="SpiderCrowd"/>.
        /// </summary>
        /// <returns>false, если игрок оказался вне проходимых клеток (например, в породе).</returns>
        public bool Rebuild(float3 localTarget, int spawnMinSteps, int spawnMaxSteps, int maxSteps = 120)
        {
            if (!IsCreated) return false;

            var cell = FindNearestWalkable(localTarget, 3);
            if (cell < 0) return false;

            SourceCell = cell;

            var job = new FlowJob
            {
                Walkable = Walkable,
                FloorY = FloorY,
                Topology = GetTopology(),
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
            };

            job.Schedule().Complete();

            return true;
        }

        public int CellIndex(int3 c) => (c.z * Dim.y + c.y) * Dim.x + c.x;

        public int3 CellOf(float3 localPosition) =>
            math.clamp((int3)math.floor(localPosition / CellSize), 0, Dim - 1);

        public bool InRange(int3 c) => math.all(c >= 0) && math.all(c < Dim);

        /// <summary>Центр клетки по горизонтали, пол — по вертикали.</summary>
        public float3 CellFloorPoint(int index)
        {
            var c = new int3(index % Dim.x, index / Dim.x % Dim.y, index / (Dim.x * Dim.y));

            return new float3((c.x + 0.5f) * CellSize, FloorY[index], (c.z + 0.5f) * CellSize);
        }

        /// <summary>
        /// Ближайшая проходимая клетка к точке, поиском по расширяющемуся кубу.
        ///
        /// Нужна потому, что игрок стоит не в центре клетки и запросто оказывается
        /// в клетке, у которой пол не нашёлся — например, в прыжке или на самом краю
        /// уступа. Без поиска толпа в такие моменты теряла бы цель целиком.
        /// </summary>
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
                    // На кольце больше нуля перебираем только его оболочку: внутренность
                    // уже проверена на прошлых кольцах.
                    if (ring > 0 && math.abs(dx) != ring && math.abs(dy) != ring && math.abs(dz) != ring) continue;

                    var c = origin + new int3(dx, dy, dz);
                    if (!InRange(c)) continue;

                    var index = CellIndex(c);
                    if (Walkable[index] == 0) continue;

                    var delta = CellFloorPoint(index) - localPosition;
                    var distance = math.lengthsq(delta);

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
            if (Walkable.IsCreated) Walkable.Dispose();
            if (FloorY.IsCreated) FloorY.Dispose();
            if (Flow.IsCreated) Flow.Dispose();
            if (WallPush.IsCreated) WallPush.Dispose();
            if (Open.IsCreated) Open.Dispose();
            if (Distance.IsCreated) Distance.Dispose();
            if (_queue.IsCreated) _queue.Dispose();
            if (SpawnCells.IsCreated) SpawnCells.Dispose();

            SourceCell = -1;
            WalkableCount = 0;
        }

        /// <summary>Поле плотности всего мира одним куском. Живёт только на время постройки сетки.</summary>
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

            // Сэмплы соседних чанков перекрываются: каждый несёт кольцо запаса шириной
            // в Pad и общий угол. Глобальный сэмпл чанка coord — это coord * cells + s,
            // и при s до cells + 2 хвост чанка ложится ровно на начало соседнего.
            // Значения там совпадают (плотность — функция от позиции), поэтому наложение
            // безобидно и разбирать его не нужно.
            var dim = size * cells + 3;

            var result = new WorldDensity
            {
                Dim = dim,
                Samples = new NativeArray<float>(dim.x * dim.y * dim.z, Allocator.TempJob)
            };

            foreach (var chunk in world.Chunks)
            {
                if (!chunk.HasDensity) continue;

                var job = new GatherChunkJob
                {
                    Chunk = chunk.Density,
                    ChunkDim = chunkDim,
                    Base = chunk.Coord * cells,

                    World = result.Samples,
                    WorldDim = dim
                };

                job.Schedule(chunk.Density.Length, 256).Complete();
            }

            return result;
        }

        [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
        private struct GatherChunkJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Chunk;
            public int ChunkDim;
            public int3 Base;

            // Чанки пишут в непересекающиеся области, кроме общих сэмплов на швах,
            // а там значение одно и то же. Джобы при этом идут по одному, с Complete
            // после каждого, так что гонки нет и без атомарных операций.
            [NativeDisableParallelForRestriction] public NativeArray<float> World;
            public int3 WorldDim;

            public void Execute(int index)
            {
                // Раскладка чанка задана CatacombDensityJob: x снаружи, z внутри,
                // то есть index = x * Dim^2 + y * Dim + z. Перепутать здесь оси —
                // значит получить зеркально-транспонированный уровень, в котором
                // проходимость размечена по чужим стенам.
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

        /// <summary>
        /// Размечает клетки: где стоит пол, туда можно; где над полом низко или пола
        /// нет вовсе — нельзя.
        /// </summary>
        [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
        private struct BuildCellsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Density;
            public int3 DensityDim;
            public int DensityPad;
            public float VoxelSize;
            public float IsoLevel;

            [WriteOnly] public NativeArray<byte> Walkable;
            [WriteOnly] public NativeArray<float> FloorY;
            [WriteOnly] public NativeArray<float3> WallPush;
            [WriteOnly] public NativeArray<byte> Open;

            public int3 Dim;
            public float CellSize;
            public float MaxDrop;
            public float ProbeStep;
            public float Headroom;
            public float WallProbe;
            public float WallProbeHeight;

            public void Execute(int index)
            {
                var x = index % Dim.x;
                var y = index / Dim.x % Dim.y;
                var z = index / (Dim.x * Dim.y);

                var center = (new float3(x, y, z) + 0.5f) * CellSize;

                Walkable[index] = 0;
                Open[index] = 0;
                FloorY[index] = center.y;
                WallPush[index] = float3.zero;

                var top = Sample(center);

                // Соглашение проекта инвертировано: больше изоуровня — пустота.
                if (top <= IsoLevel) return;

                Open[index] = 1;

                var steps = (int)math.ceil(MaxDrop / ProbeStep);

                var previousY = center.y;
                var previousD = top;

                for (var i = 1; i <= steps; i++)
                {
                    // Щуп не должен уходить глубже MaxDrop: окно высотой ровно в клетку
                    // ловит РОВНО один центр, а лишние сантиметры вниз впускают второй,
                    // и в колонке появляется дубль той же площадки.
                    var probeY = math.max(center.y - MaxDrop, center.y - i * ProbeStep);
                    var d = Sample(new float3(center.x, probeY, center.z));

                    if (d > IsoLevel)
                    {
                        previousY = probeY;
                        previousD = d;
                        continue;
                    }

                    // Пол ставим на само пересечение изоуровня, а не на шаг щупа:
                    // шаг грубее вокселя, и без доводки особи стояли бы ступеньками.
                    var t = (previousD - IsoLevel) / math.max(1e-5f, previousD - d);
                    var floor = math.lerp(previousY, probeY, t);

                    // Под потолком в полроста особи делать нечего: туда она пролезет
                    // по полю потока, а в кадре окажется наполовину в камне.
                    if (Sample(new float3(center.x, floor + Headroom, center.z)) <= IsoLevel) return;

                    Walkable[index] = 1;
                    FloorY[index] = floor;
                    WallPush[index] = MeasureWalls(new float3(center.x, floor + WallProbeHeight, center.z));
                    return;
                }
            }

            /// <summary>
            /// Насколько и куда точку отжимает от породы вокруг.
            ///
            /// Восемь направлений по горизонтали и всего две пробы на каждом — у стенки
            /// и на полном вылете щупа. Полноценная трассировка здесь не нужна: результат
            /// потом ещё сглаживается между четырьмя клетками, и лишняя точность в него
            /// не доживает, а стоит она восьмисот тысяч выборок плотности на уровень.
            /// </summary>
            private float3 MeasureWalls(float3 from)
            {
                var push = float3.zero;

                for (var k = 0; k < 8; k++)
                {
                    math.sincos(k * (math.PI * 0.25f), out var s, out var c);

                    var direction = new float3(c, 0f, s);

                    var near = Sample(from + direction * (WallProbe * 0.5f)) <= IsoLevel;
                    var far = Sample(from + direction * WallProbe) <= IsoLevel;

                    if (near) push -= direction;
                    else if (far) push -= direction * 0.5f;
                }

                return push * 0.25f;
            }

            /// <summary>Трилинейная выборка плотности. Вне мира — порода, за это отвечает зажим.</summary>
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

        /// <summary>
        /// Заливка в ширину от игрока плюс направления потока.
        ///
        /// Один поток намеренно: заливка последовательна по своей природе, распараллелить
        /// её можно только волновым фронтом, а на сетке в полсотни тысяч клеток это
        /// усложнение ради миллисекунды. На WebGL воркеров всё равно нет.
        /// </summary>
        [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
        private struct FlowJob : IJob
        {
            [ReadOnly] public NativeArray<byte> Walkable;
            [ReadOnly] public NativeArray<float> FloorY;

            public Topology Topology;

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

                Distance[Start] = 0;

                Queue[0] = Start;
                var head = 0;
                var tail = 1;

                while (head < tail)
                {
                    var current = Queue[head++];
                    var next = (ushort)(Distance[current] + 1);

                    // Дальше предела заливка не идёт, и это не экономия ради экономии:
                    // толпа рисуется на шесть десятков юнитов, спавнится ещё ближе,
                    // и путь из дальнего угла уровня никому не нужен. Зато стоимость
                    // перестаёт зависеть от размера мира — а пересчёт идёт каждый раз,
                    // когда игрок сменил клетку, то есть несколько раз в секунду на бегу.
                    if (next > MaxSteps) continue;

                    var c = Decode(current);

                    for (var n = 0; n < NeighbourCount; n++)
                    {
                        var direction = Neighbour(n);

                        var index = Topology.Step(c, direction);

                        if (index < 0 || Distance[index] != Unreachable) continue;
                        if (!Topology.DiagonalClear(c, direction, index)) continue;

                        Distance[index] = next;
                        Queue[tail++] = index;
                    }
                }

                // Направления — вторым проходом, когда расстояния уже проставлены.
                // Внутри заливки их считать нельзя: на момент, когда клетка снимается
                // с очереди, часть её соседей ещё не размечена.
                for (var i = 0; i < count; i++)
                {
                    if (Walkable[i] == 0 || Distance[i] == Unreachable) continue;

                    if (Distance[i] >= SpawnMin && Distance[i] <= SpawnMax) SpawnCells.Add(i);

                    var c = Decode(i);
                    var direction = float3.zero;

                    for (var n = 0; n < NeighbourCount; n++)
                    {
                        var offset = Neighbour(n);

                        var index = Topology.Step(c, offset);

                        if (index < 0 || Distance[index] >= Distance[i]) continue;
                        if (!Topology.DiagonalClear(c, offset, index)) continue;

                        // Складываем ВСЕ направления, которые ведут ближе, а не берём
                        // лучшее. Лучшее даёт восемь фиксированных румбов, и толпа идёт
                        // по ним углами; сумма усредняет их в непрерывное направление.
                        var step = new float3(offset.x * CellSize, FloorY[index] - FloorY[i], offset.y * CellSize);

                        direction += math.normalizesafe(step) * (Distance[i] - Distance[index]);
                    }

                    Flow[i] = math.normalizesafe(direction);
                }
            }

            private int Index(int3 c) => (c.z * Dim.y + c.y) * Dim.x + c.x;

            private int3 Decode(int index) =>
                new int3(index % Dim.x, index / Dim.x % Dim.y, index / (Dim.x * Dim.y));

            private const int NeighbourCount = SpiderFlowField.NeighbourCount;

            private static int2 Neighbour(int n) => SpiderFlowField.Neighbour(n);
        }

        /// <summary>Восемь направлений по горизонтали. Вертикаль соседом не бывает — см. Topology.</summary>
        public const int NeighbourCount = 8;

        public static int2 Neighbour(int n)
        {
            // Девять клеток три на три, из которых пропускаем среднюю: она и есть
            // сама клетка, а не сосед.
            var m = n < 4 ? n : n + 1;

            return new int2(m % 3 - 1, m / 3 - 1);
        }

        /// <summary>
        /// Правило перехода между клетками. Отдельной структурой, потому что им ходит
        /// и заливка (джоб), и разбор связности (обычный код), а разойтись им нельзя:
        /// тогда диагностика объясняла бы не то, что мешает на самом деле.
        ///
        /// Ключевое отличие от наивной сетки: сосед ищется НЕ на фиксированных уровнях
        /// высоты, а перебором колонки по высоте пола.
        ///
        /// Так пришлось сделать после замеров. Сначала соседями считались клетки
        /// с разницей уровня не больше одного, и уровни уровнями и оставались:
        /// этажи не связывались НИ при каком размере клетки (от 0.75 до 2), НИ при какой
        /// высоте шага (от 1.2 до 3), НИ при каком числе слоёв над полом (от 1 до 4) —
        /// достижимость упрямо держалась около 25%, то есть ровно один этаж из четырёх
        /// кусков. Причина в том, что уровни сетки стоят на фиксированных высотах,
        /// а пол пандуса едет наклонно: на пандусе соседняя колонка отличается по уровню
        /// на сколько придётся, и связность оказывалась заложницей того, куда попала
        /// сетка, а не того, можно ли туда шагнуть.
        ///
        /// Здесь индекс уровня на связность не влияет вовсе. Влияет только разница
        /// высот пола — то есть ровно то, что мешает или не мешает шагнуть.
        /// </summary>
        public struct Topology
        {
            [ReadOnly] public NativeArray<byte> Walkable;
            [ReadOnly] public NativeArray<float> FloorY;
            [ReadOnly] public NativeArray<byte> Open;

            public int3 Dim;
            public float MaxStep;

            /// <summary>
            /// Насколько особь способна спуститься или подняться там, где шагом уже
            /// не обойтись. Это паук: по отвесной стене он лезет, и уступ в три-четыре
            /// юнита для него не препятствие, а обычный спуск.
            ///
            /// Без этого лестницы между этажами оказывались для толпы тупиком: замер
            /// показал, что голова лестницы отстоит от пола зала на 3.65 юнита по
            /// высоте, и шагом туда не попасть ни при каком размере клетки.
            /// </summary>
            public float MaxClimb;

            public int Index(int3 c) => (c.z * Dim.y + c.y) * Dim.x + c.x;

            /// <summary>
            /// Куда приводит шаг в сторону: ищем в целевой колонке проходимую клетку
            /// с полом, ближайшим по высоте к нашему.
            /// </summary>
            /// <returns>Индекс клетки или -1.</returns>
            public int Step(int3 from, int2 direction)
            {
                var near = StepBy(from, direction, 1);
                if (near >= 0) return near;

                // Через клетку — только если вплотную идти некуда.
                //
                // Нужно из-за дырок в размеченной поверхности: одна колонка, где пол
                // попал ровно на границу слоя или не хватило запаса над головой,
                // разрывает цепочку целиком. Замер показывал такой разрыв на выходе
                // с лестницы: ближайшая размеченная клетка стояла в 3.35 юнита
                // по горизонтали, то есть ровно через одну.
                //
                // Сквозь породу это не пускает: промежуточная колонка обязана быть
                // открытой, то есть между площадками должен быть воздух.
                return StepBy(from, direction, 2);
            }

            private int StepBy(int3 from, int2 direction, int reachCells)
            {
                var x = from.x + direction.x * reachCells;
                var z = from.z + direction.y * reachCells;

                if (x < 0 || z < 0 || x >= Dim.x || z >= Dim.z) return -1;

                if (reachCells > 1)
                {
                    var midX = from.x + direction.x;
                    var midZ = from.z + direction.y;

                    if (midX < 0 || midZ < 0 || midX >= Dim.x || midZ >= Dim.z) return -1;
                    if (Open[Index(new int3(midX, from.y, midZ))] == 0) return -1;
                }

                var floor = FloorY[Index(from)];

                var best = -1;
                var bestDelta = float.MaxValue;

                // Сначала узкий проход — соседние уровни. Обычный шаг по ровному полу
                // и по пандусу попадает сюда, а это подавляющее большинство переходов;
                // широкий поиск ради них обходить незачем. Заливка от этого разделения
                // ускоряется в разы: без него каждая клетка перебирала бы по десятку
                // уровней в каждом из восьми направлений.
                var nearLow = math.max(0, from.y - 1);
                var nearHigh = math.min(Dim.y - 1, from.y + 1);

                for (var y = nearLow; y <= nearHigh; y++)
                {
                    var index = Index(new int3(x, y, z));

                    if (Walkable[index] == 0) continue;

                    var delta = math.abs(FloorY[index] - floor);

                    if (delta > MaxStep || delta >= bestDelta) continue;

                    best = index;
                    bestDelta = delta;
                }

                if (best >= 0) return best;

                // Широкий поиск — только если шагом не вышло: спуск с уступа, подъём
                // по стене, выход на лестницу.
                var span = (int)math.ceil(math.max(MaxStep, MaxClimb) / math.max(0.01f, CellHeight)) + 1;

                var low = math.max(0, from.y - span);
                var high = math.min(Dim.y - 1, from.y + span);

                for (var y = low; y <= high; y++)
                {
                    var index = Index(new int3(x, y, z));

                    if (Walkable[index] == 0) continue;

                    var delta = math.abs(FloorY[index] - floor);

                    if (delta >= bestDelta) continue;

                    // Ступенькой берётся всё, что ниже порога шага. Выше — только лазаньем,
                    // и лезть можно лишь там, где между площадками действительно пусто:
                    // иначе особь поднималась бы сквозь перекрытие на этаж выше.
                    if (delta > MaxStep)
                    {
                        if (delta > MaxClimb) continue;
                        if (!ColumnClear(x, z, from.y, y)) continue;
                    }

                    best = index;
                    bestDelta = delta;
                }

                return best;
            }

            /// <summary>Пусто ли в колонке между двумя уровнями — путь для спуска или подъёма.</summary>
            private bool ColumnClear(int x, int z, int fromY, int toY)
            {
                var low = math.min(fromY, toY);
                var high = math.max(fromY, toY);

                for (var y = low; y <= high; y++)
                {
                    if (Open[Index(new int3(x, y, z))] == 0) return false;
                }

                return true;
            }

            /// <summary>Высота клетки. Хранится отдельно, чтобы Step не тащил весь CellSize.</summary>
            public float CellHeight;

            /// <summary>
            /// Можно ли идти по диагонали, или это срезка угла сквозь породу.
            ///
            /// Спрашивается про ВОЗДУХ в обеих смежных колонках, а не про пол в них.
            /// Пол — требование не то: у соседней колонки его запросто нет в пределах
            /// клетки (там уступ, или ход идёт над пустотой), а пройти по диагонали
            /// при этом ничто не мешает. Замер на этом и споткнулся: дно лестницы
            /// упиралось в диагональ, у которой смежные клетки были открыты,
            /// но полом не размечены, и нижний этаж оставался отрезанным.
            ///
            /// Породу же это по-прежнему не пропускает: угол из камня не открыт
            /// по определению.
            /// </summary>
            public bool DiagonalClear(int3 from, int2 direction, int toIndex)
            {
                if (direction.x == 0 || direction.y == 0) return true;

                // Уровень, на котором особь проходит угол, лежит где-то между полом
                // откуда и полом куда. Проверять только уровень источника мало:
                // при спуске угол открыт уже ниже, и честный проход отвергался.
                var toY = toIndex / Dim.x % Dim.y;

                return SideOpen(from.x + direction.x, from.z, from.y, toY) &&
                       SideOpen(from.x, from.z + direction.y, from.y, toY);
            }

            private bool SideOpen(int x, int z, int yA, int yB)
            {
                if (x < 0 || z < 0 || x >= Dim.x || z >= Dim.z) return false;

                var low = math.min(yA, yB);
                var high = math.max(yA, yB);

                for (var y = low; y <= high; y++)
                {
                    if (Open[Index(new int3(x, y, z))] != 0) return true;
                }

                return false;
            }
        }

        /// <summary>Насколько особь лезет вверх и спускается вниз сверх обычного шага, юниты.</summary>
        public float ClimbHeight { get; set; } = 5f;

        public Topology GetTopology() => new Topology
        {
            Walkable = Walkable,
            FloorY = FloorY,
            Open = Open,
            Dim = Dim,
            MaxStep = AgentStep,
            MaxClimb = ClimbHeight,
            CellHeight = CellSize
        };

        /// <summary>
        /// Разбор границы достижимого: что именно отделяет дошедшую часть уровня
        /// от недошедшей.
        ///
        /// Нужен потому, что общая доля достижимости говорит только «плохо», а причин
        /// у этого ровно две и лечатся они по-разному: либо ступенька между соседними
        /// клетками выше, чем особь берёт, либо клетки вообще не соседи — разрыв
        /// шире одной клетки, и его надо лечить размером клетки, а не высотой шага.
        /// </summary>
        public string DescribeFrontier()
        {
            if (!IsCreated) return "  граница: поле не построено";

            const int search = 6;
            const int band = 8;

            var bands = (Dim.y + band - 1) / band;

            var count = new int[bands];
            var gap = new float[bands];
            var delta = new float[bands];
            var where = new float3[bands];

            for (var i = 0; i < bands; i++) gap[i] = float.MaxValue;

            for (var i = 0; i < Walkable.Length; i++)
            {
                if (Walkable[i] == 0 || Distance[i] != Unreachable) continue;

                var c = new int3(i % Dim.x, i / Dim.x % Dim.y, i / (Dim.x * Dim.y));
                var slot = c.y / band;

                count[slot]++;

                var here = CellFloorPoint(i);

                // Насколько близко недостижимая часть подходит к достижимой. Разрез
                // по полосам высоты нужен потому, что общий минимум по уровню всегда
                // мал — где-нибудь да найдётся пара соседних клеток.
                // по полосам высоты нужен потому, что общий минимум по уровню всегда
                // мал — где-нибудь да найдётся пара соседних клеток. Интересно другое:
                // что отделяет КАЖДЫЙ отрезанный кусок, и в первую очередь лестницы.
                for (var dz = -search; dz <= search; dz++)
                for (var dy = -search; dy <= search; dy++)
                for (var dx = -search; dx <= search; dx++)
                {
                    var t = c + new int3(dx, dy, dz);

                    if (math.any(t < 0) || math.any(t >= Dim)) continue;

                    var index = CellIndex(t);
                    if (Walkable[index] == 0 || Distance[index] == Unreachable) continue;

                    var there = CellFloorPoint(index);
                    var distance = math.distance(here, there);

                    if (distance >= gap[slot]) continue;

                    gap[slot] = distance;
                    delta[slot] = math.abs(there.y - here.y);
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
                    ? $"ближе всего подходит на {gap[i]:0.00} юнита " +
                      $"(перепад {delta[i]:0.00}) у ({where[i].x:0.0}, {where[i].y:0.0}, {where[i].z:0.0})"
                    : $"достижимого нет и на {search * CellSize:0.0} юнита вокруг");
            }

            text.AppendLine();
            text.Append($"    порог ступеньки сейчас {AgentStep:0.00}");

            return text.ToString();
        }

        /// <summary>Снимок полей для джобов движения: NativeArray передаются по значению.</summary>
        public FlowSampler Sampler => new FlowSampler
        {
            Walkable = Walkable,
            FloorY = FloorY,
            Flow = Flow,
            WallPush = WallPush,
            Dim = Dim,
            CellSize = CellSize
        };

        /// <summary>
        /// Чтение поля из джоба. Отдельная структура, потому что в джоб нельзя передать
        /// класс, а перечислять пять полей в каждом джобе — верный способ разойтись.
        /// </summary>
        public struct FlowSampler
        {
            [ReadOnly] public NativeArray<byte> Walkable;
            [ReadOnly] public NativeArray<float> FloorY;
            [ReadOnly] public NativeArray<float3> Flow;
            [ReadOnly] public NativeArray<float3> WallPush;

            public int3 Dim;
            public float CellSize;

            public int Index(int3 c) => (c.z * Dim.y + c.y) * Dim.x + c.x;

            public int3 CellOf(float3 position) =>
                math.clamp((int3)math.floor(position / CellSize), 0, Dim - 1);

            public bool IsWalkable(int3 c) =>
                math.all(c >= 0) && math.all(c < Dim) && Walkable[Index(c)] != 0;

            /// <summary>
            /// Клетка, в которой особь реально стоит.
            ///
            /// Нельзя просто взять клетку по координате: проходимой считается та, чей
            /// ЦЕНТР стоит над полом в пределах клетки, а особь стоит на самом полу —
            /// то есть у её нижней границы. При поле на высоте 2.5 и клетке в 1.5 юнита
            /// особь лежит в слое 1, а проходим слой 2, и запрос «проходима ли моя
            /// клетка» отвечает «нет» на совершенно нормальном месте.
            ///
            /// Это не мелочь: на этом вся толпа вставала колом там, где появилась, —
            /// проверка шага по осям отвергала любое движение.
            /// </summary>
            public bool ResolveCell(float3 position, out int3 cell)
            {
                var c = CellOf(position);

                // Порядок важен: сначала слой над особью (обычный случай), потом её
                // собственный, потом нижний — на случай, если она чуть провалилась.
                for (var dy = 1; dy >= -1; dy--)
                {
                    var probe = new int3(c.x, c.y + dy, c.z);

                    if (!IsWalkable(probe)) continue;

                    cell = probe;
                    return true;
                }

                cell = c;
                return false;
            }

            /// <summary>Есть ли под точкой проходимое место — с поправкой на слой, см. ResolveCell.</summary>
            public bool CanStand(float3 position) => ResolveCell(position, out _);

            /// <summary>
            /// Направление и высота пола с горизонтальным сглаживанием по четырём клеткам.
            ///
            /// Сглаживание идёт с весом по проходимости: непроходимая клетка в выборку
            /// не входит вовсе. Без этого её нулевое направление тянуло бы особь в стену
            /// ровно там, где она к стене ближе всего.
            /// </summary>
            public void SampleAt(float3 position, out float3 flow, out float3 wallPush, out float floorY,
                out bool valid)
            {
                flow = float3.zero;
                wallPush = float3.zero;
                floorY = position.y;
                valid = false;

                var g = position / CellSize - 0.5f;

                var baseCell = (int3)math.floor(g);
                var t = math.saturate(g - baseCell);

                var sumFlow = float3.zero;
                var sumPush = float3.zero;
                var sumFloor = 0f;
                var sumWeight = 0f;

                // Слой берём тот, в котором особь стоит, а не тот, куда попадает её
                // координата: иначе выборка идёт из соседнего этажа. См. ResolveCell.
                ResolveCell(position, out var standing);
                var y = standing.y;

                for (var dz = 0; dz <= 1; dz++)
                for (var dx = 0; dx <= 1; dx++)
                {
                    var c = new int3(baseCell.x + dx, y, baseCell.z + dz);

                    if (!IsWalkable(c)) continue;

                    var weight = (dx == 0 ? 1f - t.x : t.x) * (dz == 0 ? 1f - t.z : t.z);

                    if (weight <= 0f) continue;

                    var index = Index(c);

                    sumFlow += Flow[index] * weight;
                    sumPush += WallPush[index] * weight;
                    sumFloor += FloorY[index] * weight;
                    sumWeight += weight;
                }

                if (sumWeight <= 1e-4f) return;

                flow = math.normalizesafe(sumFlow / sumWeight);

                // Отжим от стен НЕ нормализуется: его длина и есть сила, с которой
                // тянет от камня. Нормализация сделала бы отжим у дальней стены таким же,
                // как вплотную к ней, и толпа в широком ходе жалась бы к оси.
                wallPush = sumPush / sumWeight;

                floorY = sumFloor / sumWeight;
                valid = true;
            }
        }

        /// <summary>Отладка: сколько клеток проходимо, сколько достижимо, сколько годно под спавн.</summary>
        public string Describe()
        {
            if (!IsCreated) return "поле потока не построено";

            var reachable = 0;
            for (var i = 0; i < Distance.Length; i++)
            {
                if (Distance[i] != Unreachable) reachable++;
            }

            return $"сетка {Dim.x}x{Dim.y}x{Dim.z} по {CellSize:0.0} юнита, " +
                   $"проходимо {WalkableCount}, достижимо от игрока {reachable}, " +
                   $"мест под спавн {(SpawnCells.IsCreated ? SpawnCells.Length : 0)}";
        }

        /// <summary>Гизмо проходимых клеток — чтобы глазами проверить, куда толпа вообще может попасть.</summary>
        public void DrawGizmos(Matrix4x4 localToWorld, float3 around, float radius)
        {
            if (!IsCreated) return;

            var previous = Gizmos.matrix;
            Gizmos.matrix = localToWorld;

            var size = Vector3.one * (CellSize * 0.25f);
            var radiusSq = radius * radius;

            for (var i = 0; i < Walkable.Length; i++)
            {
                if (Walkable[i] == 0) continue;

                var point = CellFloorPoint(i);
                if (math.lengthsq(point - around) > radiusSq) continue;

                var reachable = Distance[i] != Unreachable;

                Gizmos.color = reachable
                    ? new Color(0.2f, 0.9f, 0.4f, 0.5f)
                    : new Color(0.9f, 0.3f, 0.2f, 0.4f);

                Gizmos.DrawCube((Vector3)point, size);

                if (!reachable) continue;

                Gizmos.color = new Color(0.9f, 0.9f, 0.3f, 0.5f);
                Gizmos.DrawRay((Vector3)point, (Vector3)(Flow[i] * CellSize * 0.4f));
            }

            Gizmos.matrix = previous;
        }
    }
}
