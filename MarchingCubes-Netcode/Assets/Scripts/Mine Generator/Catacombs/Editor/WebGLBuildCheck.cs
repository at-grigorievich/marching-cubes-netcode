using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Сборка WebGL под площадку и её проверка (M5.2, M5.7). Площадка — define сборки
    /// (<c>PLATFORM_YANDEX</c>, <c>PLATFORM_CRAZYGAMES</c>) через <see cref="BuildPlayerOptions.extraScriptingDefines"/>,
    /// а не через настройки проекта: так define живёт только в этой сборке, редактор и прогоны
    /// остаются на заглушке площадки и не перекомпилируются. Шаблон страницы — свой на площадку.
    ///
    /// Сборка кладётся вне репозитория (<c>_builds/&lt;площадка&gt;</c>). Отчёт: размер начальной
    /// загрузки (всё, что грузится до первого кадра), всего, число файлов, имена (Яндекс: без пробелов
    /// и кириллицы), топ ассетов по размеру из <see cref="BuildReport"/> — без замера не резать.
    ///
    /// Активная платформа редактора должна быть WebGL: переключение (<see cref="SwitchToWebGL"/>)
    /// перезагружает домен, и звать его надо отдельным вызовом до сборки.
    /// </summary>
    public static class WebGLBuildCheck
    {
        public enum Target
        {
            Yandex,
            CrazyGames
        }

        /// <summary>Пороги плана (M5.1, M5.7).</summary>
        public const long InitialTarget = 20L * 1024 * 1024;
        public const long InitialLimit = 50L * 1024 * 1024;
        public const long TotalLimit = 100L * 1024 * 1024;
        public const int FilesLimit = 1500;

        private const string ScenePath = "Assets/Scenes/test.unity";

        private static string OutputRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "../../_builds"));

        [MenuItem("Tools/Mine Generator/Сборка WebGL: Yandex Games")]
        private static void MenuYandex() => Debug.Log(Report(Target.Yandex));

        [MenuItem("Tools/Mine Generator/Сборка WebGL: CrazyGames")]
        private static void MenuCrazyGames() => Debug.Log(Report(Target.CrazyGames));

        public static string ReportYandex() => Report(Target.Yandex);

        public static string ReportCrazyGames() => Report(Target.CrazyGames);

        /// <summary>Переключить редактор на WebGL — отдельным вызовом: перезагружает домен.</summary>
        public static string SwitchToWebGL()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL) return "уже WebGL";

            var ok = EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);
            return ok ? "переключено на WebGL" : "НЕ ПЕРЕКЛЮЧИЛОСЬ";
        }

        public static string SwitchToStandalone()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64) return "уже Windows";

            var ok = EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
            return ok ? "переключено на Windows" : "НЕ ПЕРЕКЛЮЧИЛОСЬ";
        }

        public static void Run()
        {
            var code = 0;

            try
            {
                var target = Environment.GetCommandLineArgs().Contains("-crazygames") ? Target.CrazyGames : Target.Yandex;
                var text = Report(target);
                code = text.Contains("ПЛОХО") ? 1 : 0;
                Debug.Log(text);
            }
            catch (Exception error)
            {
                Debug.LogError("СБОРКА WEBGL УПАЛА: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        public static string Report(Target target)
        {
            var text = new StringBuilder();
            var failed = 0;

            text.AppendLine($"=== Сборка WebGL: {target} ===");

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
            {
                text.AppendLine("  ПЛОХО: активная платформа не WebGL — сначала WebGLBuildCheck.SwitchToWebGL()");
                return text.ToString();
            }

            var folder = target == Target.Yandex ? "yandex" : "crazygames";
            var output = Path.Combine(OutputRoot, folder);

            if (Directory.Exists(output)) Directory.Delete(output, true);
            Directory.CreateDirectory(output);

            var saved = Capture();

            BuildReport report;
            var clock = Stopwatch.StartNew();

            TextureBudget(text);

            try
            {
                Apply(target);

                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = output,
                    target = BuildTarget.WebGL,
                    targetGroup = BuildTargetGroup.WebGL,
                    options = BuildOptions.None,
                    extraScriptingDefines = new[] { target == Target.Yandex ? "PLATFORM_YANDEX" : "PLATFORM_CRAZYGAMES" }
                });
            }
            finally
            {
                Restore(saved);
            }

            var summary = report.summary;
            text.AppendLine($"  итог {summary.result}, ошибок {summary.totalErrors}, предупреждений {summary.totalWarnings}, " +
                            $"сборка заняла {clock.Elapsed.TotalMinutes:0.0} мин");

            if (summary.result != BuildResult.Succeeded)
            {
                foreach (var step in report.steps)
                {
                    foreach (var message in step.messages.Where(m => m.type == LogType.Error || m.type == LogType.Exception).Take(10))
                    {
                        text.AppendLine("    " + message.content.Split('\n')[0]);
                    }
                }

                text.AppendLine("  ПЛОХО: сборка не прошла");
                return text.ToString();
            }

            failed += CheckFiles(output, text);
            TopAssets(report, text);

            text.AppendLine();
            text.AppendLine(failed > 0 ? $"ПРОВЕРКА СБОРКИ НЕ ПРОШЛА, пунктов {failed}" : "Проверка сборки пройдена");
            return text.ToString();
        }

        // ------------------------------------------------------------------ настройки

        private struct Saved
        {
            public bool Splash;
            public bool SplashLogo;
            public string Template;
            public WebGLCompressionFormat Compression;
            public bool Fallback;
            public bool NameAsHashes;
            public ManagedStrippingLevel Stripping;
            public Il2CppCodeGeneration CodeGeneration;
        }

        private static Saved Capture() => new Saved
        {
            Splash = PlayerSettings.SplashScreen.show,
            SplashLogo = PlayerSettings.SplashScreen.showUnityLogo,
            Template = PlayerSettings.WebGL.template,
            Compression = PlayerSettings.WebGL.compressionFormat,
            Fallback = PlayerSettings.WebGL.decompressionFallback,
            NameAsHashes = PlayerSettings.WebGL.nameFilesAsHashes,
            Stripping = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.WebGL),
            CodeGeneration = PlayerSettings.GetIl2CppCodeGeneration(NamedBuildTarget.WebGL)
        };

        /// <summary>Сжатие сборки — замеряется прогоном (gzip против Brotli).</summary>
        public static WebGLCompressionFormat Compression = WebGLCompressionFormat.Brotli;

        /// <summary>
        /// Сжатие с распаковкой на клиенте: обе площадки отдают файлы своим сервером, и заголовки
        /// Content-Encoding для Brotli там не гарантированы — без них браузер получил бы сжатый
        /// поток как есть. Распаковка на клиенте работает при любом сервере ценой пары секунд.
        /// </summary>
        private static void Apply(Target target)
        {
            // Заставка Unity — 2.7 МБ данных (замер первой сборки); в Unity 6 её можно выключить и на Personal.
            // Вторая сборка показала: без show=false логотип остаётся в данных — выключать и его.
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.SplashScreen.showUnityLogo = false;
            PlayerSettings.WebGL.template = target == Target.Yandex ? "PROJECT:Yandex" : "PROJECT:CrazyGames";
            PlayerSettings.WebGL.compressionFormat = Compression;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.nameFilesAsHashes = false;
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.High);
            PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.WebGL, Il2CppCodeGeneration.OptimizeSize);
        }

        /// <summary>Настройки проекта — назад: сборка не должна оставлять за собой правок в ProjectSettings.</summary>
        private static void Restore(Saved saved)
        {
            PlayerSettings.SplashScreen.show = saved.Splash;
            PlayerSettings.SplashScreen.showUnityLogo = saved.SplashLogo;
            PlayerSettings.WebGL.template = saved.Template;
            PlayerSettings.WebGL.compressionFormat = saved.Compression;
            PlayerSettings.WebGL.decompressionFallback = saved.Fallback;
            PlayerSettings.WebGL.nameFilesAsHashes = saved.NameAsHashes;
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, saved.Stripping);
            PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.WebGL, saved.CodeGeneration);
        }

        // ------------------------------------------------------------------ текстуры

        /// <summary>
        /// Бюджет текстур под WebGL (M5.7). Первая сборка: начальная загрузка 30 МБ, из них текстуры
        /// пауков и паутины 2048² — по 5.3 МБ каждая в данных, девять штук. Паук в кадре — не больше
        /// трети экрана, и 1024 хватает; ставится переопределением только для WebGL — в редакторе
        /// и других сборках текстуры прежние.
        /// </summary>
        private static readonly (string Folder, int Size)[] Budget =
        {
            ("Assets/StorePackages/Spiders - characters with animations", 1024),
            ("Assets/StorePackages/PolyOne/Cobwebs Pack", 1024)
        };

        private static void TextureBudget(StringBuilder text)
        {
            var changed = 0;

            foreach (var (folder, size) in Budget)
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) continue;

                    var platform = importer.GetPlatformTextureSettings("WebGL");
                    if (platform.overridden && platform.maxTextureSize <= size) continue;

                    platform.overridden = true;
                    platform.maxTextureSize = size;
                    platform.format = TextureImporterFormat.Automatic;
                    importer.SetPlatformTextureSettings(platform);
                    importer.SaveAndReimport();
                    changed++;
                }
            }

            text.AppendLine($"  бюджет текстур WebGL: переопределено {changed}");
        }

        // ------------------------------------------------------------------ файлы

        private static readonly Regex SafeName = new Regex(@"^[A-Za-z0-9._\-/]+$");

        private static int CheckFiles(string output, StringBuilder text)
        {
            var failed = 0;
            var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories)
                .Select(path => path.Substring(output.Length + 1).Replace('\\', '/'))
                .ToList();

            long total = 0;
            long initial = 0;
            var bad = new List<string>();

            text.AppendLine();
            text.AppendLine("  файлы сборки:");

            foreach (var file in files.OrderByDescending(f => new FileInfo(Path.Combine(output, f)).Length))
            {
                var size = new FileInfo(Path.Combine(output, file)).Length;
                total += size;

                // Начальная загрузка — всё, кроме StreamingAssets: до первого кадра качается страница,
                // загрузчик, фреймворк, код и данные целиком.
                if (!file.StartsWith("StreamingAssets/")) initial += size;
                if (!SafeName.IsMatch(file)) bad.Add(file);

                if (size >= 64 * 1024) text.AppendLine($"    {file,-48} {Mb(size),8}");
            }

            text.AppendLine($"  начальная загрузка {Mb(initial)} (цель {Mb(InitialTarget)}, порог {Mb(InitialLimit)}), " +
                            $"всего {Mb(total)} (порог {Mb(TotalLimit)}), файлов {files.Count} (порог {FilesLimit})");

            CheckScene.Expect(initial <= InitialLimit, "начальная загрузка не больше 50 МБ", text, ref failed);
            if (initial > InitialTarget) text.AppendLine("  (выше цели 20 МБ для мобильной главной CrazyGames)");
            CheckScene.Expect(total <= TotalLimit, "всего не больше 100 МБ", text, ref failed);
            CheckScene.Expect(files.Count <= FilesLimit, "файлов не больше 1500", text, ref failed);
            CheckScene.Expect(bad.Count == 0, "имена файлов без пробелов и кириллицы" + (bad.Count > 0 ? ": " + string.Join(", ", bad.Take(5)) : ""), text, ref failed);
            CheckScene.Expect(files.Contains("index.html"), "index.html в корне", text, ref failed);

            return failed;
        }

        private static void TopAssets(BuildReport report, StringBuilder text)
        {
            var sizes = new Dictionary<string, ulong>();

            foreach (var packed in report.packedAssets)
            {
                foreach (var content in packed.contents)
                {
                    var key = string.IsNullOrEmpty(content.sourceAssetPath) ? "(встроенное)" : content.sourceAssetPath;
                    sizes.TryGetValue(key, out var size);
                    sizes[key] = size + content.packedSize;
                }
            }

            text.AppendLine();
            text.AppendLine("  топ-30 ассетов по размеру в данных сборки (до сжатия):");

            foreach (var pair in sizes.OrderByDescending(p => p.Value).Take(30))
            {
                text.AppendLine($"    {Mb((long)pair.Value),8}  {pair.Key}");
            }

            var byType = new Dictionary<string, ulong>();

            foreach (var pair in sizes)
            {
                var ext = Path.GetExtension(pair.Key).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = "(прочее)";
                byType.TryGetValue(ext, out var size);
                byType[ext] = size + pair.Value;
            }

            text.AppendLine("  по типам: " + string.Join(", ", byType.OrderByDescending(p => p.Value).Take(10).Select(p => $"{p.Key} {Mb((long)p.Value)}")));
        }

        private static string Mb(long bytes) => $"{bytes / (1024f * 1024f):0.00} МБ";
    }
}
