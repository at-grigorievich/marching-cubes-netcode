using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Прогон пушек (M2): форма поражения каждой на раскладке в прямом ходе, ни одного попадания
    /// сквозь стенку, убийства приписаны своему источнику, цена выстрела, таблица DPS по толпе
    /// и по одиночке на уровнях 1 и 5 — вход для баланса (M6).
    ///
    /// Толпа стоит на месте (<see cref="SpiderCrowd.StepStatic"/>): побеги она к игроку, замер
    /// формы мерил бы её бег. Глаза — низко над полом: пауки на камне, и луч на высоте глаз
    /// стоящего прошёл бы над мелочью — это вопрос прицеливания игрока, а не формы удара.
    /// </summary>
    public static class WeaponCheck
    {
        private const float Step = 1f / 30f;
        private const float EyeHeight = 0.55f;
        private const float Tough = 1000f;

        private static CheckScene _scene;
        private static PlayerArsenal _arsenal;
        private static SpiderCrowd _crowd;

        private static Vector3 _floor;
        private static Vector3 _forward;

        public static void Run()
        {
            var code = 0;

            try
            {
                code = Report();
            }
            catch (Exception error)
            {
                Debug.LogError("ПРОГОН ПУШЕК УПАЛ: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        public static int Report()
        {
            var text = new StringBuilder();
            var failed = 0;
            var clock = Stopwatch.StartNew();

            _scene = CheckScene.Open(text, ref failed);
            if (_scene == null) return Fail(text, failed + 1);

            _crowd = _scene.Crowd;
            _arsenal = _scene.Player.GetComponent<PlayerArsenal>();

            if (_arsenal == null)
            {
                text.AppendLine("  ПЛОХО: нет PlayerArsenal — «Починить свет» его не создал");
                return Fail(text, failed + 1);
            }

            _arsenal.Prepare();
            _arsenal.Context.Audio.Muted = true;

            if (!FindCorridor(18f, out _floor, out _forward))
            {
                text.AppendLine("  ПЛОХО: не нашлось прямого хода в 18 юнитов — раскладку ставить некуда");
                return Fail(text, failed + 1);
            }

            text.AppendLine($"  прямой ход: от ({_floor.x:0}, {_floor.y:0}, {_floor.z:0}) " +
                            $"по ({_forward.x:0.00}, {_forward.z:0.00})");

            failed += Shapes(text);
            failed += Walls(text);
            failed += Attribution(text);
            failed += DpsTable(text);

            text.AppendLine();
            text.AppendLine($"прогон занял {clock.Elapsed.TotalSeconds:0.0} с");

            _arsenal.ClearLoadout();

            if (failed > 0) return Fail(text, failed);

            Debug.Log($"Проверка пушек пройдена:{Environment.NewLine}{text}");
            return 0;
        }

        // ------------------------------------------------------------------ раскладка

        /// <summary>
        /// Прямой ход: точка пола и горизонтальное направление со свободной линией не короче
        /// <paramref name="length"/> на высоте низких глаз. Ищется по полу у входа.
        /// </summary>
        private static bool FindCorridor(float length, out Vector3 floor, out Vector3 forward)
        {
            floor = default;
            forward = Vector3.forward;

            var points = new List<Vector3>();
            _crowd.PickFloorPoints(_scene.Entry + Vector3.up, 60, 0, 80, null, points);
            points.Insert(0, _scene.Entry);

            var ctx = _arsenal.Context;

            foreach (var point in points)
            {
                for (var k = 0; k < 16; k++)
                {
                    var angle = k * Mathf.PI / 8f;
                    var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    var eye = point + Vector3.up * EyeHeight;

                    if (ctx.RayToRock(eye, direction, length, out _)) continue;

                    // Пол по всей длине: пауки должны стоять на камне вдоль линии, а не висеть над ямой.
                    var grounded = true;

                    for (var d = 2f; d <= length && grounded; d += 2f)
                    {
                        grounded = ctx.RayToRock(eye + direction * d, Vector3.down, 2f, out _);
                    }

                    if (!grounded) continue;

                    floor = point;
                    forward = direction;
                    return true;
                }
            }

            return false;
        }

        private static void Aim(Vector3 target)
        {
            var eye = _floor + Vector3.up * EyeHeight;

            _scene.Player.transform.position = eye;
            _scene.Player.transform.rotation = Quaternion.LookRotation((target - eye).normalized, Vector3.up);
        }

        private static void AimForward() => Aim(_floor + Vector3.up * EyeHeight + _forward * 10f);

        private static Vector3 Ahead(float distance, float side = 0f) =>
            _floor + _forward * distance + Vector3.Cross(Vector3.up, _forward) * side + Vector3.up * 0.3f;

        private static int Put(Vector3 point, float health = Tough)
        {
            var slot = _crowd.SpawnAt(point, _crowd.HeaviestKind, health);
            _crowd.StepStatic(0f);
            return slot;
        }

        private static Weapon Equip(WeaponId id, int level = 1)
        {
            _arsenal.ClearLoadout();
            _arsenal.Give(id, level);
            _crowd.ClearAll();
            _crowd.StepStatic(0f);

            // Смена пушки — 0.15 с: взятая в руки стреляет не сразу.
            Wait(PlayerArsenal.SwitchTime + Step);

            return _arsenal.Get(id);
        }

        /// <summary>Держать огонь (или альт) столько секунд, шагая всё, что движется само.</summary>
        private static void Hold(float seconds, bool fire = true, bool alt = false, bool releaseAtEnd = true)
        {
            var first = true;

            for (var t = 0f; t < seconds; t += Step)
            {
                var commands = PlayerCommands.Empty;
                commands.FireHeld = fire;
                commands.FirePressed = fire && first;
                commands.AltHeld = alt;
                commands.AltPressed = alt && first;

                Advance(commands);
                first = false;
            }

            if (!releaseAtEnd) return;

            for (var i = 0; i < 3; i++) Advance(PlayerCommands.Empty);
        }

        private static void Tap(bool alt = false)
        {
            var commands = PlayerCommands.Empty;
            commands.FireHeld = !alt;
            commands.FirePressed = !alt;
            commands.AltHeld = alt;
            commands.AltPressed = alt;

            Advance(commands);
            Advance(PlayerCommands.Empty);
        }

        private static void Wait(float seconds)
        {
            for (var t = 0f; t < seconds; t += Step) Advance(PlayerCommands.Empty);
        }

        private static void Advance(PlayerCommands commands)
        {
            _arsenal.Tick(commands, Step, true);
            _arsenal.Context.Projectiles.Tick(Step);
            _crowd.StepStatic(Step);
            _crowd.FlushKills();
        }

        private static float Lost(int slot, float before)
        {
            var now = _crowd.HealthAt(slot);
            return now < 0f ? before : before - now;
        }

        // ------------------------------------------------------------------ формы

        private static int Shapes(StringBuilder text)
        {
            var failed = 0;
            var full = CombatUnits.Hits(Tough);

            text.AppendLine();
            text.AppendLine("=== Формы поражения ===");

            // 1. Гранатомёт: взрыв у первой цели задевает соседа в трёх юнитах и не задевает в семи.
            {
                Equip(WeaponId.Launcher);
                var a = Put(Ahead(8f));
                var b = Put(Ahead(10f));
                var c = Put(Ahead(16f));

                Aim(_crowd.PositionAt(a));
                Tap();
                Wait(1f);

                var la = Lost(a, full);
                var lb = Lost(b, full);
                var lc = Lost(c, full);

                text.AppendLine($"  гранатомёт: у цели −{la:0}, в 2 юнитах за ней −{lb:0}, в 8 юнитах −{lc:0}");
                CheckScene.Expect(la > 0f && lb > 0f && lc <= 0f, "взрыв бьёт в радиусе 5 и не дальше", text, ref failed);
            }

            // 2. Револьвер: пять в линию — ровно три пробиты, урон убывает.
            {
                Equip(WeaponId.Revolver);
                var slots = new[] { 4f, 6f, 8f, 10f, 12f }.Select(d => Put(Ahead(d))).ToArray();

                Aim(_crowd.PositionAt(slots[4]));

                var rest = slots.Select(s => _crowd.HealthAt(s)).ToArray();
                Tap();
                Wait(0.2f);

                var lost = slots.Select((s, i) => Lost(s, rest[i])).ToArray();
                var hit = lost.Count(l => l > 0f);

                text.AppendLine($"  револьвер: пятеро в линию, урон {string.Join(" / ", lost.Select(l => l.ToString("0")))}");
                CheckScene.Expect(hit == 3, "луч пробивает ровно трёх", text, ref failed);
                CheckScene.Expect(lost[0] > lost[1] && lost[1] > lost[2], "каждый следующий слабее", text, ref failed);
            }

            // 3. Дробовик: двое на оси бьются, третий — нет, когда ствол отвёрнут от него на 45°.
            // Поворачивается прицел, а не паук: точка «3 вперёд, 3 вбок» в ходе шириной 3.5 лежит
            // в стене, и паук садился в ближайшую проходимую клетку — после правки разметки сетки
            // (M4) это клетка в углу пола и стены, в 26° от оси, внутри конуса.
            {
                Equip(WeaponId.Shotgun);
                var near = Put(Ahead(3f));
                var far = Put(Ahead(8f));

                AimForward();
                Tap();
                Wait(0.2f);

                var ln = Lost(near, full);
                var lf = Lost(far, full);

                _crowd.ClearAll();
                var side = Put(Ahead(3f));
                var eye = _floor + Vector3.up * EyeHeight;
                Aim(eye + Quaternion.Euler(0f, 45f, 0f) * _forward * 10f);

                // Выстрел дробовика — не чаще 1.9 в секунду: ждать, пока он снова готов.
                Wait(0.6f);
                Tap();
                Wait(0.2f);

                var ls = Lost(side, full);

                text.AppendLine($"  дробовик: в 3 юнитах −{ln:0}, в 8 −{lf:0}, сбоку на 45° −{ls:0}");
                CheckScene.Expect(ln > 0f && lf > 0f && ls <= 0f, "конус бьёт по оси и не бьёт вбок", text, ref failed);
                CheckScene.Expect(ln > lf, "в упор сильнее, чем вдали", text, ref failed);
            }

            // 4. Гвоздомёт: очередь замедляет и ранит.
            {
                Equip(WeaponId.Nailgun);
                var target = Put(Ahead(8f));

                Aim(_crowd.PositionAt(target));
                Hold(0.6f, true, false, false);

                _crowd.TryGetStatus(target, out var status);
                var lost = Lost(target, full);

                Wait(0.1f);

                text.AppendLine($"  гвоздомёт: за 0.6 с −{lost:0} HP, замедление ×{(status.Slow > 0f ? status.SlowScale : 1f):0.00}");
                CheckScene.Expect(lost > 0f && status.Slow > 0f && status.SlowScale < 1f, "гвозди ранят и замедляют", text, ref failed);
            }

            // 5. Рельса: шестеро в линию — все.
            {
                Equip(WeaponId.Rail);
                var slots = new[] { 5f, 7f, 9f, 11f, 13f, 15f }.Select(d => Put(Ahead(d))).ToArray();

                Aim(_crowd.PositionAt(slots[5]));
                Hold(1.2f);
                Wait(0.2f);

                var hit = slots.Count(s => Lost(s, full) > 0f);

                text.AppendLine($"  рельса: из шести в линию задеты {hit}");
                CheckScene.Expect(hit == 6, "луч рельсы пробивает всех", text, ref failed);
            }

            // 6. Огнемёт: в четырёх юнитах горит, в девяти — нет.
            {
                Equip(WeaponId.Flamer);
                var near = Put(Ahead(4f));
                var far = Put(Ahead(9.5f));

                AimForward();
                Hold(0.5f, true, false, false);

                _crowd.TryGetStatus(near, out var burning);
                _crowd.TryGetStatus(far, out var cold);

                Wait(0.1f);

                text.AppendLine($"  огнемёт: в 4 юнитах горит {burning.Burn:0.0} с, в 9.5 — {cold.Burn:0.0} с");
                CheckScene.Expect(burning.Burn > 0f && cold.Burn <= 0f && Lost(far, full) <= 0f,
                    "конус огня 7 юнитов: ближний горит, дальний нет", text, ref failed);
            }

            // 7. Тесла: восемь в цепочку — один разряд не длиннее пяти (захват + 4 прыжка).
            {
                Equip(WeaponId.Tesla);
                var slots = Enumerable.Range(0, 8).Select(i => Put(Ahead(6f + i * 2f))).ToArray();

                Aim(_crowd.PositionAt(slots[0]));
                Hold(Step * 2f, true, false, false);

                var hit = slots.Count(s => Lost(s, full) > 0f);
                Wait(0.1f);

                text.AppendLine($"  тесла: один разряд задел {hit} из 8 в цепочке");
                CheckScene.Expect(hit >= 2 && hit <= 5, "цепь прыгает, но не длиннее пяти", text, ref failed);
            }

            // 8. Кирка: трое впереди, один за спиной — дуга только спереди.
            {
                Equip(WeaponId.PickHammer);
                var front = new[] { Put(Ahead(1.8f)), Put(Ahead(2.2f, 0.8f)), Put(Ahead(2.2f, -0.8f)) };
                var behind = Put(Ahead(-2f));

                AimForward();
                Tap();
                Wait(0.6f);

                var hitFront = front.Count(s => Lost(s, full) > 0f);
                var lb = Lost(behind, full);

                text.AppendLine($"  кирка: впереди задеты {hitFront} из 3, за спиной −{lb:0}");
                CheckScene.Expect(hitFront >= 2 && lb <= 0f, "дуга бьёт впереди и не бьёт за спиной", text, ref failed);
            }

            return failed;
        }

        // ------------------------------------------------------------------ стенка

        private static int Walls(StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Сквозь стенку ===");

            var walls = FindThinWalls(40);
            var full = CombatUnits.Hits(Tough);
            var tested = 0;

            foreach (var (near, direction, behind, thickness) in walls)
            {
                // Взрыв гранаты вплотную к стенке: паук за ней в радиусе, но ходами далеко.
                Equip(WeaponId.Launcher);

                var slot = Put(behind);
                var origin = near - direction * 0.3f;

                // Паук сел туда, куда просили? Ближайшая проходимая клетка бывает и по эту
                // сторону тонкой стенки — тогда это не проверка стенки, а промах раскладки.
                if (slot < 0 || !Blocked(origin, _crowd.PositionAt(slot))) continue;

                // И сел не за углом: паук садится в ближайшую проходимую клетку, а не в точку,
                // и та бывает в нескольких шагах обхода от стены. Тогда взрыв достаёт его ходами
                // честно. Отсев — своей заливкой по сетке (`Field.Reach`), не той, что режет взрыв:
                // иначе проверка доказывала бы сама себя.
                if (NearByCorridor(origin, _crowd.PositionAt(slot), 7)) continue;

                var distance = (_crowd.PositionAt(slot) - origin).magnitude;
                _arsenal.Detonate(origin, -direction);

                var lost = Lost(slot, full);

                // Воронка копается ДО удара (куски ложатся на дно ямы), и яма у тонкого места
                // открывает короткий ход за перегородку — по пустоте, а не сквозь камень. Взрыв тогда
                // достаёт паука честно, и это уже не проверка стенки.
                if (NearByCorridor(origin, _crowd.PositionAt(slot), 7))
                {
                    text.AppendLine($"  перегородка {thickness:0.0} юнита: воронка открыла ход за неё (паук потерял {lost:0} HP) — не в счёт");
                    continue;
                }

                tested++;

                text.AppendLine($"  перегородка {thickness:0.0} юнита: взрыв у стенки, паук за ней в {distance:0.0} юнита " +
                                $"потерял {lost:0} HP");
                CheckScene.Expect(lost <= 0f, "взрыв не бьёт сквозь перегородку", text, ref failed);

                // Дробовик в стенку в упор — по тому же пауку: пушку меняем, раскладку нет.
                _arsenal.ClearLoadout();
                _arsenal.Give(WeaponId.Shotgun);
                Wait(PlayerArsenal.SwitchTime + Step);

                var saved = _floor;
                _floor = near - direction * 1.2f - Vector3.up * EyeHeight;
                Aim(_crowd.PositionAt(slot));

                var eye = _scene.Player.transform.position;
                var toSpider = _crowd.PositionAt(slot) - eye;

                // Перегородка бывает не до свода, и от глаз до паука прямая открыта поверх неё —
                // тогда дробь долетает честно, и это не проверка стенки.
                if (!_arsenal.Context.RayToRock(eye, toSpider.normalized, toSpider.magnitude - 1f, out _))
                {
                    _floor = saved;
                    continue;
                }

                var culled = _crowd.HiddenCulled;

                Tap();
                Wait(0.2f);
                _floor = saved;

                lost = Lost(slot, full);

                text.AppendLine($"  дробовик в стенку в упор: паук за ней потерял {lost:0} HP, " +
                                $"отсеяно невидимых {_crowd.HiddenCulled - culled}");
                CheckScene.Expect(lost <= 0f, "конус не бьёт сквозь перегородку", text, ref failed);

                if (tested >= 4) break;
            }

            // Пустая проверка не доказывает ничего: перегородка у входа на сиде сцены есть всегда.
            if (tested == 0) text.AppendLine("  перегородки тоньше двух с половиной юнитов с ходом за ней рядом со входом не нашлось");
            CheckScene.Expect(tested > 0, "нашлась хотя бы одна перегородка для проверки", text, ref failed);

            return failed;
        }

        private static readonly Dictionary<int, int> CorridorSteps = new Dictionary<int, int>();

        /// <summary>Есть ли по сетке путь от точки до клеток вокруг паука не длиннее steps шагов.</summary>
        private static bool NearByCorridor(Vector3 from, Vector3 spider, int steps)
        {
            var world = _scene.World.transform;
            var field = _crowd.Field;

            field.Reach((float3)world.InverseTransformPoint(from), steps, CorridorSteps);

            var local = (float3)world.InverseTransformPoint(spider);
            var cell = (int3)math.floor(local / field.CellSize);

            for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var c = cell + new int3(dx, dy, dz);
                if (math.any(c < 0) || math.any(c >= field.Dim)) continue;

                if (CorridorSteps.ContainsKey((c.z * field.Dim.y + c.y) * field.Dim.x + c.x)) return true;
            }

            return false;
        }

        /// <summary>Есть ли камень между точками — по полю плотности (грабли №1).</summary>
        private static bool Blocked(Vector3 from, Vector3 to)
        {
            var delta = to - from;
            var steps = Mathf.Max(2, Mathf.CeilToInt(delta.magnitude / 0.1f));

            for (var k = 1; k < steps; k++)
            {
                if (_scene.World.IsSolid(from + delta * (k / (float)steps))) return true;
            }

            return false;
        }

        /// <summary>
        /// Тонкие перегородки: от пола по горизонтали до стены, сквозь породу не толще двух
        /// с половиной юнитов — и за ней ход, до которого по ходам дальше восьми шагов.
        /// </summary>
        private static List<(Vector3, Vector3, Vector3, float)> FindThinWalls(int max)
        {
            var found = new List<(Vector3, Vector3, Vector3, float)>();

            var world = _scene.World;
            var ctx = _arsenal.Context;

            var points = new List<Vector3>();
            _crowd.PickFloorPoints(_scene.Entry + Vector3.up, 160, 0, 160, null, points);

            var steps = new Dictionary<int, int>();

            foreach (var point in points)
            {
                var eye = point + Vector3.up * 0.8f;

                for (var k = 0; k < 8 && found.Count < max; k++)
                {
                    var angle = k * Mathf.PI / 4f;
                    var dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

                    if (!ctx.RayToRock(eye, dir, 4f, out var hit)) continue;

                    var t = 0f;
                    var inside = hit.point + dir * 0.1f;

                    while (t < 2.6f && world.IsSolid(inside + dir * t)) t += 0.1f;

                    if (t >= 2.5f || t < 0.3f) continue;

                    var far = inside + dir * (t + 0.6f);
                    if (world.IsSolid(far)) continue;

                    // Ходами — далеко ОТ ТОЧКИ ВЗРЫВА: иначе это не перегородка, а угол того же хода.
                    // Мерить от глаз нельзя: взрыв режется заливкой от стены, а не от глаз, и паук
                    // за углом мог быть дальше восьми шагов от глаз, но в шести от стены. Предел взрыва
                    // гранатомёта — радиус 5 в клетках плюс одна (MarkReach), здесь ещё одна про запас.
                    _crowd.Field.Reach((float3)world.transform.InverseTransformPoint(hit.point - dir * 0.3f), 7, steps);

                    var farCell = _crowd.Field.FindNearestWalkable((float3)world.transform.InverseTransformPoint(far), 2);
                    if (farCell < 0 || steps.ContainsKey(farCell)) continue;

                    found.Add((hit.point, dir, far, t + 0.1f));
                }

                if (found.Count >= max) break;
            }

            return found;
        }

        // ------------------------------------------------------------------ атрибуция и цена

        private static int Attribution(StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Убийства приписаны своей пушке, цена выстрела ===");

            for (var i = 0; i < (int)WeaponId.Count; i++)
            {
                var id = (WeaponId)i;
                var weapon = Equip(id, 5);

                var targets = new[] { Put(Ahead(2.5f), 1f), Put(Ahead(3f, 0.6f), 1f), Put(Ahead(3f, -0.6f), 1f) };
                var source = WeaponIds.SourceOf(id);
                var before = _crowd.KillsBy(source);

                Aim(_crowd.PositionAt(targets[0]));

                var clock = Stopwatch.StartNew();
                Hold(id == WeaponId.Rail ? 1.2f : 0.5f);
                var ms = clock.Elapsed.TotalMilliseconds;

                Wait(1.5f);

                var credited = _crowd.KillsBy(source) - before;
                var dead = targets.Count(s => _crowd.HealthAt(s) < 0f);

                text.AppendLine($"  {WeaponIds.Key(id),-9} ур.5: убито {dead} из 3, журнал толпы +{credited}, " +
                                $"пушка насчитала {weapon.Kills}; держать огонь — {ms:0.0} мс на всё");

                CheckScene.Expect(dead >= 1, $"{WeaponIds.Key(id)} убивает вплотную", text, ref failed);
                CheckScene.Expect(credited == dead && weapon.Kills == credited,
                    $"{WeaponIds.Key(id)}: убийства записаны на неё и только на неё", text, ref failed);
            }

            return failed;
        }

        // ------------------------------------------------------------------ DPS

        private static int DpsTable(StringBuilder text)
        {
            text.AppendLine();
            text.AppendLine("=== Урон в секунду (6 с огня, перезарядки включены) ===");
            text.AppendLine("  пушка       толпа ур.1  толпа ур.5  одиночка ур.1  одиночка ур.5");

            var failed = 0;
            const float seconds = 6f;

            for (var i = 0; i < (int)WeaponId.Count; i++)
            {
                var id = (WeaponId)i;
                var row = new float[4];

                for (var level = 0; level < 2; level++)
                {
                    var lvl = level == 0 ? 1 : 5;

                    // Толпа: три десятка живучих в куче впереди — на дистанции боя пушки
                    // (кирка бьёт на трёх юнитах, огнемёт на семи).
                    var near = id == WeaponId.PickHammer ? 1.6f : id == WeaponId.Flamer || id == WeaponId.Shotgun ? 3f : 6f;
                    var gap = id == WeaponId.PickHammer ? 0.35f : 1.2f;

                    var weapon = Equip(id, lvl);

                    for (var k = 0; k < 30; k++) Put(Ahead(near + (k % 5) * gap, (k / 5 - 2.5f) * 0.9f));

                    AimForward();
                    row[level] = Fire(weapon, id, seconds);

                    // Одиночка: один живучий на дистанции боя.
                    weapon = Equip(id, lvl);
                    var single = Put(Ahead(id == WeaponId.PickHammer ? 2f : id == WeaponId.Flamer ? 5f : 8f));

                    Aim(_crowd.PositionAt(single));
                    row[2 + level] = Fire(weapon, id, seconds);
                }

                text.AppendLine($"  {WeaponIds.Key(id),-10} {row[0],10:0} {row[1],11:0} {row[2],14:0} {row[3],14:0}");

                if (row[0] <= 0f || row[2] <= 0f) failed++;
            }

            if (failed > 0) text.AppendLine($"  ПЛОХО: пушек без урона: {failed}");

            return failed;
        }

        /// <summary>Огонь заданное время в своём ритме: рельса заряжает и отпускает, кирка бьёт сериями.</summary>
        private static float Fire(Weapon weapon, WeaponId id, float seconds)
        {
            var source = WeaponIds.SourceOf(id);
            var before = weapon.DamageDealt + _crowd.BurnDamageBy(source);

            if (id == WeaponId.Rail || id == WeaponId.PickHammer)
            {
                for (var t = 0f; t < seconds;)
                {
                    var hold = id == WeaponId.Rail ? 1f : Step * 2f;

                    Hold(hold);
                    t += hold + Step * 3f;

                    Wait(id == WeaponId.Rail ? 0.2f : 0.1f);
                    t += id == WeaponId.Rail ? 0.2f : 0.1f;
                }
            }
            else
            {
                Hold(seconds);
            }

            Wait(0.5f);

            return (weapon.DamageDealt + _crowd.BurnDamageBy(source) - before) / seconds;
        }

        private static int Fail(StringBuilder text, int failed)
        {
            Debug.LogError($"ПРОВЕРКА ПУШЕК НЕ ПРОШЛА, проблемных пунктов {failed}:{Environment.NewLine}{text}");
            return 1;
        }
    }
}
