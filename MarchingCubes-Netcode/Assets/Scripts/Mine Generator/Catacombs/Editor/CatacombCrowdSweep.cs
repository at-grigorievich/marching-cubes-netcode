using System;
using System.Diagnostics;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Перебор параметров сетки навигации по связности и цене.
    ///
    /// Заведён по той же причине, что и перебор света: числа здесь подбираются не на глаз.
    /// Связность сетки — вопрос с ненулевой ценой в обе стороны. Мелкая клетка точнее
    /// ложится на узкие ходы и крутые пандусы, но заливка растёт кубически, а поле
    /// направлений занимает память; крупная дешева, но пролетает мимо лестниц, и тогда
    /// толпа физически не может попасть на соседний этаж.
    ///
    /// Меряется на нескольких сидах: связность зависит от того, какие лестницы выпали,
    /// и на одном сиде можно подобрать значение, которое на следующем разваливается.
    /// </summary>
    public static class CatacombCrowdSweep
    {
        private const string ScenePath = "Assets/Scenes/test.unity";

        private static readonly float[] CellSizes = { 0.75f, 1f, 1.25f, 1.5f };
        private static readonly float[] Reaches = { 1.2f, 1.6f, 2.2f };
        private static readonly int[] Seeds = { 1337, 2, 777 };

        public static void Run()
        {
            var code = 0;

            try
            {
                Report();
            }
            catch (Exception error)
            {
                Debug.LogError("ПЕРЕБОР УПАЛ: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        public static void Report()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var world = UnityEngine.Object.FindFirstObjectByType<CatacombWorld>();
            if (world == null) throw new Exception("НЕТ CatacombWorld на сцене " + ScenePath);

            var text = new StringBuilder();

            text.AppendLine("клетка | корка | сид  | клеток сетки | проходимо | достижимо | доля   | постройка | заливка | память");

            foreach (var cell in CellSizes)
            foreach (var reach in Reaches)
            {
                foreach (var seed in Seeds)
                {
                    world.GenerateImmediate(seed);

                    if (!world.TryGetSpawnPoint(out var spawn)) spawn = world.transform.position;

                    var local = (float3)world.transform.InverseTransformPoint(spawn);

                    var field = new SpiderFlowField();

                    try
                    {
                        var build = Stopwatch.StartNew();
                        field.Build(world, cell, reach);
                        build.Stop();

                        // Связность меряется заливкой БЕЗ предела: предел — это про
                        // стоимость, а вопрос «связан ли уровень» от него не зависит.
                        // Смешать эти два замера в один значит померить, сколько уровня
                        // попало в радиус, и принять это за связность.
                        var ok = field.Rebuild(local, 12, 40, 4096);

                        var reachable = 0;

                        for (var i = 0; i < field.Distance.Length; i++)
                        {
                            if (field.Walkable[i] != 0 && field.Distance[i] != SpiderFlowField.Unreachable) reachable++;
                        }

                        var share = field.WalkableCount == 0 ? 0f : reachable / (float)field.WalkableCount;

                        // Стоимость — отдельным прогоном, на том пределе, с которым
                        // заливка работает в игре. Она идёт заново при каждой смене
                        // клетки игроком, то есть несколько раз в секунду на бегу,
                        // и это единственное здесь, что попадает в бюджет кадра.
                        var flood = Stopwatch.StartNew();
                        field.Rebuild(local, 12, 40, 250);
                        flood.Stop();

                        // Байты на клетку: направление 12, нормаль 12, глубина 4,
                        // расстояние 2, проходимость 1, пустота 1, грани 1, очередь 4.
                        var memory = field.CellCount * 37L / 1024 / 1024;

                        text.AppendLine(
                            $"{cell,6:0.00} | {reach,5:0.00} | {seed,4} | {field.CellCount,12:N0} | {field.WalkableCount,9:N0} | " +
                            $"{reachable,9:N0} | {share,6:P1} | {build.ElapsedMilliseconds,6} мс | " +
                            $"{flood.ElapsedMilliseconds,4} мс | {memory,4} МБ" +
                            (ok ? "" : "  ИГРОК ВНЕ СЕТКИ"));
                    }
                    finally
                    {
                        field.Dispose();
                    }
                }
            }

            Debug.Log($"Перебор сетки навигации:{Environment.NewLine}{text}");
        }
    }
}
