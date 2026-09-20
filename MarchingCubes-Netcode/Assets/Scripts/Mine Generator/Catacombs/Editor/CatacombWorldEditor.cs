﻿﻿using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MineGenerator.Catacombs.EditorTools
{
    [CustomEditor(typeof(CatacombWorld))]
    public sealed class CatacombWorldEditor : Editor
    {
        private const string MaterialPath = "Assets/Materials/Cave Rock.mat";
        private const string TexturePath = "Assets/Textures/Cave Rock.png";
        private const string NormalPath = "Assets/Textures/Cave Rock Normal.png";
        private const string SettingsPath = "Assets/Resources/Data/Catacomb Settings.asset";
        private const string CaveShaderName = "Mine Generator/Cave Triplanar";
        private const string WebPrefabFolder = "Assets/StorePackages/PolyOne/Cobwebs Pack/Prefabs";

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var world = (CatacombWorld)target;
            var settings = world.Settings;

            EditorGUILayout.Space();

            if (settings == null)
            {
                EditorGUILayout.HelpBox("Назначьте Catacomb Settings, чтобы генерировать катакомбы.",
                    MessageType.Warning);
                return;
            }

            if (settings.IsPresetOutdated)
            {
                EditorGUILayout.HelpBox(
                    "Настройки остались от прошлой версии генератора: часть значений формы " +
                    "(мягкость края, шум, сглаживание стыков) там старая, и коробки размывает обратно " +
                    "в бесформенные ходы.", MessageType.Warning);

                if (GUILayout.Button("Привести настройки к эталонным"))
                {
                    var material = GetOrCreateCaveMaterial();

                    ApplyReferencePreset(settings);
                    EnsureCaveMaterial(settings, material);
                    AssetDatabase.SaveAssets();

                    world.Generate();
                }

                EditorGUILayout.Space();
            }

            DrawStats(world, settings);

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Сгенерировать")) world.Generate();
                if (GUILayout.Button("Новый сид")) world.Generate(Random.Range(int.MinValue, int.MaxValue));
                if (GUILayout.Button("Очистить")) world.Clear();
            }

            if (world.IsGenerating)
            {
                var rect = EditorGUILayout.GetControlRect(false, 18f);
                EditorGUI.ProgressBar(rect, world.Progress, $"Генерация {world.Progress:P0}");
                Repaint();
            }
        }

        private static void DrawStats(CatacombWorld world, CatacombSettings settings)
        {
            var size = settings.WorldSize;

            var vertices = 0;
            var triangles = 0;

            foreach (var chunk in world.Chunks)
            {
                if (chunk.Mesh == null) continue;
                vertices += chunk.Mesh.vertexCount;
                triangles += (int)(chunk.Mesh.GetIndexCount(0) / 3);
            }

            var density = settings.DensityMemoryBytes / (1024f * 1024f);

            EditorGUILayout.LabelField("Статистика", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Размер мира", $"{size.x:0} x {size.y:0} x {size.z:0} юнитов");
            EditorGUILayout.LabelField("Чанков", $"{settings.ChunkCount} по {settings.ChunkResolution}^3 ячеек");
            EditorGUILayout.LabelField("Поля плотности", $"{density:0.0} МБ");

            if (world.Chunks.Count > 0)
            {
                EditorGUILayout.LabelField("Сид", world.CurrentSeed.ToString());
                EditorGUILayout.LabelField("Геометрия", $"{vertices:N0} вершин / {triangles:N0} треугольников");

                if (world.Layout != null)
                {
                    EditorGUILayout.LabelField("Планировка",
                        $"{world.Layout.LevelCount} уровней, {world.Layout.Boxes.Length} примитивов");
                    EditorGUILayout.LabelField("Граф ходов",
                        $"развилок в среднем {world.Layout.AverageDegree:0.0} на узел, " +
                        $"тупиков {world.Layout.DeadEnds}");

                    if (world.Layout.DeadEnds > 0)
                    {
                        EditorGUILayout.HelpBox(
                            $"{world.Layout.DeadEnds} тупиков. Поднимите Min Connections до 2, " +
                            "иначе игрока будет некуда уводить от толпы.", MessageType.Warning);
                    }
                }
            }

            var levelsFit = LevelsThatFit(settings);
            if (levelsFit < settings.LevelCount)
            {
                EditorGUILayout.HelpBox(
                    $"По высоте мира помещается только {levelsFit} уровней из {settings.LevelCount}. " +
                    "Поднимите World Size In Chunks по Y или уменьшите Room Height.", MessageType.Warning);
            }

            if (settings.Material == null)
            {
                EditorGUILayout.HelpBox("В настройках не задан материал — чанки будут розовыми.",
                    MessageType.Warning);
                DrawAssignMaterialButton(settings, world);
            }
            else if (settings.Material.shader == null || settings.Material.shader.name != CaveShaderName)
            {
                EditorGUILayout.HelpBox(
                    $"Материал \"{settings.Material.name}\" использует шейдер " +
                    $"\"{(settings.Material.shader == null ? "нет" : settings.Material.shader.name)}\". " +
                    "У меша Marching Cubes нет UV, поэтому обычный шейдер даёт ровную заливку без фактуры.",
                    MessageType.Warning);
                DrawAssignMaterialButton(settings, world);
            }

            if (density > 64f)
            {
                EditorGUILayout.HelpBox(
                    "Поля плотности занимают больше 64 МБ. Для WebGL уменьшите размер мира " +
                    "или разрешение чанка.", MessageType.Warning);
            }
        }

        /// <summary>
        /// Ровно те значения, на которых снят отчётный рендер. Нужен, потому что ассет
        /// настроек переживает обновления генератора: поля, совпавшие по имени со старой
        /// версией, сохраняют старые значения, и уровень остаётся размытым, хотя примитивы
        /// уже стали коробками.
        /// </summary>
        private static void ApplyReferencePreset(CatacombSettings settings)
        {
            if (settings == null) return;

            var so = new SerializedObject(settings);

            so.FindProperty("chunkResolution").intValue = 16;
            so.FindProperty("voxelSize").floatValue = 1f;
            so.FindProperty("worldSizeInChunks").vector3IntValue = new Vector3Int(5, 4, 5);
            so.FindProperty("boundaryThickness").floatValue = 3f;
            so.FindProperty("gridStep").floatValue = 4f;

            so.FindProperty("seed").intValue = 1337;
            so.FindProperty("randomizeSeed").boolValue = false;

            so.FindProperty("levelCount").intValue = 3;
            so.FindProperty("roomsPerLevel").vector2IntValue = new Vector2Int(10, 14);

            // Не залы, а узлы-развилки: места ровно чтобы обкружить толпу.
            so.FindProperty("roomSize").vector2Value = new Vector2(6f, 10f);
            so.FindProperty("roomHeight").floatValue = 4.5f;
            so.FindProperty("arenasPerLevel").intValue = 0;
            so.FindProperty("arenaScale").floatValue = 2.2f;

            so.FindProperty("corridorWidth").floatValue = 3.5f;
            so.FindProperty("corridorHeight").floatValue = 3.6f;
            so.FindProperty("extraConnections").floatValue = 0.6f;
            so.FindProperty("minConnections").intValue = 2;

            so.FindProperty("galleryShare").floatValue = 0.35f;
            so.FindProperty("galleryMinLength").floatValue = 20f;
            so.FindProperty("galleryWidthScale").floatValue = 1.35f;
            so.FindProperty("galleryHeightScale").floatValue = 1.3f;

            so.FindProperty("pathChaos").floatValue = 0.6f;
            so.FindProperty("pinchChance").floatValue = 0.3f;
            so.FindProperty("pinchWidth").floatValue = 0.55f;
            so.FindProperty("floorWave").floatValue = 6f;
            so.FindProperty("floorWaveScale").floatValue = 0.02f;

            so.FindProperty("cornerRounding").floatValue = 0.9f;
            so.FindProperty("junctionBlend").floatValue = 0.6f;

            so.FindProperty("levelLinkMode").enumValueIndex = (int)LevelLinkMode.Stairwell;
            so.FindProperty("linksBetweenLevels").intValue = 2;
            so.FindProperty("rampRun").floatValue = 16f;
            so.FindProperty("rampSlope").floatValue = 0.5f;

            so.FindProperty("noiseScale").floatValue = 0.12f;
            so.FindProperty("noiseAmplitude").floatValue = 0.35f;
            so.FindProperty("noiseOctaves").intValue = 2;

            so.FindProperty("isoLevel").floatValue = 0.5f;
            so.FindProperty("surfaceSoftness").floatValue = 0.7f;
            so.FindProperty("colliderMode").enumValueIndex = (int)ColliderMode.All;
            so.FindProperty("millisecondsPerFrame").floatValue = 8f;

            so.FindProperty("presetVersion").intValue = CatacombSettings.CurrentPresetVersion;

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
        }

        [MenuItem("Tools/Mine Generator/Настройки как на рендере")]
        private static void ApplyPresetMenu()
        {
            var settings = AssetDatabase.LoadAssetAtPath<CatacombSettings>(SettingsPath);

            if (settings == null)
            {
                Debug.LogWarning($"Не найден {SettingsPath}. Сначала создайте сетап катакомб.");
                return;
            }

            var material = GetOrCreateCaveMaterial();

            settings = AssetDatabase.LoadAssetAtPath<CatacombSettings>(SettingsPath);

            ApplyReferencePreset(settings);
            EnsureCaveMaterial(settings, material);
            AssetDatabase.SaveAssets();

            foreach (var world in Object.FindObjectsByType<CatacombWorld>(FindObjectsSortMode.None))
            {
                if (world.Settings == settings) world.Generate();
            }

            EditorGUIUtility.PingObject(settings);
            Debug.Log("Настройки приведены к эталонным: 5x4x5 чанков, разрешение 16, 3 уровня, " +
                      "узкие ходы, узлы-развилки без тупиков, сид 1337.");
        }

        private static void DrawAssignMaterialButton(CatacombSettings settings, CatacombWorld world)
        {
            if (!GUILayout.Button("Назначить трипланарный материал породы")) return;

            var material = GetOrCreateCaveMaterial();
            AssignMaterial(settings, material);
            AssetDatabase.SaveAssets();

            if (world.Chunks.Count > 0) world.Generate(world.CurrentSeed);
        }

        private static int LevelsThatFit(CatacombSettings settings)
        {
            var margin = settings.BoundaryThickness + settings.PrimitiveMargin;
            var usable = settings.WorldSize.y - margin - settings.RoomHeight - margin;

            return Mathf.Clamp(Mathf.FloorToInt(usable / (settings.RoomHeight + 3f)) + 1, 1, settings.LevelCount);
        }

        [MenuItem("Tools/Mine Generator/Создать сетап катакомб")]
        private static void CreateSetup()
        {
            var settings = CreateSettingsAsset();
            var world = CreateWorld(settings);

            Selection.activeObject = world.gameObject;
            EditorGUIUtility.PingObject(settings);
        }

        /// <summary>Собирает сцену, в которой механику можно потрогать руками: игрок, свет, катакомбы.</summary>
        [MenuItem("Tools/Mine Generator/Создать тестовую сцену")]
        private static void CreateTestScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var settings = CreateSettingsAsset();
            var world = CreateWorld(settings);

            var player = new GameObject("Test Player");
            player.transform.position = new Vector3(0f, settings.WorldSize.y * 0.5f, 0f);

            var controller = player.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.4f;
            controller.center = new Vector3(0f, -0.2f, 0f);
            controller.slopeLimit = 55f;
            controller.stepOffset = 0.5f;

            var camera = player.AddComponent<Camera>();
            camera.fieldOfView = 75f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 400f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = BackgroundColor(Style);

            player.AddComponent<AudioListener>();

            var lamp = new GameObject("Headlamp");
            lamp.transform.SetParent(player.transform, false);
            ApplyHeadlamp(lamp.AddComponent<Light>(), Style);

            var fill = new GameObject("Fill Light");
            ApplyFillLight(fill.AddComponent<Light>(), Style);

            var rig = player.AddComponent<CatacombTestRig>();
            var rigObject = new SerializedObject(rig);
            rigObject.FindProperty("world").objectReferenceValue = world;
            rigObject.ApplyModifiedPropertiesWithoutUndo();

            ApplyCaveRenderSettings(Style);
            EnsureFixtures(world, Style);
            EnsureDust(player);

            Selection.activeObject = player;
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log("Тестовая сцена собрана. Нажмите Play, кликните по окну игры и копайте ЛКМ.");
        }

        /// <summary>
        /// Чинит освещение, туман и материал в уже открытой сцене.
        ///
        /// Нужен потому, что положение фонаря и RenderSettings хранятся в сохранённой сцене,
        /// а свойства материала — в его ассете. Правки в коде генератора до них не доходят:
        /// пересборка скриптов ничего этого не двигает, и «я изменил» превращается
        /// в «у меня всё по-старому».
        /// </summary>
        [MenuItem("Tools/Mine Generator/Починить свет и туман в текущей сцене")]
        private static void RepairCurrentScene()
        {
            var style = Style;

            var changes = new System.Text.StringBuilder();
            changes.AppendLine($"  стиль: {(style == CaveLightStyle.Deep ? "как в DRG" : "как было")}");

            var rig = Object.FindFirstObjectByType<CatacombTestRig>();

            if (rig != null)
            {
                var lamp = rig.GetComponentInChildren<Light>();

                if (lamp != null)
                {
                    var beforeRange = lamp.range;
                    ApplyHeadlamp(lamp, style);
                    changes.AppendLine($"  фонарь: дальность {beforeRange:0.0} -> {lamp.range:0.0}, " +
                                       $"яркость {lamp.intensity:0.0}");
                }
                else
                {
                    changes.AppendLine("  фонарь: не найден");
                }


                var camera = rig.GetComponent<Camera>();

                if (camera != null)
                {
                    camera.backgroundColor = BackgroundColor(style);
                }

                changes.AppendLine(style == CaveLightStyle.Deep
                    ? EnsureDust(rig.gameObject)
                    : RemoveDust(rig.gameObject));
            }
            else
            {
                changes.AppendLine("  игрок со стендом не найден — фонарь и эффекты не тронуты");
            }

            var fill = FindDirectional();

            if (fill == null)
            {
                var go = new GameObject("Fill Light");
                fill = go.AddComponent<Light>();
                changes.AppendLine("  заполняющий свет: создан");
            }
            else
            {
                changes.AppendLine("  заполняющий свет: обновлён");
            }

            ApplyFillLight(fill, style);

            var fogBefore = $"{(RenderSettings.fog ? "вкл" : "выкл")}, плотность {RenderSettings.fogDensity:0.000}";

            ApplyCaveRenderSettings(style);

            changes.AppendLine($"  туман: было [{fogBefore}] -> вкл, экспоненциальный, " +
                               $"плотность {RenderSettings.fogDensity:0.000}, цвет холодный");
            changes.AppendLine("  ambient: трёхцветный, опущен — заливку теперь держит шейдер");

            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);

            if (material != null)
            {
                ApplyCaveMaterialPreset(material, style);
                changes.AppendLine("  материал: приведён к эталонным значениям, подключена карта нормалей");
            }
            else
            {
                changes.AppendLine($"  материал по пути {MaterialPath} не найден");
            }

            changes.AppendLine(EnsureFixtures(Object.FindFirstObjectByType<CatacombWorld>(), style));

            AssetDatabase.SaveAssets();

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log($"Сцена \"{scene.name}\" починена:{System.Environment.NewLine}{changes}" +
                      "Сохраните сцену (Ctrl+S), иначе при следующем открытии вернётся старое.");
        }

        // Эталонные значения света держим в одном месте.
        //
        // Раньше «Создать тестовую сцену» и «Починить свет» несли каждая свою копию, и это
        // ровно тот сорт дублирования, который здесь уже подводил: сцена сохранена, а числа
        // в коде другие, и «у меня всё по-старому» становится честной жалобой.

        /// <summary>
        /// Стиль освещения. Их два, а не один, намеренно: прошлая переделка картинки
        /// откатывалась целиком, и возврат к прежнему виду должен быть пунктом меню,
        /// а не археологией по истории git.
        /// </summary>
        private enum CaveLightStyle
        {
            /// <summary>Как было: ровная тёплая заливка, фонарь на 38 юнитов, темноты в кадре нет.</summary>
            Flat = 0,

            /// <summary>Как в DRG: короткий спад света, холодная тьма, тёплые источники в мире.</summary>
            Deep = 1
        }

        private const string StyleKey = "MineGenerator.Catacombs.LightStyle";

        private static CaveLightStyle Style
        {
            get => (CaveLightStyle)EditorPrefs.GetInt(StyleKey, (int)CaveLightStyle.Deep);
            set => EditorPrefs.SetInt(StyleKey, (int)value);
        }

        [MenuItem("Tools/Mine Generator/Свет: как в DRG (тьма и цвет)")]
        private static void UseDeepStyle()
        {
            Style = CaveLightStyle.Deep;
            RepairCurrentScene();
        }

        [MenuItem("Tools/Mine Generator/Свет: как было (ровная заливка)")]
        private static void UseFlatStyle()
        {
            Style = CaveLightStyle.Flat;
            RepairCurrentScene();
        }

        // Туман в ровном стиле нейтрально-серый, в глубоком — холодный и заметно темнее.
        // Цвет тумана здесь работает как цвет самой дали: всё, что дальше десятка юнитов,
        // окрашивается именно им, и нейтральный серый делает даль бесцветной.
        private static readonly Color FlatFogColor = new Color(0.07f, 0.065f, 0.062f);
        private static readonly Color DeepFogColor = new Color(0.018f, 0.030f, 0.042f);

        private static Color BackgroundColor(CaveLightStyle style) => style == CaveLightStyle.Deep
            ? new Color(0.008f, 0.012f, 0.018f)
            : new Color(0.02f, 0.02f, 0.03f);

        /// <summary>
        /// Фонарь игрока.
        ///
        /// Точечный и вынесен вбок и вверх — именно ради теней. Источник, стоящий ровно
        /// в камере, теней не даёт вовсе: всё, что он освещает, по определению видно,
        /// а тень прячется ровно за тем, что её отбрасывает.
        ///
        /// Направление выноса важнее его длины, и это выяснилось замером. Вынос вверх-назад
        /// (0, 1.6, −0.8) кажется безопасным, но лежит на оси взгляда: тень от предмета
        /// уходит точно за сам предмет и в кадр не попадает — 0.00% затенённых пикселей
        /// при длине 1.8. Боковой вынос той же длины даёт 0.49%, то есть тень наконец видно.
        ///
        /// Длину держим умеренной. Замер на 12 ракурсах у стен: при 1.7 черноты в кадре
        /// 0.5% (столько же, сколько без выноса вообще), при 3.2 — уже 1.5%. Чернота
        /// у стен — это тот самый дефект, из-за которого вынос когда-то убрали совсем,
        /// так что запас здесь дороже лишней десятой процента теней.
        ///
        /// Что меняет глубокий стиль — только дальность. Затухание точечного источника идёт
        /// от отношения расстояния к дальности, поэтому 38 юнитов в ходе шириной четыре
        /// означают, что спадать свету негде: на всех видимых расстояниях он почти одинаков,
        /// и кадр получается ровно залитым. При 16 юнитах свет на десяти юнитах вчетверо
        /// слабее, чем на четырёх, — появляется та самая даль, в которую он не достаёт.
        /// Яркость подняли, чтобы ближний план остался прежним.
        /// </summary>
        private static void ApplyHeadlamp(Light lamp, CaveLightStyle style)
        {
            lamp.transform.localPosition = new Vector3(1.4f, 0.9f, 0f);
            lamp.type = LightType.Point;

            var deep = style == CaveLightStyle.Deep;

            // Было 11/38 и 1.6/2.2. Те же множители, что у ламп на стенах (дальность x2,
            // яркость x8) — компенсация обратноквадратичного затухания URP, подобранная
            // замером по метрикам стиля. Замерялся глубокий стиль; ровный получил ту же
            // пару без отдельной проверки — физика у обоих одна.
            lamp.range = deep ? 22f : 76f;
            lamp.intensity = deep ? 12.8f : 17.6f;
            lamp.color = deep ? new Color(1f, 0.88f, 0.68f) : new Color(1f, 0.94f, 0.82f);

            lamp.shadows = LightShadows.Soft;
            lamp.shadowStrength = 0.85f;

            // Смещения под воксельный меш. Нормали здесь берутся из градиента плотности
            // и на гранях гуляют, поэтому обычный bias оставляет полосы самозатенения;
            // normalBias сдвигает выборку вдоль нормали и убирает их без заметного отрыва
            // тени от подножия. Ближняя плоскость маленькая: в тесных ходах тень нужна
            // от того, что в полуметре, а не в пяти.
            lamp.shadowBias = 0.05f;
            lamp.shadowNormalBias = 0.5f;
            lamp.shadowNearPlane = 0.1f;
        }

        /// <summary>
        /// Заполняющий свет с фиксированного направления — он и держит форму объёма.
        ///
        /// Теней не отбрасывает намеренно: направленный источник в замкнутой пещере весь
        /// под породой, и с тенями он не дал бы ничего, кроме сплошного затенения.
        ///
        /// В кадр он вносит около 1.4% яркости, то есть работает не яркостью, а цветом:
        /// его дело — подкрасить теневую сторону холодным, чтобы она отличалась от
        /// освещённой не только тем, что темнее. Поэтому в глубоком стиле он слабее,
        /// но насыщеннее.
        /// </summary>
        private static void ApplyFillLight(Light fill, CaveLightStyle style)
        {
            fill.transform.rotation = Quaternion.Euler(52f, -34f, 0f);
            fill.type = LightType.Directional;

            var deep = style == CaveLightStyle.Deep;

            fill.intensity = deep ? 0.25f : 0.55f;
            fill.color = deep ? new Color(0.30f, 0.52f, 0.85f) : new Color(0.72f, 0.78f, 0.95f);
            fill.shadows = LightShadows.None;
        }

        /// <summary>Ambient и туман.</summary>
        private static void ApplyCaveRenderSettings(CaveLightStyle style)
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;

            if (style == CaveLightStyle.Deep)
            {
                // Втрое темнее и вполне синий. Ambient — это ровно тот свет, который
                // приходит отовсюду одинаково, то есть светотени он не создаёт, а гасит:
                // чем он выше, тем меньше разница между освещённой гранью и теневой.
                // Насыщенность при этом бесплатна — она не поднимает яркость, а делает
                // неосвещённую породу цветной, а не серой.
                // Подняты в полтора раза против первой версии стиля. Это плата за прореженные
                // лампы: ambient не участвует в попиксельном бюджете и потому не даёт швов
                // на стыках чанков, в отличие от лишней лампы.
                RenderSettings.ambientSkyColor = new Color(0.058f, 0.090f, 0.132f);
                RenderSettings.ambientEquatorColor = new Color(0.038f, 0.054f, 0.079f);
                RenderSettings.ambientGroundColor = new Color(0.017f, 0.021f, 0.030f);

                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Exponential;

                // Вдвое плотнее, чем в ровном стиле. Туман здесь не атмосфера, а граница
                // видимого: он гасит дальний конец хода в холодную темноту и тем даёт
                // кадру третий план, которого при ровной заливке не было вовсе.
                RenderSettings.fogDensity = 0.05f;
                RenderSettings.fogColor = DeepFogColor;

                return;
            }

            RenderSettings.ambientSkyColor = new Color(0.20f, 0.21f, 0.26f);
            RenderSettings.ambientEquatorColor = new Color(0.15f, 0.15f, 0.17f);
            RenderSettings.ambientGroundColor = new Color(0.09f, 0.08f, 0.08f);

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;

            // Плотность под реальные дистанции: в тесных ходах простреливается 10-34 юнита,
            // и при 0.008 туман давал 15% на 20 юнитах — глазом не видно.
            RenderSettings.fogDensity = 0.025f;
            RenderSettings.fogColor = FlatFogColor;
        }

        /// <summary>
        /// Эталонные значения материала породы — ровно те, что лежат в ассете.
        ///
        /// Живут в коде, а не только в ассете: ассет переживает обновления генератора, и
        /// правка значений по умолчанию в шейдере до уже созданного материала не доходит.
        /// </summary>
        private static void ApplyCaveMaterialPreset(Material material, CaveLightStyle style)
        {
            if (material == null) return;

            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", GetOrCreateRockTexture());
            if (material.HasProperty("_BumpMap")) material.SetTexture("_BumpMap", GetOrCreateRockNormal());

            // Рельеф не относится к стилю света: он про поверхность, а не про источники,
            // и в обоих стилях один и тот же.
            SetFloat(material, "_BumpScale", 1.2f);

            SetFloat(material, "_TexScale", 0.22f);
            SetFloat(material, "_DetailScale", 5.7f);
            SetFloat(material, "_DetailStrength", 0.5f);
            SetFloat(material, "_DetailMid", 0.62f);

            SetFloat(material, "_Metallic", 0f);
            SetFloat(material, "_SpecAA", 2f);

            if (style == CaveLightStyle.Deep)
            {
                // Порода почти нейтральная. Тёплое альбедо под тёплым фонарём не оставляет
                // кадру ни одного другого тона: что бы ни происходило со светом, всё
                // остаётся коричневым. Тепло теперь приносят источники, а не сам камень.
                SetColor(material, "_Color", new Color(0.36f, 0.36f, 0.37f));

                // Разводим пол и потолок по температуре, а не только по яркости: пол тёплый
                // под тёплым фонарём, потолок уходит в синеву. Это единственный источник
                // второго цвета на самой породе — всё остальное красят источники.
                SetColor(material, "_FloorTint", new Color(1f, 0.96f, 0.88f));
                SetColor(material, "_WallTint", new Color(0.70f, 0.71f, 0.75f));
                SetColor(material, "_CeilingTint", new Color(0.24f, 0.28f, 0.38f));
                SetFloat(material, "_TintStrength", 0.9f);

                // Заливка в шейдере вместе с ambient давала 33% яркости кадра — это она
                // держала нижнюю границу на 0.3 и не давала появиться темноте. Здесь она
                // втрое слабее и холодная.
                SetColor(material, "_SkyFill", new Color(0.075f, 0.105f, 0.150f));
                SetColor(material, "_GroundFill", new Color(0.033f, 0.036f, 0.042f));

                // Плоская добавка, не зависящая ни от одного источника: именно она делала
                // невозможной темноту в принципе. 0.06 на альбедо породы — это и есть тот
                // пол 0.3, который видно на гистограмме.
                SetFloat(material, "_MinLight", 0.008f);
                SetFloat(material, "_FloorLevel", 0f);

                // Заворот света за терминатор спасал грани вдоль взгляда от чёрного, но он же
                // размазывает светотень. Теперь темноты бояться нечего — она стала замыслом.
                SetFloat(material, "_Wrap", 0.18f);

                // Влажный камень. Блик — единственное, что в кадре может быть ярче единицы
                // по ощущению, и он же выдаёт направление на источник, которого при
                // рассеянном свете не видно.
                SetFloat(material, "_Glossiness", 0.14f);

                EditorUtility.SetDirty(material);
                return;
            }

            SetColor(material, "_Color", new Color(0.44f, 0.41f, 0.37f));
            SetColor(material, "_FloorTint", new Color(1f, 0.98f, 0.92f));
            SetColor(material, "_WallTint", new Color(0.72f, 0.70f, 0.68f));
            SetColor(material, "_CeilingTint", new Color(0.34f, 0.34f, 0.40f));
            SetFloat(material, "_TintStrength", 0.85f);

            SetColor(material, "_SkyFill", new Color(0.30f, 0.32f, 0.38f));
            SetColor(material, "_GroundFill", new Color(0.17f, 0.15f, 0.14f));
            SetFloat(material, "_MinLight", 0.06f);
            SetFloat(material, "_FloorLevel", 0.03f);

            SetFloat(material, "_Wrap", 0.25f);
            SetFloat(material, "_Glossiness", 0.06f);

            EditorUtility.SetDirty(material);
        }

        /// <summary>
        /// Светильники уровня нужны только глубокому стилю: при ровной заливке их свет
        /// теряется в ней целиком, а считаться источники всё равно будут.
        /// </summary>
        private static string EnsureFixtures(CatacombWorld world, CaveLightStyle style)
        {
            if (world == null) return "  светильники: мир не найден";

            // Сцены от прошлой версии несут удалённый CaveGlowVeins битой ссылкой. Она
            // не рендерит, но висит в инспекторе и сыплет предупреждения при каждом входе
            // в play-режим.
            var stale = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(world.gameObject);

            var fixtures = world.GetComponent<CaveFixtures>();

            if (style == CaveLightStyle.Flat)
            {
                EnsureWebs(world, style);

                if (fixtures == null) return "  светильники: в этом стиле не нужны";

                fixtures.Clear();
                Object.DestroyImmediate(fixtures);

                // В ровной заливке весь уровень светится сразу, и генераторам нечего
                // зажигать: механика темноты в этом стиле не работает в принципе.
                var strayGenerators = world.GetComponent<CaveGenerators>();

                if (strayGenerators != null)
                {
                    strayGenerators.Clear();
                    Object.DestroyImmediate(strayGenerators);
                }

                return "  светильники: убраны вместе с генераторами";
            }

            // Существующий компонент не донастраиваем, а сносим и создаём заново — иначе
            // правка значений по умолчанию в коде до сцены не доходит: поля живут в
            // сохранённом файле сцены, и «я поменял число» превращается в «у меня
            // всё по-старому». Ценой этого пункт меню сбрасывает ручную настройку
            // в инспекторе, и это его работа: он называется «починить».
            var rebuilt = fixtures != null;

            if (rebuilt)
            {
                fixtures.Clear();
                Object.DestroyImmediate(fixtures);
            }

            fixtures = world.gameObject.AddComponent<CaveFixtures>();

            var fixturesObject = new SerializedObject(fixtures);
            fixturesObject.FindProperty("world").objectReferenceValue = world;
            fixturesObject.ApplyModifiedPropertiesWithoutUndo();

            fixtures.Rebuild();

            var generators = EnsureGenerators(world, fixtures);

            EnsureWebs(world, style);
            EnsureMood(world);

            var what = rebuilt ? "пересозданы" : "добавлены";

            var line = stale > 0
                ? $"  светильники: {what}, убрано битых компонентов {stale}"
                : $"  светильники: {what}";

            return line + System.Environment.NewLine + generators;
        }

        /// <summary>
        /// Генераторы света — цель забега. Пересоздаются так же, как светильники,
        /// и по той же причине: значения полей живут в сохранённой сцене, и правка
        /// умолчаний в коде до них не доходит (грабли №8).
        /// </summary>
        private static string EnsureGenerators(CatacombWorld world, CaveFixtures fixtures)
        {
            var generators = world.GetComponent<CaveGenerators>();
            var rebuilt = generators != null;

            if (rebuilt)
            {
                generators.Clear();
                Object.DestroyImmediate(generators);
            }

            generators = world.gameObject.AddComponent<CaveGenerators>();

            var serialized = new SerializedObject(generators);
            serialized.FindProperty("world").objectReferenceValue = world;
            serialized.FindProperty("fixtures").objectReferenceValue = fixtures;
            serialized.FindProperty("crowd").objectReferenceValue =
                Object.FindFirstObjectByType<SpiderCrowd>();
            serialized.ApplyModifiedPropertiesWithoutUndo();

            generators.Rebuild();

            var what = rebuilt ? "пересозданы" : "добавлены";

            return $"  генераторы: {what}, расставлено {generators.Count}, " +
                   $"светильников горит {fixtures.LitCount} из {fixtures.Count}";
        }

        /// <summary>
        /// Паутина в щелях. Как и светильники, компонент пересоздаётся, а не донастраивается:
        /// значения полей живут в сохранённой сцене, и правка умолчаний в коде до них
        /// не доходит.
        /// </summary>
        private static void EnsureWebs(CatacombWorld world, CaveLightStyle style)
        {
            if (world == null) return;

            var webs = world.GetComponent<CaveWebs>();

            if (webs != null)
            {
                webs.Clear();
                Object.DestroyImmediate(webs);
            }

            // В ровном стиле паутины нет: она держится на контрасте с темнотой, а при
            // сплошной заливке превращается в серое пятно на серой стене.
            if (style == CaveLightStyle.Flat) return;

            webs = world.gameObject.AddComponent<CaveWebs>();

            var so = new SerializedObject(webs);
            so.FindProperty("world").objectReferenceValue = world;

            FillWebPrefabs(so.FindProperty("webPrefabs"));

            so.ApplyModifiedPropertiesWithoutUndo();

            webs.Rebuild();
        }

        /// <summary>
        /// Раскладывает по компоненту готовые меши паутины из пака PolyOne Cobwebs.
        ///
        /// Делается здесь, а не в самом компоненте: <see cref="CaveWebs"/> работает
        /// и в плеере, а поиск ассетов по пути — это AssetDatabase, то есть редактор.
        /// Компонент про пак ничего не знает и съест любой набор префабов, лишь бы
        /// меш лежал в плоскости XY.
        /// </summary>
        private static void FillWebPrefabs(SerializedProperty property)
        {
            if (property == null) return;

            if (!Directory.Exists(WebPrefabFolder))
            {
                Debug.LogWarning($"Паутина: папка {WebPrefabFolder} не найдена, паутины не будет");
                property.arraySize = 0;
                return;
            }

            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { WebPrefabFolder });

            // Порядок FindAssets не обещан, а сид уровня должен давать одну и ту же
            // паутину от прогона к прогону — иначе не сравнить два кадра одной сцены.
            var paths = new List<string>();

            foreach (var guid in guids) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            paths.Sort(System.StringComparer.Ordinal);

            property.arraySize = paths.Count;

            for (var i = 0; i < paths.Count; i++)
                property.GetArrayElementAtIndex(i).objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<GameObject>(paths[i]);
        }

        /// <summary>Настроение этажа — цвет дали по тому, куда спустился игрок.</summary>
        private static void EnsureMood(CatacombWorld world)
        {
            if (world == null || world.GetComponent<CaveLevelMood>() != null) return;

            var mood = world.gameObject.AddComponent<CaveLevelMood>();

            var so = new SerializedObject(mood);
            so.FindProperty("world").objectReferenceValue = world;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Пыль висит на камере, а не на мире: она нужна только рядом с игроком.</summary>
        private static string EnsureDust(GameObject player)
        {
            if (player == null) return "  пыль: игрок не найден";
            if (player.GetComponent<CaveDustMotes>() != null) return "  пыль: уже есть";

            player.AddComponent<CaveDustMotes>();
            return "  пыль: добавлена";
        }

        private static string RemoveDust(GameObject player)
        {
            var dust = player != null ? player.GetComponent<CaveDustMotes>() : null;
            if (dust == null) return "  пыль: в этом стиле не нужна";

            Object.DestroyImmediate(dust);
            return "  пыль: убрана";
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        private static void SetColor(Material material, string property, Color value)
        {
            if (material.HasProperty(property)) material.SetColor(property, value);
        }

        private static Light FindDirectional()
        {
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional) return light;
            }

            return null;
        }

        [MenuItem("Tools/Mine Generator/Пересоздать материал породы")]
        private static void RegenerateMaterial()
        {
            AssetDatabase.DeleteAsset(TexturePath);
            AssetDatabase.DeleteAsset(NormalPath);
            AssetDatabase.DeleteAsset(MaterialPath);

            var material = GetOrCreateCaveMaterial();

            // Материал пересоздан — значит у него новый GUID, и ссылка в настройках
            // указывает в никуда. Без этого чанки после пересборки становятся розовыми.
            var settings = AssetDatabase.LoadAssetAtPath<CatacombSettings>(SettingsPath);
            if (settings != null)
            {
                AssignMaterial(settings, material);
                AssetDatabase.SaveAssets();
            }

            foreach (var world in Object.FindObjectsByType<CatacombWorld>(FindObjectsSortMode.None))
            {
                if (world.Chunks.Count > 0) world.Generate(world.CurrentSeed);
            }

            EditorGUIUtility.PingObject(material);
        }

        private static CatacombSettings CreateSettingsAsset()
        {
            // Материал и текстура создаются первыми. Их импорт дёргает AssetDatabase,
            // а тот инвалидирует уже загруженные ссылки на ассеты: если сначала взять
            // настройки, к моменту записи материала объект окажется уничтоженным.
            var material = GetOrCreateCaveMaterial();

            var existing = AssetDatabase.LoadAssetAtPath<CatacombSettings>(SettingsPath);
            if (existing != null)
            {
                if (existing.IsPresetOutdated)
                {
                    ApplyReferencePreset(existing);
                    Debug.Log("Найден ассет настроек от прошлой версии генератора — значения приведены к эталонным.");
                }

                EnsureCaveMaterial(existing, material);
                AssetDatabase.SaveAssets();
                return existing;
            }

            EnsureFolder(Path.GetDirectoryName(SettingsPath));

            var settings = CreateInstance<CatacombSettings>();
            AssetDatabase.CreateAsset(settings, SettingsPath);

            ApplyReferencePreset(settings);
            AssignMaterial(settings, material);

            AssetDatabase.SaveAssets();
            return settings;
        }

        /// <summary>
        /// Ставит материал породы, если сейчас стоит не он. Ассет настроек переживает
        /// обновления генератора, и в нём мог остаться материал от прошлой версии —
        /// именно так уровень оставался залит одним цветом.
        /// </summary>
        private static void EnsureCaveMaterial(CatacombSettings settings, Material caveMaterial = null)
        {
            if (settings == null) return;

            var current = settings.Material;
            if (current != null && current.shader != null && current.shader.name == CaveShaderName) return;

            AssignMaterial(settings, caveMaterial != null ? caveMaterial : GetOrCreateCaveMaterial());
            AssetDatabase.SaveAssets();
        }

        private static void AssignMaterial(CatacombSettings settings, Material material)
        {
            if (settings == null || material == null) return;

            var so = new SerializedObject(settings);
            so.FindProperty("material").objectReferenceValue = material;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(settings);
        }

        private static Material GetOrCreateCaveMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);

            if (existing != null) return existing;

            // Запасной — URP-овский Lit. Built-in Standard в URP не собирается вовсе
            // и даёт розовую породу, то есть «запасной вариант», который хуже отсутствия.
            var shader = Shader.Find(CaveShaderName) ?? Shader.Find("Universal Render Pipeline/Lit");

            var material = new Material(shader);

            // Не только текстуры, а весь эталон: значения по умолчанию из шейдера
            // до созданного материала уже не доходят, а разъезжаться им нельзя.
            ApplyCaveMaterialPreset(material, Style);

            EnsureFolder(Path.GetDirectoryName(MaterialPath));
            AssetDatabase.CreateAsset(material, MaterialPath);
            AssetDatabase.SaveAssets();

            return material;
        }


        /// <summary>
        /// Бесшовная текстура камня из value-noise. Нужна, чтобы трипланарный шейдер
        /// давал масштаб и фактуру: без неё поверхность выглядит однотонной заливкой.
        /// </summary>
        private static Texture2D GetOrCreateRockTexture()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            if (existing != null) return existing;

            const int size = 512;

            var texture = new Texture2D(size, size, TextureFormat.RGB24, false);
            var pixels = new Color32[size * size];

            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var value = RockHeight(x, y, size, true);

                var r = Mathf.Clamp01(value * 1.04f);
                var g = Mathf.Clamp01(value * 0.99f);
                var b = Mathf.Clamp01(value * 0.90f);

                pixels[y * size + x] = new Color32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), 255);
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            EnsureFolder(Path.GetDirectoryName(TexturePath));
            File.WriteAllBytes(TexturePath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(TexturePath, ImportAssetOptions.ForceSynchronousImport);

            var importer = (TextureImporter)AssetImporter.GetAtPath(TexturePath);
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;

            importer.anisoLevel = 4;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();

            return AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
        }


        /// <summary>
        /// Высота породы в точке текстуры. Одна и та же для альбедо и для карты нормалей —
        /// иначе рельеф лёг бы мимо рисунка, и камень поехал бы двумя разными слоями.
        /// </summary>
        /// <param name="pixelGrain">
        /// Крошка на уровне одного пикселя. Нужна альбедо, чтобы поверхность не была мыльной,
        /// но в карту нормалей не идёт: центральная разность по соседним пикселям превратила бы
        /// её в чистый шум, который вдобавок рассыпается муаром на дальних мипах.
        /// </param>
        private static float RockHeight(int x, int y, int size, bool pixelGrain)
        {
            var u = x / (float)size;
            var v = y / (float)size;

            // Крупная порода, поверх неё зерно, поверх — тонкие трещины.
            var body = Fbm(u, v, 6, 5);
            var grain = Fbm(u, v, 48, 3);

            // Складка модулем даёт узкий гребень; высокая степень сужает его до трещины.
            var ridge = 1f - Mathf.Abs(Fbm(u, v, 20, 4) * 2f - 1f);
            var cracks = Mathf.Pow(Mathf.Clamp01(ridge), 18f);

            // Без маски трещины идут сплошной сеткой и порода читается как кракелюр.
            var crackMask = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Fbm(u, v, 3, 2) - 0.42f) * 3f));

            var value = 0.60f + (body - 0.5f) * 0.55f + (grain - 0.5f) * 0.26f;
            value -= cracks * crackMask * 0.30f;

            if (pixelGrain) value += (LatticeValue(x, y, size, 913) - 0.5f) * 0.06f;

            // Контраст: без него всё сползается в один средний тон.
            return Mathf.Clamp01(0.5f + (value - 0.5f) * 1.35f);
        }

        /// <summary>
        /// Карта нормалей породы из того же поля высот, что и альбедо.
        ///
        /// Центральная разность по соседям, с заворотом через край: текстура бесшовная,
        /// и шов в карте нормалей был бы виден как ровная светлая линия поперёк стены.
        /// </summary>
        private static Texture2D GetOrCreateRockNormal()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(NormalPath);
            if (existing != null) return existing;

            const int size = 512;

            // Наклон в единицах высоты на пиксель. Число подбирается глазом и потом
            // домножается на _BumpScale в материале — здесь важно лишь не упереться
            // в единицу по осям, иначе рельеф начнёт срезаться на гребнях.
            const float slope = 6f;

            var texture = new Texture2D(size, size, TextureFormat.RGB24, false);
            var pixels = new Color32[size * size];

            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var left = RockHeight((x - 1 + size) % size, y, size, false);
                var right = RockHeight((x + 1) % size, y, size, false);
                var down = RockHeight(x, (y - 1 + size) % size, size, false);
                var up = RockHeight(x, (y + 1) % size, size, false);

                var normal = new Vector3((left - right) * slope, (down - up) * slope, 1f).normalized;

                pixels[y * size + x] = new Color32(
                    (byte)((normal.x * 0.5f + 0.5f) * 255),
                    (byte)((normal.y * 0.5f + 0.5f) * 255),
                    (byte)((normal.z * 0.5f + 0.5f) * 255),
                    255);
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            EnsureFolder(Path.GetDirectoryName(NormalPath));
            File.WriteAllBytes(NormalPath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(NormalPath, ImportAssetOptions.ForceSynchronousImport);

            var importer = (TextureImporter)AssetImporter.GetAtPath(NormalPath);

            // Тип NormalMap обязателен: от него зависит раскладка каналов при сжатии,
            // и UnpackNormal в шейдере рассчитывает именно на неё. convertToNormalmap
            // при этом выключен — в файле уже нормали, а не высоты.
            importer.textureType = TextureImporterType.NormalMap;
            importer.convertToNormalmap = false;

            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 4;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();

            return AssetDatabase.LoadAssetAtPath<Texture2D>(NormalPath);
        }

        /// <summary>Value-noise с периодом по решётке — края текстуры сходятся без шва.</summary>
        private static float Fbm(float u, float v, int period, int octaves)
        {
            var sum = 0f;
            var amplitude = 1f;
            var norm = 0f;
            var current = period;

            for (var i = 0; i < octaves; i++)
            {
                sum += TileableNoise(u, v, current, i * 71) * amplitude;
                norm += amplitude;

                amplitude *= 0.5f;
                current *= 2;
            }

            return sum / Mathf.Max(norm, 1e-4f);
        }

        private static float TileableNoise(float u, float v, int period, int salt)
        {
            var x = u * period;
            var y = v * period;

            var x0 = Mathf.FloorToInt(x);
            var y0 = Mathf.FloorToInt(y);

            var fx = x - x0;
            var fy = y - y0;

            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);

            var c00 = LatticeValue(x0, y0, period, salt);
            var c10 = LatticeValue(x0 + 1, y0, period, salt);
            var c01 = LatticeValue(x0, y0 + 1, period, salt);
            var c11 = LatticeValue(x0 + 1, y0 + 1, period, salt);

            return Mathf.Lerp(Mathf.Lerp(c00, c10, fx), Mathf.Lerp(c01, c11, fx), fy);
        }

        private static float LatticeValue(int x, int y, int period, int salt)
        {
            // Заворачиваем координаты решётки — отсюда бесшовность.
            x = ((x % period) + period) % period;
            y = ((y % period) + period) % period;

            var h = x * 374761393 + y * 668265263 + salt * 1274126177;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;

            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }

        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || Directory.Exists(folder)) return;

            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
        }

        private static CatacombWorld CreateWorld(CatacombSettings settings)
        {
            var go = new GameObject("Catacombs");
            var world = go.AddComponent<CatacombWorld>();

            var serialized = new SerializedObject(world);
            serialized.FindProperty("settings").objectReferenceValue = settings;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Намеренно не регистрируем в undo: Ctrl+Z снёс бы объект вместе с чанками,
            // которые в undo не заведены, и консоль завалило бы dangling child.
            // Пересоздать мир дешевле, чем отменять.
            return world;
        }
    }
}

