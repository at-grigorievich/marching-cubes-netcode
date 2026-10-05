using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Ресурсы интерфейса: базовые ресурсы TextMeshPro и шрифт с кириллицей и турецкими буквами.
    ///
    /// Шрифт — Roboto (лицензия Apache 2.0, лежит в проекте в демо Toony Colors): в нём есть
    /// кириллица и ğ ş ı İ ç ö ü — языки площадок ru/en/tr (M5). Атлас динамический на время
    /// разработки; к выпуску — статический атлас только нужных символов, собранный из таблицы
    /// строк (M5, «Локализация»): динамический тянет в сборку файл шрифта целиком.
    /// </summary>
    public static class UiSetup
    {
        public const string FontFolder = "Assets/Fonts";
        public const string FontAssetPath = "Assets/Resources/Fonts/Roboto SDF.asset";
        public const string BoldAssetPath = "Assets/Resources/Fonts/Roboto Bold SDF.asset";

        private const string RegularSource =
            "Assets/StorePackages/JMO Assets/Toony Colors Pro/Demo TCP2/SG Demo Assets/_Misc Assets/Roboto-Regular.ttf";

        private const string BoldSource =
            "Assets/StorePackages/JMO Assets/Toony Colors Pro/Editor/Fonts/Roboto/Roboto-Bold.ttf";

        [MenuItem("Tools/Mine Generator/Интерфейс: ресурсы TextMeshPro и шрифт")]
        private static void Menu() => Debug.Log(Ensure());

        public static string Ensure()
        {
            var report = "Интерфейс:";

            if (!File.Exists("Assets/TextMesh Pro/Resources/TMP Settings.asset"))
            {
                TMP_PackageResourceImporter.ImportResources(true, false, false);
                report += "\n  TextMeshPro: базовые ресурсы импортированы";
            }
            else
            {
                report += "\n  TextMeshPro: ресурсы уже есть";
            }

            report += "\n  " + EnsureFont(RegularSource, "Roboto-Regular.ttf", FontAssetPath);
            report += "\n  " + EnsureFont(BoldSource, "Roboto-Bold.ttf", BoldAssetPath);

            return report;
        }

        private static string EnsureFont(string source, string fileName, string assetPath)
        {
            if (File.Exists(assetPath)) return $"{Path.GetFileName(assetPath)}: уже есть";

            if (!Directory.Exists(FontFolder)) Directory.CreateDirectory(FontFolder);

            var target = $"{FontFolder}/{fileName}";

            if (!File.Exists(target))
            {
                if (!File.Exists(source)) return $"{fileName}: исходник не найден ({source})";

                File.Copy(source, target);
                AssetDatabase.ImportAsset(target);
            }

            var font = AssetDatabase.LoadAssetAtPath<Font>(target);
            if (font == null) return $"{fileName}: не импортировался как шрифт";

            var folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                AssetDatabase.Refresh();
            }

            var asset = TMP_FontAsset.CreateFontAsset(font, 72, 8, GlyphRenderMode.SDFAA, 1024, 1024,
                AtlasPopulationMode.Dynamic, true);

            if (asset == null) return $"{fileName}: TMP не создал ассет шрифта";

            asset.name = Path.GetFileNameWithoutExtension(assetPath);

            AssetDatabase.CreateAsset(asset, assetPath);

            // Атлас и материал — подассетами, как делает окно создания шрифта TMP: иначе после
            // перезапуска редактора у ассета не будет ни текстуры, ни материала.
            if (asset.atlasTextures != null)
            {
                foreach (var atlas in asset.atlasTextures)
                {
                    if (atlas == null) continue;

                    atlas.name = asset.name + " Atlas";
                    AssetDatabase.AddObjectToAsset(atlas, asset);
                }
            }

            if (asset.material != null)
            {
                asset.material.name = asset.name + " Material";
                AssetDatabase.AddObjectToAsset(asset.material, asset);
            }

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            return $"{Path.GetFileName(assetPath)}: создан, атлас динамический";
        }
    }
}
