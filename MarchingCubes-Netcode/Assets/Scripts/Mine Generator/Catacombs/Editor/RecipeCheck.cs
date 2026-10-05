using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Рецепты уровня (M4.7): каждая пара «ярус, глубина ≤ 4» на трёх сидах (грабли №13). Вопросы —
    /// связен ли уровень, нет ли тупиков, есть ли зал логова и стоит ли в нём Матка, сколько стоят
    /// генерация и сетка толпы и сколько сетка весит.
    ///
    /// Связность — заливкой БЕЗ предела дальности: игровая заливка обрезана `floodSteps`, и на карте
    /// 6×4×6 дальний угол за пределом выглядел бы недостижимым, хотя связан (предел — про стоимость,
    /// не про связность). По полю плотности, не физикой (грабли №1).
    ///
    /// Рецепт кладётся в КОПИЮ ассета настроек, как в игре (MetaGame): правка самого ассета осела бы
    /// в файле. В конце мир возвращается к своим настройкам и сиду — прочие прогоны ждут их.
    /// </summary>
    public static class RecipeCheck
    {
        private static readonly int[] Seeds = { 1337, 2, 777 };
        private const int MaxDepth = 4;

        public static void Run()
        {
            var code = 0;

            try
            {
                code = Report();
            }
            catch (Exception error)
            {
                Debug.LogError("ПРОГОН РЕЦЕПТОВ УПАЛ: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        public static int Report()
        {
            var text = new StringBuilder();
            var failed = 0;
            var clock = Stopwatch.StartNew();

            var scene = CheckScene.Open(text, ref failed);
            if (scene == null) return Fail(text, failed + 1);

            var original = scene.World.CurrentSeed;
            var baseSettings = scene.World.Settings;
            var copies = new List<CatacombSettings>();

            // Пары с одинаковой картой (рецепт отличается только светом и длиной стадии) дают тот же
            // уровень на том же сиде — мерить их второй раз значит мерить то же самое.
            var measured = new Dictionary<string, string>();
            var worstGrid = 0f;
            var worstMemory = 0f;
            var worstGenerate = 0f;

            try
            {
                for (var tier = 1; tier <= SortieDifficulty.MaxTier; tier++)
                {
                    for (var depth = 1; depth <= MaxDepth; depth++)
                    {
                        var recipe = LevelRecipe.For(tier, depth);
                        var key = MapKey(recipe);
                        var name = $"ярус {tier}, глубина {depth}";

                        text.AppendLine();
                        text.AppendLine($"=== {name}: {recipe.ChunksX}×{recipe.ChunksY}×{recipe.ChunksZ} чанков, этажей {recipe.LevelCount}, " +
                                        $"узлов {recipe.RoomsMin}–{recipe.RoomsMax}, галерей {recipe.GalleryShare:0.00}, щелей {recipe.PinchChance:0.00}, " +
                                        $"лампы ×{recipe.LampScale:0.00}, паутина ×{recipe.WebScale:0.00}, стадия {CheckScene.Clock(recipe.StageSeconds)} ===");

                        if (measured.TryGetValue(key, out var same))
                        {
                            text.AppendLine($"  карта та же, что у пары «{same}» — уровень на тех же сидах тот же");
                            continue;
                        }

                        measured[key] = name;

                        foreach (var seed in Seeds)
                        {
                            var copy = Object.Instantiate(baseSettings);
                            copy.hideFlags = HideFlags.DontSave;
                            copy.ApplyRecipe(recipe);
                            copies.Add(copy);

                            scene.World.UseSettings(copy);
                            var m = Measure(scene, seed);

                            worstGrid = Mathf.Max(worstGrid, m.GridMs);
                            worstMemory = Mathf.Max(worstMemory, m.GridMb);
                            worstGenerate = Mathf.Max(worstGenerate, m.GenerateMs);

                            text.AppendLine($"  сид {seed,5}: чанков {m.Chunks}, генерация {m.GenerateMs:0} мс, сетка толпы {m.GridMs:0} мс, " +
                                            $"{m.GridMb:0.0} МБ ({m.Cells} клеток, проходимых {m.Walkable}); " +
                                            $"недостижимых {m.Unreachable}, тупиков {m.DeadEnds}, щелей {m.Pinches}, " +
                                            $"зал {(m.HasHall ? $"высотой {m.HallHeight:0.0}" : "НЕТ")}, логово {(m.LairInHall ? "в зале" : "НЕ В ЗАЛЕ")}");

                            var tag = $"{name}, сид {seed}";
                            CheckScene.Expect(m.Unreachable == 0, $"{tag}: связность сетки толпы 100%", text, ref failed);
                            CheckScene.Expect(m.DeadEnds == 0, $"{tag}: тупиков 0", text, ref failed);
                            CheckScene.Expect(m.HasHall && m.HallHeight >= 9f && m.LairInHall, $"{tag}: зал логова есть, Матка в нём", text, ref failed);
                        }
                    }
                }
            }
            finally
            {
                // Вернуть миру его настройки и уровень сцены.
                scene.World.UseSettings(baseSettings);
                scene.World.GenerateImmediate(original);
                scene.PlaceBody(scene.Entry);
                scene.Crowd.Rebuild();
                scene.Queen.Rebuild();

                foreach (var copy in copies) Object.DestroyImmediate(copy);
            }

            text.AppendLine();
            text.AppendLine($"Худшее: генерация {worstGenerate:0} мс, сетка толпы {worstGrid:0} мс, память сетки {worstMemory:0.0} МБ");
            text.AppendLine("  (в редакторе; в игре генерация идёт по кадрам с бюджетом, сетка — одним кадром на ярус)");

            // Сетка строится одним кадром на входе в ярус, под экраном загрузки; память — потолок для WebGL.
            CheckScene.Expect(worstMemory <= 40f, "память сетки толпы ≤ 40 МБ на самой большой карте", text, ref failed);

            text.AppendLine();
            text.AppendLine($"прогон занял {clock.Elapsed.TotalSeconds:0.0} с");

            if (failed > 0) return Fail(text, failed);

            Debug.Log($"Проверка рецептов пройдена:{Environment.NewLine}{text}");
            return 0;
        }

        private struct Metrics
        {
            public int Chunks;
            public float GenerateMs;
            public float GridMs;
            public float GridMb;
            public int Cells;
            public int Walkable;
            public int Unreachable;
            public int DeadEnds;
            public int Pinches;
            public bool HasHall;
            public float HallHeight;
            public bool LairInHall;
        }

        private static Metrics Measure(CheckScene scene, int seed)
        {
            var m = new Metrics();
            var world = scene.World;

            var watch = Stopwatch.StartNew();
            world.GenerateImmediate(seed);
            m.GenerateMs = (float)watch.Elapsed.TotalMilliseconds;
            m.Chunks = world.Chunks.Count;

            // Игрока — на вход ЭТОГО уровня (грабли №30).
            world.TryGetSpawnPoint(out var spawn);
            scene.PlaceBody(spawn);

            watch.Restart();
            scene.Crowd.Rebuild();
            m.GridMs = (float)watch.Elapsed.TotalMilliseconds;

            scene.Queen.Rebuild();

            var field = scene.Crowd.Field;
            m.Cells = field.CellCount;
            m.Walkable = field.WalkableCount;
            m.GridMb = MemoryOf(field) / (1024f * 1024f);

            // Заливка без предела — от входа; спавн-полоса здесь ни при чём.
            var local = (Unity.Mathematics.float3)world.transform.InverseTransformPoint(spawn);
            field.Rebuild(local, 6, 26, SpiderFlowField.Unreachable - 1);
            m.Unreachable = UnreachableCount(field);

            var layout = world.Layout;
            m.DeadEnds = layout.DeadEnds;
            m.Pinches = layout.Pinches.Count;

            m.HasHall = world.TryGetLairHall(out var hall, out m.HallHeight);
            m.LairInHall = m.HasHall && scene.Queen.HasLair &&
                           Mathf.Abs(scene.Queen.LairPosition.x - hall.x) < layout.LairHallSize.x * 0.5f &&
                           Mathf.Abs(scene.Queen.LairPosition.z - hall.z) < layout.LairHallSize.y * 0.5f;

            return m;
        }

        /// <summary>Ключ карты: всё, что влияет на планировку. Свет, паутина и длина стадии — нет.</summary>
        private static string MapKey(in LevelRecipe r) =>
            $"{r.ChunksX}x{r.ChunksY}x{r.ChunksZ}|{r.LevelCount}|{r.RoomsMin}-{r.RoomsMax}|{r.GalleryShare}|{r.PinchChance}|{r.PathChaos}";

        private static int UnreachableCount(SpiderFlowField field)
        {
            var unreachable = 0;

            for (var i = 0; i < field.Walkable.Length; i++)
            {
                if (field.Walkable[i] != 0 && field.Distance[i] == SpiderFlowField.Unreachable) unreachable++;
            }

            return unreachable;
        }

        /// <summary>Сколько весят нативные массивы сетки — все поля, и закрытые тоже (очередь заливки).</summary>
        private static long MemoryOf(SpiderFlowField field)
        {
            long bytes = 0;

            foreach (var info in typeof(SpiderFlowField).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var type = info.FieldType;
                if (!type.IsGenericType) continue;

                var definition = type.GetGenericTypeDefinition();
                var element = type.GetGenericArguments()[0];

                if (definition == typeof(NativeArray<>))
                {
                    var value = info.GetValue(field);
                    var created = (bool)type.GetProperty("IsCreated").GetValue(value);
                    if (!created) continue;

                    var length = (int)type.GetProperty("Length").GetValue(value);
                    bytes += (long)length * UnsafeUtility.SizeOf(element);
                }
                else if (definition == typeof(NativeList<>))
                {
                    var value = info.GetValue(field);
                    var created = (bool)type.GetProperty("IsCreated").GetValue(value);
                    if (!created) continue;

                    var capacity = (int)type.GetProperty("Capacity").GetValue(value);
                    bytes += (long)capacity * UnsafeUtility.SizeOf(element);
                }
            }

            return bytes;
        }

        private static int Fail(StringBuilder text, int failed)
        {
            Debug.LogError($"ПРОВЕРКА РЕЦЕПТОВ НЕ ПРОШЛА, проблемных пунктов {failed}:{Environment.NewLine}{text}");
            return 1;
        }
    }
}
