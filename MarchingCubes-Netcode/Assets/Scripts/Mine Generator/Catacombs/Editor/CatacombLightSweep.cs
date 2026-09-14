using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Подбор множителей яркости и дальности источников замером, а не на глаз.
    ///
    /// Заведён при переезде на URP: в Built-in точечный источник затухал по пологой
    /// табличной кривой, в URP — строго обратноквадратично, да ещё с гладким окном,
    /// гасящим свет в ноль у границы дальности. На середине хода это разница в три-семь
    /// раз, и прежние значения давали втрое более тёмную сцену. Нужную пару множителей
    /// (x8 по яркости, x2 по дальности) нашёл именно этот перебор.
    ///
    /// Оставлен в проекте, потому что задача повторяется: любая правка стиля света
    /// упирается в те же три метрики, и мерить их глазами по одному кадру бесполезно.
    /// Гоняет четыре эталонных ракурса, направленный заполняющий не трогает — затухания
    /// по расстоянию у него нет, компенсировать нечего.
    ///
    /// Диапазоны перебора правятся прямо в <see cref="Sweep"/>: это стенд, а не настройка.
    /// </summary>
    public static class CatacombLightSweep
    {
        public static void Run()
        {
            var code = 0;

            try
            {
                Sweep();
            }
            catch (Exception error)
            {
                Debug.LogError("СВИП УПАЛ: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        private static void Sweep()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/test.unity", OpenSceneMode.Single);

            if (!EditorApplication.ExecuteMenuItem("Tools/Mine Generator/Свет: как в DRG (тьма и цвет)"))
                throw new Exception("не найден пункт меню стиля");

            var world = UnityEngine.Object.FindFirstObjectByType<CatacombWorld>();
            if (world == null) throw new Exception("НЕТ CatacombWorld");

            world.GenerateImmediate();

            var player = GameObject.Find("Test Player");
            var cam = player.GetComponent<Camera>();

            // Снимаем исходные значения один раз: множители применяются к ним, а не
            // накапливаются от прогона к прогону.
            var lights = new List<Light>();
            var baseIntensity = new List<float>();
            var baseRange = new List<float>();

            foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                lights.Add(light);
                baseIntensity.Add(light.intensity);
                baseRange.Add(light.range);
            }

            var spots = Spots(world, player);

            float[] gains = { 5f, 6f, 8f, 10f, 12f };
            float[] reaches = { 1.5f, 1.75f, 2f };

            var report = new StringBuilder();
            report.AppendLine("СВИП цель: тёмных 23-26%, в полосе 0.1-0.3 около 76%, максимум 0.87-0.98");

            foreach (var reach in reaches)
            {
                foreach (var gain in gains)
                {
                    for (var i = 0; i < lights.Count; i++)
                    {
                        if (lights[i] == null) continue;

                        // Направленный не трогаем вовсе: затухания по расстоянию у него нет,
                        // закон в URP не изменился, и компенсировать ему нечего. Умножать
                        // его вместе со всеми — значит менять не яркость, а сам стиль:
                        // заполняющий приходит отовсюду одинаково и гасит светотень.
                        if (lights[i].type == LightType.Directional) continue;

                        lights[i].intensity = baseIntensity[i] * gain;
                        lights[i].range = baseRange[i] * reach;
                    }

                    double dark = 0, band = 0, max = 0, sd = 0;

                    foreach (var spot in spots)
                    {
                        var m = Measure(cam, player, spot);

                        dark += m.Dark;
                        band += m.Band;
                        sd += m.Sd;
                        max = Math.Max(max, m.Max);
                    }

                    var n = spots.Count;

                    report.AppendLine($"  яркость x{gain:0.0} дальность x{reach:0.00}: " +
                                      $"тёмных {dark / n:0.0}% | полоса {band / n:0.0}% | " +
                                      $"разброс {sd / n:0.000} | макс {max:0.000}");
                }
            }

            Debug.Log(report.ToString());
        }

        private static List<Vector3> Spots(CatacombWorld world, GameObject player)
        {
            Vector3 spawn;
            if (!world.TryGetSpawnPoint(out spawn)) spawn = player.transform.position;

            var spots = new List<Vector3> { spawn + Vector3.up * 0.8f };
            var rooms = world.GetRoomCenters(1);

            for (var i = 0; i < rooms.Count && spots.Count < 4; i += Mathf.Max(1, rooms.Count / 3))
                spots.Add(rooms[i] + Vector3.up * 0.8f);

            return spots;
        }

        private struct Metrics
        {
            public double Dark;
            public double Band;
            public double Sd;
            public double Max;
        }

        private static Metrics Measure(Camera cam, GameObject player, Vector3 point)
        {
            // Тот же выбор ракурса, что в CatacombBatchCheck: смотрим туда, где до стены
            // около девяти юнитов, иначе кадр упирается в породу или в пустую даль.
            var bestYaw = 0f;
            var bestScore = -1f;

            for (var a = 0; a < 24; a++)
            {
                var yaw = a * 15f;
                var dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;

                RaycastHit probe;
                var d = Physics.Raycast(point, dir, out probe, 60f) ? probe.distance : 60f;
                var score = -Mathf.Abs(d - 9f);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestYaw = yaw;
                }
            }

            player.transform.position = point;
            player.transform.rotation = Quaternion.Euler(4f, bestYaw, 0f);

            var rt = new RenderTexture(640, 360, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;

            RenderTexture.active = rt;
            var tex = new Texture2D(640, 360, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 640, 360), 0, 0);
            tex.Apply();
            RenderTexture.active = null;

            var px = tex.GetPixels();

            double sum = 0;
            var dark = 0;
            var band = 0;
            var max = 0f;

            foreach (var c in px)
            {
                var l = c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;

                sum += l;

                if (l < 0.12f) dark++;
                if (l >= 0.1f && l <= 0.3f) band++;
                if (l > max) max = l;
            }

            var mean = sum / px.Length;

            double var2 = 0;
            foreach (var c in px)
            {
                var l = c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
                var2 += (l - mean) * (l - mean);
            }

            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);

            return new Metrics
            {
                Dark = 100.0 * dark / px.Length,
                Band = 100.0 * band / px.Length,
                Sd = Math.Sqrt(var2 / px.Length),
                Max = max
            };
        }
    }
}
