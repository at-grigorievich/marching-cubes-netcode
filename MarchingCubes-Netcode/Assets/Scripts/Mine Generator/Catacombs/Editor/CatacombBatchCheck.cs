using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Прогон без графического редактора: применить стиль, собрать уровень, снять кадры.
    ///
    /// Нужен, когда редактор закрыт, — тогда проектный лок свободен и batchmode ходит прямо
    /// по проекту. Живёт в Assets, а не в scratchpad, потому что -executeMethod умеет звать
    /// только скомпилированные редакторные сборки проекта.
    /// </summary>
    public static class CatacombBatchCheck
    {
        private const string OutDir = @"D:\UnityProjects\marching-cubes-arena\_shots";

        public static void Run()
        {
            var code = 0;

            try
            {
                Report();
            }
            catch (Exception error)
            {
                Debug.LogError("ПРОГОН УПАЛ: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        /// <summary>
        /// То же самое, но без выхода из редактора: <see cref="Run"/> закрывает процесс,
        /// и позвать его при открытом редакторе — значит захлопнуть его пользователю.
        /// Через <c>unity cmd run_script</c> зовётся эта.
        /// </summary>
        public static void Report()
        {
            Directory.CreateDirectory(OutDir);

            var tag = Environment.GetCommandLineArgs();
            var name = "batch";
            var menu = "Tools/Mine Generator/Свет: как в DRG (тьма и цвет)";

            for (var i = 0; i < tag.Length - 1; i++)
            {
                if (tag[i] == "-shotTag") name = tag[i + 1];
                if (tag[i] == "-styleMenu") menu = tag[i + 1];
            }

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/test.unity", OpenSceneMode.Single);

            if (!EditorApplication.ExecuteMenuItem(menu)) throw new Exception("не найден пункт меню: " + menu);
            EditorSceneManager.SaveScene(scene);

            var world = UnityEngine.Object.FindFirstObjectByType<CatacombWorld>();
            if (world == null) throw new Exception("НЕТ CatacombWorld");

            world.GenerateImmediate();

            // Эталонные метрики стиля (доля тёмных пикселей, полоса яркости, максимум
            // в кадре) снимались на ПОЛНОСТЬЮ ЗАЖЖЁННОМ уровне, и сравнивать их
            // с погашенным не с чем: он чёрный по определению, а не потому, что
            // со светом что-то не так. Механика темноты проверяется отдельно.
            var litFixtures = UnityEngine.Object.FindFirstObjectByType<CaveFixtures>();

            if (litFixtures != null)
            {
                litFixtures.SetAllLit(true);
                Debug.Log($"ПРОГОН светильники зажжены принудительно: {litFixtures.LitCount} из {litFixtures.Count}");
            }

            var material = world.Settings.Material;

            // Шейдер с ошибкой компиляции даёт розовую породу, и по одним цифрам яркости
            // это не отличить от тёмной сцены. Проверяем явно.
            if (ShaderUtil.ShaderHasError(material.shader))
                throw new Exception("ШЕЙДЕР НЕ СОБРАЛСЯ: " + material.shader.name);

            Debug.Log($"ПРОГОН шейдер {material.shader.name} собран, " +
                      $"карта нормалей {(material.GetTexture("_BumpMap") != null ? "подключена" : "НЕТ")}, " +
                      $"сила рельефа {material.GetFloat("_BumpScale"):0.00}");

            var lamps = 0;
            var veins = 0;
            var shadowed = 0;

            // Считаем по имени родителя, а не по renderMode: в Forward+ вершинных
            // источников нет вовсе, и это поле больше ни на что не влияет.
            foreach (var light in world.GetComponentsInChildren<Light>())
            {
                var fixture = light.transform.parent;

                if (fixture != null && fixture.name == "Vein") veins++;
                else lamps++;

                if (light.shadows != LightShadows.None) shadowed++;
            }

            Debug.Log($"ПРОГОН ламп {lamps}, жил {veins}, с тенями {shadowed}");

            ReportWebs(world);

            var player = GameObject.Find("Test Player");
            var cam = player.GetComponent<Camera>();

            Vector3 spawn;
            if (!world.TryGetSpawnPoint(out spawn)) spawn = player.transform.position;

            var spots = new System.Collections.Generic.List<Vector3> { spawn + Vector3.up * 0.8f };
            var rooms = world.GetRoomCenters(1);
            for (var i = 0; i < rooms.Count && spots.Count < 4; i += Mathf.Max(1, rooms.Count / 3))
                spots.Add(rooms[i] + Vector3.up * 0.8f);

            var shot = 0;

            foreach (var p in spots)
            {
                var bestYaw = 0f;
                var bestScore = -1f;

                for (var a = 0; a < 24; a++)
                {
                    var yaw = a * 15f;
                    var dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                    RaycastHit probe;
                    var d = Physics.Raycast(p, dir, out probe, 60f) ? probe.distance : 60f;
                    var score = -Mathf.Abs(d - 9f);
                    if (score > bestScore) { bestScore = score; bestYaw = yaw; }
                }

                player.transform.position = p;
                player.transform.rotation = Quaternion.Euler(4f, bestYaw, 0f);

                var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                rt.antiAliasing = 4;
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = null;

                RenderTexture.active = rt;
                var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                tex.Apply();
                RenderTexture.active = null;

                var px = tex.GetPixels();
                double sum = 0;
                var dark = 0;
                var magenta = 0;
                var max = 0f;

                foreach (var c in px)
                {
                    var l = c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
                    sum += l;
                    if (l < 0.12f) dark++;
                    if (l > max) max = l;

                    // Розовый цвет несобранного шейдера: красный и синий высоко, зелёный низко.
                    if (c.r > 0.6f && c.b > 0.6f && c.g < 0.3f) magenta++;
                }

                var mean = sum / px.Length;

                double var2 = 0;
                foreach (var c in px)
                {
                    var l = c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
                    var2 += (l - mean) * (l - mean);
                }

                var sd = Math.Sqrt(var2 / px.Length);

                double sat = 0;
                foreach (var c in px)
                {
                    var mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                    var mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                    sat += mx > 1e-4f ? (mx - mn) / mx : 0;
                }

                sat /= px.Length;

                Debug.Log($"ПРОГОН кадр {shot}: сред {mean:0.000} разброс {sd:0.000} | " +
                          $"тёмных {100.0 * dark / px.Length:0.0}% | насыщ {sat:0.000} | макс {max:0.000} | " +
                          $"розовых {100.0 * magenta / px.Length:0.00}%");

                File.WriteAllBytes(Path.Combine(OutDir, $"{name}_{shot}.png"), tex.EncodeToPNG());

                UnityEngine.Object.DestroyImmediate(tex);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);

                shot++;
            }

            ReportWebTear(world);

            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log("ПРОГОН готово");
        }

        /// <summary>
        /// Паутина: сколько её, ловится ли вязкий объём запросом и рвётся ли она.
        ///
        /// Проверять это в batchmode можно и нужно: физические запросы работают и вне
        /// play-режима — коллайдеры зарегистрированы сразу, — а сам движок физики там
        /// не крутится. То есть тракт «объём находится, паутина рвётся» проверяется
        /// полностью, и непроверенным остаётся только ощущение от торможения на ходу.
        /// Его смотреть руками: в этом проекте несколько диагнозов подряд оказались
        /// неверными именно из-за гадания по картинке вместо проверки в игре.
        /// </summary>
        private static void ReportWebs(CatacombWorld world)
        {
            var webs = world.GetComponentsInChildren<CaveWeb>();

            if (webs.Length == 0)
            {
                Debug.Log("ПРОГОН паутин 0");
                return;
            }

            var volumes = 0;
            var found = 0;

            foreach (var web in webs)
            {
                var box = web.GetComponent<BoxCollider>();

                if (box != null && box.isTrigger) volumes++;

                // Тот же запрос, которым паутину ищет движение игрока. Если он её
                // не находит, замедления в игре не будет, сколько бы объёмов ни висело.
                var overlaps = Physics.OverlapSphere(web.transform.position, 0.3f, ~0,
                    QueryTriggerInteraction.Collide);

                foreach (var overlap in overlaps)
                {
                    if (overlap.GetComponent<CaveWeb>() == null) continue;

                    found++;
                    break;
                }
            }

            Debug.Log($"ПРОГОН паутин {webs.Length}, с вязким объёмом {volumes}, " +
                      $"ловится запросом {found}, замедление x{webs[0].SpeedScale:0.00} " +
                      $"(падение x{webs[0].FallScale:0.00})");
        }

        /// <summary>
        /// Разрыв паутины. Отдельно от отчёта и ПОСЛЕ съёмки кадров: проверка рвёт
        /// настоящую паутину, а измерительный инструмент не должен менять то, что меряет.
        /// </summary>
        private static void ReportWebTear(CatacombWorld world)
        {
            var webs = world.GetComponentsInChildren<CaveWeb>();

            if (webs.Length == 0) return;

            var before = webs.Length;
            var torn = CaveWebs.TearAt(webs[0].transform.position, 1.5f);
            var left = world.GetComponentsInChildren<CaveWeb>().Length;

            Debug.Log($"ПРОГОН разрыв: было {before}, радиус 1.5 порвал {torn}, осталось {left}");
        }
    }
}
