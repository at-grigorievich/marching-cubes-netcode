using System;
using System.Diagnostics;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Зал логова (M3.4) на четырёх сидах: грабли №13 — один сид ничего не значит.
    ///
    /// Зал раздувает один узел дальней трети вширь и ввысь, и вопросы такие: есть ли он, выше ли
    /// девяти юнитов (Матка ×2.4–3.0 в обычный узел высотой 4.5 не влезает), не разрезал ли он
    /// уровень и не оставил ли тупиков, и стоит ли логово Матки в нём. Связность — по сетке толпы
    /// (заливкой от игрока по полю плотности), не физикой (грабли №1).
    /// </summary>
    public static class LairCheck
    {
        private static readonly int[] Seeds = { 1337, 2, 777, 55555 };

        public static void Run()
        {
            var code = 0;

            try
            {
                code = Report();
            }
            catch (Exception error)
            {
                Debug.LogError("ПРОГОН ЗАЛА ЛОГОВА УПАЛ: " + error);
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

            text.AppendLine();
            text.AppendLine("=== Зал логова на четырёх сидах ===");

            foreach (var seed in Seeds)
            {
                scene.World.GenerateImmediate(seed);

                // Игрока — на вход ЭТОГО уровня: со входа прошлого сида он стоит в породе,
                // и заливка от него не находит ни одной клетки (грабли №30).
                scene.World.TryGetSpawnPoint(out var spawn);
                scene.PlaceBody(spawn);

                scene.Crowd.Rebuild();
                scene.Queen.Rebuild();

                var layout = scene.World.Layout;
                var hasHall = scene.World.TryGetLairHall(out var hall, out var height);
                var reachable = ReachableShare(scene.Crowd);
                var inHall = hasHall && scene.Queen.HasLair &&
                             Mathf.Abs(scene.Queen.LairPosition.x - hall.x) < layout.LairHallSize.x * 0.5f &&
                             Mathf.Abs(scene.Queen.LairPosition.z - hall.z) < layout.LairHallSize.y * 0.5f;

                scene.World.TryGetSpawnPoint(out var entry);

                text.AppendLine($"  сид {seed,6}: зал {(hasHall ? $"{layout.LairHallSize.x:0.0}×{layout.LairHallSize.y:0.0}×{height:0.0}" : "НЕТ")}, " +
                                $"от входа {(hasHall ? Vector3.Distance(entry, hall) : 0f):0} юнитов, тупиков {layout.DeadEnds}, " +
                                $"достижимо {reachable:P1}, логово {(inHall ? "в зале" : "НЕ В ЗАЛЕ")}");

                CheckScene.Expect(hasHall && height >= 9f, $"сид {seed}: зал есть, высота ≥ 9", text, ref failed);
                CheckScene.Expect(layout.DeadEnds == 0, $"сид {seed}: тупиков 0", text, ref failed);
                CheckScene.Expect(reachable >= 0.99f, $"сид {seed}: связность сетки толпы ≥ 99%", text, ref failed);
                CheckScene.Expect(inHall, $"сид {seed}: логово Матки в зале", text, ref failed);
            }

            // Вернуть уровень сцены — прочие прогоны ждут свой сид.
            scene.World.GenerateImmediate(original);
            scene.PlaceBody(scene.Entry);
            scene.Crowd.Rebuild();
            scene.Queen.Rebuild();

            text.AppendLine();
            text.AppendLine($"прогон занял {clock.Elapsed.TotalSeconds:0.0} с");

            if (failed > 0) return Fail(text, failed);

            Debug.Log($"Проверка зала логова пройдена:{Environment.NewLine}{text}");
            return 0;
        }

        /// <summary>Доля проходимых клеток сетки толпы, достижимых от игрока, — как в CatacombCrowdCheck.</summary>
        private static float ReachableShare(SpiderCrowd crowd)
        {
            var field = crowd.Field;
            var walkable = 0;
            var reachable = 0;

            for (var i = 0; i < field.Walkable.Length; i++)
            {
                if (field.Walkable[i] == 0) continue;

                walkable++;
                if (field.Distance[i] != SpiderFlowField.Unreachable) reachable++;
            }

            return walkable == 0 ? 0f : reachable / (float)walkable;
        }

        private static int Fail(StringBuilder text, int failed)
        {
            Debug.LogError($"ПРОВЕРКА ЗАЛА ЛОГОВА НЕ ПРОШЛА, проблемных пунктов {failed}:{Environment.NewLine}{text}");
            return 1;
        }
    }
}
