using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Создаёт и настраивает ассеты URP под катакомбы.
    ///
    /// Живёт отдельным пунктом меню, а не выполняется один раз руками, по той же причине,
    /// по которой существует «Починить свет»: значения полей лежат в сохранённых ассетах,
    /// и правка умолчаний в коде до них не доходит. Пункт ассеты <b>пересоздаёт</b>.
    /// </summary>
    public static class CatacombUrpSetup
    {
        private const string Folder = "Assets/Settings";

        private const string RendererPath = Folder + "/Cave Renderer.asset";
        private const string PipelinePath = Folder + "/Cave URP.asset";

        [MenuItem("Tools/Mine Generator/URP: создать и назначить ассеты")]
        public static void Run()
        {
            var changes = new StringBuilder();

            EnsureFolder(Folder);

            var renderer = CreateRenderer(changes);
            var pipeline = CreatePipeline(renderer, changes);

            Assign(pipeline, changes);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"URP настроен:{System.Environment.NewLine}{changes}");
        }

        /// <summary>
        /// Рендерер. Здесь задаётся то, ради чего весь переезд и затевался — Forward+.
        /// </summary>
        private static UniversalRendererData CreateRenderer(StringBuilder changes)
        {
            var data = ScriptableObject.CreateInstance<UniversalRendererData>();

            // Forward+ раздаёт источники кластерами по тайлам экрана, а не поштучно
            // каждому рендереру. Поштучная раздача в Built-in и давала ровный вертикальный
            // шов через весь экран на стыке соседних чанков: когда ламп в кадре больше
            // попиксельного бюджета, соседи получали разные наборы источников.
            //
            // Плата — потолок в 32 видимых источника на камеру на WebGL2. Он упирается
            // в 64 КБ uniform-буфера: SSBO в WebGL нет, и массив источников больше
            // туда просто не влезает. В уровне источников под восемь десятков, но
            // в кадре тесного хода их единицы.
            data.renderingMode = RenderingMode.ForwardPlus;

            // Копия глубины и кадра не нужны: ни мягких частиц, ни искажений,
            // ни экранного затенения в катакомбах нет. Каждая из них — лишний
            // полноэкранный проход, а цель WebGL.
            data.depthPrimingMode = DepthPrimingMode.Disabled;

            AssetDatabase.CreateAsset(data, RendererPath);

            changes.AppendLine($"  рендерер: {RendererPath}, путь Forward+");

            return data;
        }

        private static UniversalRenderPipelineAsset CreatePipeline(UniversalRendererData renderer,
            StringBuilder changes)
        {
            var pipeline = UniversalRenderPipelineAsset.Create(renderer);

            // Лампы в катакомбах заметно ярче единицы, а порода тёмная. Без HDR
            // яркая часть упирается в потолок ещё до тонмаппинга, и ореол вокруг
            // источника превращается в плоское белое пятно.
            pipeline.supportsHDR = true;

            // Меш Marching Cubes — это грани размером с воксель, то есть сплошные
            // косые кромки. Сглаживание им нужнее, чем обычной геометрии; 2 выборки —
            // компромисс с WebGL, как и было в настройках качества до переезда.
            pipeline.msaaSampleCount = 2;

            pipeline.additionalLightsShadowmapResolution = 2048;

            // Фонарь игрока — точечный источник, а не направленный. В URP главным
            // считается только направленный, поэтому тени фонаря идут по ветке
            // дополнительных источников: без этой галочки он не отбрасывает их вовсе.
            //
            // Через SerializedObject, потому что у самих свойств сеттеры internal:
            // снаружи сборки URP они доступны только на чтение. Заодно приходится
            // руками держать m_AnyShadowsSupported — его обычно ведёт тот самый сеттер.
            var serialized = new SerializedObject(pipeline);

            serialized.FindProperty("m_MainLightShadowsSupported").boolValue = true;
            serialized.FindProperty("m_AdditionalLightShadowsSupported").boolValue = true;
            serialized.FindProperty("m_AnyShadowsSupported").boolValue = true;
            serialized.FindProperty("m_SoftShadowsSupported").boolValue = true;

            serialized.ApplyModifiedPropertiesWithoutUndo();

            // В ходах простреливается 10-34 юнита, а дальность самой дальней лампы 38.
            // Полтораста юнитов, стоявшие в настройках качества, тратились на пустоту.
            pipeline.shadowDistance = 50f;

            // Каскады делятся только у направленного источника, а он здесь один —
            // заполняющий, и теней не отбрасывает намеренно.
            pipeline.shadowCascadeCount = 1;

            pipeline.supportsCameraDepthTexture = false;
            pipeline.supportsCameraOpaqueTexture = false;

            AssetDatabase.CreateAsset(pipeline, PipelinePath);

            changes.AppendLine($"  ассет пайплайна: {PipelinePath}");
            changes.AppendLine("  HDR вкл, MSAA 2x, тени дополнительных источников вкл (фонарь точечный)");
            changes.AppendLine($"  дальность теней {pipeline.shadowDistance:0}, каскадов {pipeline.shadowCascadeCount}");

            return pipeline;
        }

        /// <summary>
        /// Назначает пайплайн и в общие настройки графики, и в каждый уровень качества.
        ///
        /// Оба места обязательны: незаполненный уровень качества откатывает проект
        /// на Built-in молча, и разница видна только по тому, что порода стала розовой.
        /// </summary>
        private static void Assign(UniversalRenderPipelineAsset pipeline, StringBuilder changes)
        {
            GraphicsSettings.defaultRenderPipeline = pipeline;

            var restore = QualitySettings.GetQualityLevel();
            var names = QualitySettings.names;

            for (var level = 0; level < names.Length; level++)
            {
                QualitySettings.SetQualityLevel(level, false);
                QualitySettings.renderPipeline = pipeline;
            }

            QualitySettings.SetQualityLevel(restore, false);

            changes.AppendLine($"  назначен по умолчанию и во всех {names.Length} уровнях качества");
        }

        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || Directory.Exists(folder)) return;

            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
        }
    }
}
