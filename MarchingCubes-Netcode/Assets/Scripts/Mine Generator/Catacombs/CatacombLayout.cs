using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Единственный примитив планировки — параллелепипед со скруглёнными рёбрами.
    /// Залы, коридоры, площадки и пандусы это всё он, отличаются только габаритами
    /// и поворотом. Капсулы из первой версии давали «кишки», прямоугольник со
    /// скруглением читается как рукотворный ход.
    /// </summary>
    public struct CaveBox
    {
        public float3 Center;
        public float3 HalfExtents;
        public float Round;
        public quaternion Rotation;

        /// <summary>Радиус описанной сферы — для отсечения и ранних выходов.</summary>
        public float BoundRadius => math.length(HalfExtents);
    }

    /// <summary>
    /// Планировка катакомб в локальных координатах мира [0, WorldSize].
    ///
    /// Всё выравнивается по планировочной сетке, коридоры идут строго по осям и
    /// поворачивают под прямым углом, спуски между этажами — серпантин с площадками.
    /// Считается на main-thread: примитивов сотни, это доли миллисекунды.
    /// </summary>
    public sealed class CatacombLayout : IDisposable
    {
        public NativeArray<CaveBox> Boxes;

        /// <summary>Центры полов залов по уровням — точки спавна волн и лута.</summary>
        public readonly List<List<Vector3>> RoomsByLevel = new List<List<Vector3>>();

        /// <summary>Центры залов верхнего уровня — кандидаты на точку входа игрока.</summary>
        public readonly List<Vector3> SpawnPoints = new List<Vector3>();

        /// <summary>Узкое место: середина, ось хода и длина сужения.</summary>
        public struct Pinch
        {
            public Vector3 Center;
            public Vector3 Direction;
            public float Length;
        }

        /// <summary>
        /// Узкие места. Нужны не игре, а проверке: щель шириной около двух юнитов шум стен
        /// может закрыть насовсем, и уровень развалится на несвязные куски.
        ///
        /// Ось хранится не для красоты. Проверять проходимость точкой нельзя: внутри
        /// сплошной породы коллайдеров нет вовсе, они есть только на поверхностях ходов,
        /// и любая проверка «свободно ли здесь» отвечает «да» в том числе внутри камня.
        /// Проходимость проверяется просвечиванием ВДОЛЬ оси: если между входом и выходом
        /// щели что-то есть, она закрыта.
        /// </summary>
        public readonly List<Pinch> Pinches = new List<Pinch>();

        public int Seed { get; private set; }
        public int LevelCount { get; private set; }

        /// <summary>Узлы, из которых ведёт ровно один ход. В игре про толпу это ловушки без контригры.</summary>
        public int DeadEnds { get; private set; }

        /// <summary>Среднее число ходов из узла. 2 — сплошная кишка, 3 и выше — сеть развилок.</summary>
        public float AverageDegree { get; private set; }

        public bool IsCreated => Boxes.IsCreated;

        public void Dispose()
        {
            if (Boxes.IsCreated) Boxes.Dispose();
            SpawnPoints.Clear();
            RoomsByLevel.Clear();
            Pinches.Clear();
        }

        private struct Room
        {
            public float3 Floor;
            public float2 Size;
        }

        public static CatacombLayout Build(CatacombSettings settings, int seed)
        {
            var layout = new CatacombLayout { Seed = seed };
            var rng = new Random(seed == 0 ? 1u : (uint)seed);

            var worldSize = (float3)settings.WorldSize;
            var grid = math.max(0.5f, settings.GridStep);

            var margin = settings.BoundaryThickness + settings.PrimitiveMargin;
            margin = math.min(margin, math.cmin(worldSize) * 0.3f);

            var boxes = new List<CaveBox>();

            // Пол верхнего этажа опускаем на высоту зала, чтобы потолок не пробил крышу мира.
            var topFloor = worldSize.y - margin - settings.RoomHeight;
            var bottomFloor = margin;

            var usable = math.max(0f, topFloor - bottomFloor);
            var perLevel = settings.RoomHeight + 3f;

            // Если этажей больше, чем влезает по высоте, они сольются в кашу — режем количество.
            var levelCount = math.clamp((int)math.floor(usable / perLevel) + 1, 1, settings.LevelCount);
            layout.LevelCount = levelCount;

            var levelFloor = new float[levelCount];
            for (var l = 0; l < levelCount; l++)
            {
                levelFloor[l] = levelCount == 1
                    ? (topFloor + bottomFloor) * 0.5f
                    : math.lerp(topFloor, bottomFloor, l / (float)(levelCount - 1));
            }

            var levelRooms = new List<List<Room>>();

            var totalNodes = 0;
            var totalEnds = 0;
            var deadEnds = 0;

            for (var l = 0; l < levelCount; l++)
            {
                var rooms = ScatterRooms(settings, ref rng, levelFloor[l], margin, worldSize, grid,
                    l, bottomFloor, topFloor);
                levelRooms.Add(rooms);

                foreach (var room in rooms) boxes.Add(MakeRoom(settings, room));

                var edges = ConnectLevel(settings, ref rng, boxes, rooms, layout, l, bottomFloor, topFloor);

                totalNodes += rooms.Count;
                totalEnds += CountEndpoints(edges, rooms.Count, ref deadEnds);
            }

            for (var l = 0; l < levelCount - 1; l++)
            {
                LinkLevels(settings, ref rng, boxes, levelRooms[l], levelRooms[l + 1], margin, worldSize, grid,
                    layout, l, bottomFloor, topFloor);
            }

            layout.DeadEnds = deadEnds;
            layout.AverageDegree = totalNodes > 0 ? totalEnds / (float)totalNodes : 0f;

            layout.Boxes = new NativeArray<CaveBox>(boxes.ToArray(), Allocator.Persistent);

            foreach (var rooms in levelRooms)
            {
                var centers = new List<Vector3>(rooms.Count);
                foreach (var room in rooms) centers.Add((Vector3)room.Floor);
                layout.RoomsByLevel.Add(centers);
            }

            if (layout.RoomsByLevel.Count > 0) layout.SpawnPoints.AddRange(layout.RoomsByLevel[0]);

            return layout;
        }

        private static List<Room> ScatterRooms(CatacombSettings settings, ref Random rng, float floor,
            float margin, float3 worldSize, float grid, int level, float bottomFloor, float topFloor)
        {
            var count = rng.NextInt(settings.RoomsPerLevel.x, settings.RoomsPerLevel.y + 1);
            var cellsPerAxis = math.max(1, (int)math.ceil(math.sqrt(count)));

            var cells = new List<int2>(cellsPerAxis * cellsPerAxis);
            for (var cx = 0; cx < cellsPerAxis; cx++)
            for (var cz = 0; cz < cellsPerAxis; cz++)
                cells.Add(new int2(cx, cz));

            for (var i = cells.Count - 1; i > 0; i--)
            {
                var j = rng.NextInt(0, i + 1);
                (cells[i], cells[j]) = (cells[j], cells[i]);
            }

            count = math.min(count, cells.Count);

            var minBound = new float2(margin, margin);
            var maxBound = new float2(worldSize.x - margin, worldSize.z - margin);
            var span = maxBound - minBound;

            var rooms = new List<Room>(count);

            for (var i = 0; i < count; i++)
            {
                var cell = cells[i];

                var size = new float2(
                    SnapTo(rng.NextFloat(settings.RoomSize.x, settings.RoomSize.y), grid),
                    SnapTo(rng.NextFloat(settings.RoomSize.x, settings.RoomSize.y), grid));

                size = math.max(size, grid);
                size = math.min(size, span * 0.9f);

                var u = new float2(
                    (cell.x + rng.NextFloat(0.25f, 0.75f)) / cellsPerAxis,
                    (cell.y + rng.NextFloat(0.25f, 0.75f)) / cellsPerAxis);

                var center = minBound + u * span;
                center = SnapTo(center, grid);

                // Зал целиком внутри мира: иначе его срежет запечатанная граница.
                center = math.clamp(center, minBound + size * 0.5f, maxBound - size * 0.5f);

                rooms.Add(new Room { Floor = new float3(center.x, FloorAt(settings, center, level, floor, bottomFloor, topFloor), center.y), Size = size });
            }

            var arenas = math.min(settings.ArenasPerLevel, rooms.Count);
            for (var i = 0; i < arenas; i++)
            {
                var pick = rng.NextInt(0, rooms.Count);
                var room = rooms[pick];

                var scaled = math.min(SnapTo(room.Size * settings.ArenaScale, grid), span * 0.9f);
                var center = math.clamp(room.Floor.xz, minBound + scaled * 0.5f, maxBound - scaled * 0.5f);

                rooms[pick] = new Room
                {
                    Floor = new float3(center.x, FloorAt(settings, center, level, floor, bottomFloor, topFloor), center.y),
                    Size = scaled
                };
            }

            return rooms;
        }

        private static int CountEndpoints(List<int2> edges, int nodeCount, ref int deadEnds)
        {
            if (nodeCount == 0) return 0;

            var degree = new int[nodeCount];
            foreach (var e in edges)
            {
                degree[e.x]++;
                degree[e.y]++;
            }

            foreach (var d in degree) if (d == 1) deadEnds++;

            return edges.Count * 2;
        }

        /// <summary>Остовное дерево гарантирует связность уровня, лишние рёбра дают кольца для кайтинга.</summary>
        private static List<int2> ConnectLevel(CatacombSettings settings, ref Random rng, List<CaveBox> boxes,
            List<Room> rooms, CatacombLayout layout, int level, float bottomFloor, float topFloor)
        {
            var count = rooms.Count;
            if (count < 2) return new List<int2>();

            var inTree = new bool[count];
            var best = new float[count];
            var bestFrom = new int[count];

            inTree[0] = true;
            for (var i = 1; i < count; i++)
            {
                best[i] = math.distancesq(rooms[0].Floor, rooms[i].Floor);
                bestFrom[i] = 0;
            }

            var edges = new List<int2>();

            for (var step = 1; step < count; step++)
            {
                var pick = -1;
                var pickDist = float.MaxValue;

                for (var i = 1; i < count; i++)
                {
                    if (inTree[i] || best[i] >= pickDist) continue;
                    pick = i;
                    pickDist = best[i];
                }

                if (pick < 0) break;

                inTree[pick] = true;
                edges.Add(new int2(bestFrom[pick], pick));

                for (var i = 1; i < count; i++)
                {
                    if (inTree[i]) continue;
                    var d = math.distancesq(rooms[pick].Floor, rooms[i].Floor);
                    if (d >= best[i]) continue;
                    best[i] = d;
                    bestFrom[i] = pick;
                }
            }

            var extra = Mathf.RoundToInt(settings.ExtraConnections * (count - 1));
            if (extra > 0)
            {
                var linked = new HashSet<int>();
                foreach (var e in edges) linked.Add(EdgeKey(e.x, e.y, count));

                var candidates = new List<KeyValuePair<float, int2>>();

                for (var a = 0; a < count; a++)
                for (var b = a + 1; b < count; b++)
                {
                    if (linked.Contains(EdgeKey(a, b, count))) continue;
                    candidates.Add(new KeyValuePair<float, int2>(
                        math.distancesq(rooms[a].Floor, rooms[b].Floor), new int2(a, b)));
                }

                candidates.Sort((l, r) => l.Key.CompareTo(r.Key));

                for (var i = 0; i < math.min(extra, candidates.Count); i++) edges.Add(candidates[i].Value);
            }

            EnsureMinimumDegree(settings, rooms, edges);

            // Галереи выбираем из самых длинных связей: смысл галереи в дистанции, а короткая
            // прямая её не даёт. Из длинных берём долю, остальные остаются ломаными —
            // иначе уровень снова станет чертежом по линейке.
            var gallery = PickGalleries(settings, ref rng, rooms, edges);

            for (var i = 0; i < edges.Count; i++)
            {
                RouteHorizontal(settings, ref rng, boxes, rooms[edges[i].x].Floor, rooms[edges[i].y].Floor,
                    layout, level, bottomFloor, topFloor, gallery[i]);
            }

            return edges;
        }

        /// <summary>
        /// Добирает связи узлам, из которых ведёт меньше ходов, чем нужно.
        ///
        /// Без этого у остовного дерева остаются листья — тупики. В игре про толпу зомби
        /// тупик это смерть без контригры, поэтому по умолчанию требуем минимум два хода.
        /// </summary>
        private static void EnsureMinimumDegree(CatacombSettings settings, List<Room> rooms, List<int2> edges)
        {
            var count = rooms.Count;
            var required = math.min(settings.MinConnections, count - 1);
            if (required < 1) return;

            var degree = new int[count];
            var linked = new HashSet<int>();

            foreach (var e in edges)
            {
                degree[e.x]++;
                degree[e.y]++;
                linked.Add(EdgeKey(e.x, e.y, count));
            }

            for (var a = 0; a < count; a++)
            {
                // Пересчитываем на каждом шаге: добавленное ребро поднимает степень обоим концам.
                while (degree[a] < required)
                {
                    var best = -1;
                    var bestScore = float.MaxValue;

                    for (var b = 0; b < count; b++)
                    {
                        if (b == a || linked.Contains(EdgeKey(a, b, count))) continue;

                        // Ближе и беднее связями — лучше: так тупики связываются друг с другом,
                        // а не всё сходится в один хаб.
                        var score = math.distancesq(rooms[a].Floor, rooms[b].Floor) * (1f + degree[b]);
                        if (score >= bestScore) continue;

                        bestScore = score;
                        best = b;
                    }

                    if (best < 0) break;

                    edges.Add(new int2(a, best));
                    linked.Add(EdgeKey(a, best, count));

                    degree[a]++;
                    degree[best]++;
                }
            }
        }

        /// <summary>
        /// Отмечает связи, которые станут стрелковыми галереями.
        ///
        /// Галерея — это прямой простреливаемый прогон, по которому игрок отступает с
        /// гранатомётом. Замер до их появления: вперёд простреливалось в среднем 8.5 юнита,
        /// а в четверти точек маршрута меньше 3.8 — то есть орда доходила вплотную раньше,
        /// чем граната успевала стать безопасной, и стрелять приходилось себе под ноги.
        /// </summary>
        private static bool[] PickGalleries(CatacombSettings settings, ref Random rng, List<Room> rooms,
            List<int2> edges)
        {
            var gallery = new bool[edges.Count];
            if (settings.GalleryShare <= 0f) return gallery;

            // Кандидаты — только достаточно длинные связи.
            var candidates = new List<KeyValuePair<float, int>>();

            for (var i = 0; i < edges.Count; i++)
            {
                var a = rooms[edges[i].x].Floor;
                var b = rooms[edges[i].y].Floor;

                // Длину меряем по самой длинной оси: галерея — это один прямой пролёт,
                // и простреливаться будет именно он, а не диагональ между узлами.
                var run = math.max(math.abs(a.x - b.x), math.abs(a.z - b.z));
                if (run < settings.GalleryMinLength) continue;

                candidates.Add(new KeyValuePair<float, int>(run, i));
            }

            if (candidates.Count == 0) return gallery;

            candidates.Sort((l, r) => r.Key.CompareTo(l.Key));

            var take = math.max(1, (int)math.round(candidates.Count * settings.GalleryShare));

            for (var i = 0; i < math.min(take, candidates.Count); i++) gallery[candidates[i].Value] = true;

            return gallery;
        }

        /// <summary>
        /// Ход между двумя узлами. Раньше это была строгая буква Г — два осевых пролёта
        /// и поворот на 90 градусов; уровень от этого читался как чертёж по линейке.
        /// Теперь это ломаная из нескольких колен, по-прежнему строго осевая (капсулы
        /// и косые ходы были отвергнуты раньше), но с обходами в сторону и с высотой,
        /// которая едет по той же волне, что и полы узлов.
        /// </summary>
        private static void RouteHorizontal(CatacombSettings settings, ref Random rng, List<CaveBox> boxes,
            float3 from, float3 to, CatacombLayout layout = null, int level = 0,
            float bottomFloor = float.MinValue, float topFloor = float.MaxValue, bool gallery = false)
        {
            if (gallery)
            {
                BuildGallery(settings, ref rng, boxes, from, to, layout);
                return;
            }

            var points = BuildPath(settings, ref rng, from, to, level, bottomFloor, topFloor);

            for (var i = 0; i < points.Count - 1; i++)
            {
                AddCorridorMaybePinched(settings, ref rng, boxes, points[i], points[i + 1], layout);
            }
        }

        /// <summary>
        /// Стрелковая галерея: длинный прямой пролёт плюс короткий подвод к дальнему узлу.
        ///
        /// Ни колен, ни сужений, ни волны пола — всё это режет линию взгляда, а галерея
        /// существует ровно ради неё. Длинный пролёт ставится первым и идёт по той оси,
        /// по которой узлы разнесены сильнее: он и будет простреливаемым.
        ///
        /// Шире и выше обычного хода: по ней отступают спиной, и в неё должна помещаться
        /// дуга гранаты. Заодно галерея этим отличается на глаз — в тесной сети ходов
        /// широкий прямой прогон сам работает ориентиром.
        /// </summary>
        private static void BuildGallery(CatacombSettings settings, ref Random rng, List<CaveBox> boxes,
            float3 from, float3 to, CatacombLayout layout)
        {
            var width = settings.CorridorWidth * settings.GalleryWidthScale;
            var height = settings.CorridorHeight * settings.GalleryHeightScale;

            var alongX = math.abs(to.x - from.x) >= math.abs(to.z - from.z);

            // Излом ставим так, чтобы длинный пролёт шёл от игрока, а не к нему.
            var corner = alongX
                ? new float3(to.x, from.y, from.z)
                : new float3(from.x, from.y, to.z);

            AddCorridor(settings, boxes, from, corner, width, height);

            var landing = new float3(to.x, from.y, to.z);
            AddCorridor(settings, boxes, corner, landing, width, height);

            // Перепад высоты между узлами добираем отдельным звеном обычного сечения:
            // наклонный пол в самой галерее укоротил бы линию взгляда.
            if (math.abs(to.y - from.y) > 0.01f) AddCorridor(settings, boxes, landing, to, width, height);
        }

        /// <summary>
        /// Ломаная по осям от одного пола до другого.
        ///
        /// Колен тем больше, чем выше PathChaos. Каждое колено — это осевой отрезок, поэтому
        /// прямоугольный язык уровня сохраняется: меняется только то, что путь больше
        /// не кратчайший. Обход в сторону (перелёт за габарит пары узлов) включается тем же
        /// параметром и даёт те самые «непрямые» ходы.
        /// </summary>
        private static List<float3> BuildPath(CatacombSettings settings, ref Random rng, float3 from, float3 to,
            int level, float bottomFloor, float topFloor)
        {
            var chaos = math.saturate(settings.PathChaos);

            // При нулевом хаосе поведение ровно прежнее: одно колено, буква Г.
            var corners = 1 + (int)math.round(chaos * 3f);

            var flat = new List<float2>(corners + 2) { from.xz };

            var current = from.xz;
            var target = to.xz;

            // Идём попеременно по X и по Z, каждый раз проходя случайную долю остатка.
            var alongX = rng.NextBool();

            for (var i = 0; i < corners; i++)
            {
                var remaining = target - current;

                var fraction = rng.NextFloat(0.35f, 0.75f);
                var step = alongX ? new float2(remaining.x * fraction, 0f) : new float2(0f, remaining.y * fraction);

                current += step;

                // Обход: уводим колено за пределы прямоугольника между узлами. Это и есть
                // главный источник непрямых ходов — без него ломаная всё равно монотонна
                // и читается как та же буква Г, только со ступеньками.
                if (rng.NextFloat() < chaos * 0.5f)
                {
                    var detour = rng.NextFloat(1f, 2.5f) * settings.GridStep * (rng.NextBool() ? 1f : -1f);
                    current += alongX ? new float2(0f, detour) : new float2(detour, 0f);
                }

                current = SnapTo(current, settings.GridStep);
                flat.Add(current);

                alongX = !alongX;
            }

            // Доводим до цели двумя осевыми пролётами, иначе последний отрезок пойдёт косо.
            flat.Add(new float2(target.x, current.y));
            flat.Add(target);

            // Убираем вырожденные и совпадающие точки: нулевой отрезок даёт бокс нулевой длины.
            var cleaned = new List<float2>(flat.Count) { flat[0] };
            for (var i = 1; i < flat.Count; i++)
            {
                if (math.distance(flat[i], cleaned[cleaned.Count - 1]) > 0.05f) cleaned.Add(flat[i]);
            }

            if (cleaned.Count < 2) cleaned.Add(target);

            // Высота: базовая интерполяция от узла к узлу плюс та же волна, что у полов.
            // Благодаря ей ход между двумя узлами одной высоты всё равно не плоский.
            var lengths = new float[cleaned.Count];
            var total = 0f;

            for (var i = 1; i < cleaned.Count; i++)
            {
                total += math.distance(cleaned[i], cleaned[i - 1]);
                lengths[i] = total;
            }

            var points = new List<float3>(cleaned.Count);

            for (var i = 0; i < cleaned.Count; i++)
            {
                float y;

                if (i == 0) y = from.y;
                else if (i == cleaned.Count - 1) y = to.y;
                else
                {
                    var t = total > 1e-3f ? lengths[i] / total : 0f;
                    var baseY = math.lerp(from.y, to.y, t);

                    // Волну на промежуточных коленах берём вполсилы: на полную ход начинает
                    // скакать вверх-вниз внутри одного пролёта.
                    var wave = Wave(settings, cleaned[i], level) * 0.5f;
                    y = math.clamp(baseY + wave, bottomFloor, topFloor);
                }

                points.Add(new float3(cleaned[i].x, y, cleaned[i].y));
            }

            // Уклон каждого звена держим в пределах уклона пандуса — иначе получится
            // стенка, на которую игрок не заберётся. Правим только промежуточные колена:
            // концы это полы узлов, их двигать нельзя.
            var maxSlope = math.max(0.05f, settings.RampSlope);

            for (var pass = 0; pass < 2; pass++)
            {
                for (var i = 1; i < points.Count - 1; i++)
                {
                    var prev = points[i - 1];
                    var next = points[i + 1];
                    var here = points[i];

                    var dPrev = math.distance(here.xz, prev.xz);
                    var dNext = math.distance(here.xz, next.xz);

                    var lo = math.max(prev.y - dPrev * maxSlope, next.y - dNext * maxSlope);
                    var hi = math.min(prev.y + dPrev * maxSlope, next.y + dNext * maxSlope);

                    if (lo > hi) { lo = (prev.y + next.y) * 0.5f; hi = lo; }

                    points[i] = new float3(here.x, math.clamp(here.y, lo, hi), here.z);
                }
            }

            return points;
        }

        /// <summary>
        /// Волна пола. Одна и та же функция для полов узлов и для колен ходов — иначе
        /// ход не сойдётся с узлом по высоте и на стыке появится ступенька.
        /// </summary>
        private static float Wave(CatacombSettings settings, float2 positionXZ, int level)
        {
            if (settings.FloorWave <= 0f) return 0f;

            // Смещение по уровню: без него все этажи получают одинаковый рельеф и волны
            // выстраиваются столбиками одна над другой.
            var offset = new float2(level * 37.1f, level * -19.7f);

            return noise.snoise((positionXZ + offset) * settings.FloorWaveScale) * settings.FloorWave;
        }

        private static float FloorAt(CatacombSettings settings, float2 centerXZ, int level, float floor,
            float bottomFloor, float topFloor)
        {
            return math.clamp(floor + Wave(settings, centerXZ, level), bottomFloor, topFloor);
        }

        /// <summary>Минимальная ширина щели в юнитах.</summary>
        ///
        /// Ниже этого уровень рискует развалиться: радиус капсулы игрока 0.4, шум стен
        /// съедает до NoiseAmplitude с каждой стороны, и щель в полтора юнита закрывается
        /// наглухо. Проверяется потом ещё и замером по PinchPoints.
        private const float MinPassableWidth = 2f;

        /// <summary>
        /// Звено хода. Иногда — с узким местом посередине: широкий кусок, щель, широкий кусок.
        ///
        /// Сделать щель отдельным узким боксом поверх широкого нельзя: поле плотности
        /// объединяется, и широкий ход уже выбрал породу — добавленный узкий бокс ничего
        /// не сузит. Сужать можно только тем, что широкие куски до щели не доходят.
        /// </summary>
        private static void AddCorridorMaybePinched(CatacombSettings settings, ref Random rng, List<CaveBox> boxes,
            float3 from, float3 to, CatacombLayout layout)
        {
            var length = math.distance(from, to);

            var pinchLength = math.max(1.5f, settings.CorridorWidth * 0.6f);

            // Запас по краям звена всего в полторы ширины хода. Было три, и при ломаном
            // пути почти ни одно звено не проходило по длине: щелей на весь уровень
            // получалось шесть штук, то есть ни одной на глаз.
            var needed = pinchLength + settings.CorridorWidth * 1.4f;

            if (settings.PinchChance <= 0f || length < needed || rng.NextFloat() >= settings.PinchChance)
            {
                AddCorridor(settings, boxes, from, to, settings.CorridorWidth, settings.CorridorHeight);
                return;
            }

            var half = pinchLength * 0.5f / length;
            var mid = rng.NextFloat(0.35f, 0.65f);

            var a = math.lerp(from, to, mid - half);
            var b = math.lerp(from, to, mid + half);

            var width = math.max(settings.CorridorWidth * settings.PinchWidth, MinPassableWidth);

            // Высоту щели тоже режем, но слабее: щель, в которую нужно пролезать боком,
            // а не ползком — иначе камера упирается в потолок и кадр чернеет.
            var height = math.max(settings.CorridorHeight * 0.8f, 2.2f);

            // Торцевого запаса у широких кусков со стороны щели быть не должно: он
            // перекрыл бы её целиком и сужения не получилось бы вовсе.
            AddCorridor(settings, boxes, from, a, settings.CorridorWidth, settings.CorridorHeight, 1f, 0f);
            AddCorridor(settings, boxes, a, b, width, height, 0f, 0f);
            AddCorridor(settings, boxes, b, to, settings.CorridorWidth, settings.CorridorHeight, 0f, 1f);

            if (layout != null)
            {
                var direction = math.normalizesafe(to - from);

                layout.Pinches.Add(new Pinch
                {
                    Center = (Vector3)math.lerp(from, to, mid),
                    Direction = (Vector3)direction,
                    Length = pinchLength
                });
            }
        }

        private static void LinkLevels(CatacombSettings settings, ref Random rng, List<CaveBox> boxes,
            List<Room> upper, List<Room> lower, float margin, float3 worldSize, float grid,
            CatacombLayout layout, int level, float bottomFloor, float topFloor)
        {
            if (upper.Count == 0 || lower.Count == 0) return;

            var links = math.min(settings.LinksBetweenLevels, math.min(upper.Count, lower.Count));

            var usedUpper = new HashSet<int>();
            var usedLower = new HashSet<int>();

            for (var link = 0; link < links; link++)
            {
                var bestA = -1;
                var bestB = -1;
                var bestDist = float.MaxValue;

                for (var a = 0; a < upper.Count; a++)
                {
                    if (usedUpper.Contains(a)) continue;

                    for (var b = 0; b < lower.Count; b++)
                    {
                        if (usedLower.Contains(b)) continue;

                        var d = math.distancesq(upper[a].Floor.xz, lower[b].Floor.xz);
                        if (d >= bestDist) continue;

                        bestDist = d;
                        bestA = a;
                        bestB = b;
                    }
                }

                if (bestA < 0 || bestB < 0) break;

                usedUpper.Add(bestA);
                usedLower.Add(bestB);

                BuildStairwell(settings, ref rng, boxes, upper[bestA], lower[bestB],
                    margin, worldSize, grid, layout, level, bottomFloor, topFloor);
            }
        }

        /// <summary>
        /// Серпантин: пролёты постоянного уклона с площадками на разворотах.
        /// Уклон задан явно, поэтому спуск всегда проходим пешком.
        /// </summary>
        private static void BuildStairwell(CatacombSettings settings, ref Random rng, List<CaveBox> boxes,
            Room topRoom, Room bottomRoom, float margin, float3 worldSize, float grid,
            CatacombLayout layout, int level, float bottomFloor, float topFloor)
        {
            var top = topRoom.Floor;
            var bottom = bottomRoom.Floor;

            var drop = top.y - bottom.y;
            if (drop <= 0.01f)
            {
                RouteHorizontal(settings, ref rng, boxes, top, bottom, layout, level, bottomFloor, topFloor);
                return;
            }

            var alongX = rng.NextBool();

            var axisMin = margin + settings.CorridorWidth;
            var axisMax = (alongX ? worldSize.x : worldSize.z) - margin - settings.CorridorWidth;

            if (axisMax <= axisMin)
            {
                RouteHorizontal(settings, ref rng, boxes, top, bottom, layout, level, bottomFloor, topFloor);
                return;
            }

            var axisValue = alongX ? top.x : top.z;
            var roomHalf = (alongX ? topRoom.Size.x : topRoom.Size.y) * 0.5f;

            var exitDirection = axisMax - axisValue >= axisValue - axisMin ? 1f : -1f;

            // Голову лестницы выносим за стену зала: если начать спуск из центра,
            // в полу зала получается неогороженная яма вместо входа на лестницу.
            var entryOffset = SnapTo(roomHalf + settings.CorridorWidth, grid);
            var entryAxis = math.clamp(axisValue + entryOffset * exitDirection, axisMin, axisMax);

            var entry = alongX
                ? new float3(entryAxis, top.y, SnapTo(top.z, grid))
                : new float3(SnapTo(top.x, grid), top.y, entryAxis);

            AddCorridor(settings, boxes, top, entry);
            AddLanding(settings, boxes, entry);

            // Направление пролётов выбираем заново — от головы лестницы, а не от зала.
            var spacePositive = axisMax - entryAxis;
            var spaceNegative = entryAxis - axisMin;

            var direction = spacePositive >= spaceNegative ? 1f : -1f;
            var available = math.max(spacePositive, spaceNegative);

            var run = math.clamp(settings.RampRun, grid, math.max(grid, available));

            var perLeg = math.max(0.01f, run * settings.RampSlope);
            var legs = math.clamp((int)math.ceil(drop / perLeg), 1, 16);

            // Один длинный ход вместо серпантина, если места хватает и так попросили.
            if (settings.LevelLinkMode == LevelLinkMode.StraightRamp)
            {
                var needed = drop / math.max(0.01f, settings.RampSlope);
                if (needed <= available)
                {
                    run = needed;
                    legs = 1;
                }
            }

            var previous = entry;

            for (var i = 1; i <= legs; i++)
            {
                var y = math.lerp(top.y, bottom.y, i / (float)legs);
                var offset = i % 2 == 1 ? run * direction : 0f;

                var point = alongX
                    ? new float3(entry.x + offset, y, entry.z)
                    : new float3(entry.x, y, entry.z + offset);

                AddCorridor(settings, boxes, previous, point);
                AddLanding(settings, boxes, point);

                previous = point;
            }

            RouteHorizontal(settings, ref rng, boxes, previous, bottom, layout, level, bottomFloor, topFloor);
        }

        private static CaveBox MakeRoom(CatacombSettings settings, Room room)
        {
            var height = settings.RoomHeight;
            var round = math.min(settings.CornerRounding,
                math.min(height, math.cmin(room.Size)) * 0.4f);

            return new CaveBox
            {
                Center = room.Floor + new float3(0f, height * 0.5f, 0f),
                HalfExtents = new float3(room.Size.x * 0.5f, height * 0.5f, room.Size.y * 0.5f),
                Round = math.max(0f, round),
                Rotation = quaternion.identity
            };
        }

        private static void AddLanding(CatacombSettings settings, List<CaveBox> boxes, float3 floor)
        {
            var width = settings.CorridorWidth * 1.6f;

            // Наклонный ход в вертикальном сечении выше своей высоты: h / cos(угла).
            // Площадка должна перекрывать его целиком, иначе устье пролёта пробивает
            // её потолок и в проёме остаётся ступенька.
            var slope = settings.RampSlope;
            var height = settings.CorridorHeight * math.sqrt(1f + slope * slope);

            boxes.Add(new CaveBox
            {
                Center = floor + new float3(0f, height * 0.5f, 0f),
                HalfExtents = new float3(width * 0.5f, height * 0.5f, width * 0.5f),
                Round = math.min(settings.CornerRounding, math.min(width, height) * 0.4f),
                Rotation = quaternion.identity
            });
        }

        /// <summary>
        /// Коридор между двумя точками пола. Наклонный ход получается тем же боксом,
        /// просто повёрнутым: его «пол» едет вдоль уклона.
        /// </summary>
        private static void AddCorridor(CatacombSettings settings, List<CaveBox> boxes, float3 fromFloor,
            float3 toFloor, float width = -1f, float height = -1f, float startOverlap = 1f, float endOverlap = 1f)
        {
            var delta = toFloor - fromFloor;
            var length = math.length(delta);
            if (length < 1e-3f) return;

            var forward = delta / length;
            var reference = math.abs(forward.y) > 0.99f ? new float3(0f, 0f, 1f) : new float3(0f, 1f, 0f);
            var rotation = quaternion.LookRotationSafe(forward, reference);

            if (width <= 0f) width = settings.CorridorWidth;
            if (height <= 0f) height = settings.CorridorHeight;


            // Запас по полширины с торцов нужен горизонтальным Г-поворотам, чтобы угол
            // сомкнулся без щели. Наклонному пролёту он вреден: снизу такой запас уходит
            // под пол площадки и оставляет ступеньку с острой кромкой, а сверху пробивает
            // потолок. На концах пролётов и так стоят площадки, они стык и закрывают.
            var tilted = math.abs(forward.y) > 0.05f;
            var overlap = tilted ? 0f : width * 0.5f;

            // Запасы с торцов теперь раздельные: у кусков, примыкающих к щели, запас
            // с её стороны обнуляется, иначе он щель и закроет.
            var startPad = overlap * math.saturate(startOverlap);
            var endPad = overlap * math.saturate(endOverlap);

            // Несимметричные запасы сдвигают середину бокса: длина выросла не поровну.
            var center = (fromFloor + toFloor) * 0.5f
                         + math.mul(rotation, new float3(0f, height * 0.5f, (endPad - startPad) * 0.5f));

            boxes.Add(new CaveBox
            {
                Center = center,
                HalfExtents = new float3(width * 0.5f, height * 0.5f, length * 0.5f + (startPad + endPad) * 0.5f),
                Round = math.min(settings.CornerRounding, math.min(width, height) * 0.4f),
                Rotation = rotation
            });
        }

        private static int EdgeKey(int a, int b, int count)
        {
            var lo = math.min(a, b);
            var hi = math.max(a, b);
            return lo * count + hi;
        }

        private static float SnapTo(float value, float step) => math.round(value / step) * step;
        private static float2 SnapTo(float2 value, float step) => math.round(value / step) * step;

    }
}
