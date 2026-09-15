using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Запекает анимацию пауков из пака в текстуры, пригодные для отрисовки толпой.
    ///
    /// Зачем это нужно. В паке паук — это SkinnedMeshRenderer на 49 костей и 73 трансформа,
    /// причём Animator Controller к нему не приложен вовсе. Заводить контроллер и вешать
    /// по Animator на каждую особь нельзя: на трёх сотнях особей это двадцать с лишним
    /// тысяч трансформов, которые Unity обходит на главном потоке, а воркеров на WebGL нет.
    ///
    /// Поэтому скиннинг считается ЗДЕСЬ, один раз, покадрово: каждый кадр клипа снимается
    /// через SkinnedMeshRenderer.BakeMesh и укладывается строкой в текстуру. В рантайме
    /// от анимации остаётся выборка текстуры в вершинном шейдере, а все особи вида идут
    /// одним мешем через инстансинг.
    ///
    /// Из шести клипов пака берутся три. Остальные стоят места в загрузке и не нужны:
    /// про клипы стояния и второй удар см. комментарий у <see cref="SpiderClip"/>.
    /// </summary>
    public static class SpiderVatBaker
    {
        private const string PrefabFolder =
            "Assets/StorePackages/Spiders - characters with animations/" +
            "Spiders - characters with animations/Models/Prefabs";

        private const string OutputFolder = "Assets/Spiders";

        private const string ShaderName = "Mine Generator/Cave Crowd";

        /// <summary>
        /// Виды по умолчанию: два мелких и два крупных.
        ///
        /// Не все десять намеренно. Каждый вид это своя пара текстур примерно на
        /// три четверти мегабайта, и десять видов дали бы семь с лишним мегабайт
        /// в загрузке WebGL ради разнообразия, которое в тёмном коридоре на дистанции
        /// боя всё равно не читается. Четырёх хватает, чтобы толпа не выглядела
        /// размноженной копией; остальные доступны через «запечь выделенные».
        /// </summary>
        private static readonly string[] DefaultKinds =
        {
            "little_spider",
            "karakurt",
            "spider_cross",
            "tarantula"
        };

        /// <summary>
        /// Во сколько раз увеличить каждый вид.
        ///
        /// В натуральную величину паук из пака около юнита поперёк при коридоре
        /// в три с половиной — в кадре это мышь, а не угроза. Но и одинаково крупными
        /// их делать нельзя: при трёх юнитах поперёк одна особь перекрывает ход целиком,
        /// и толпа вырождается в очередь по одному. Поэтому размер разведён по видам:
        /// крупные читаются силуэтом, мелкие заполняют промежутки и лезут по стенам.
        ///
        /// Кто здесь не назван, получает средний множитель.
        /// </summary>
        private static readonly (string Kind, float Scale)[] Scales =
        {
            ("little_spider", 2.2f),
            ("karakurt", 2.6f),
            ("spider_cross", 3.1f),
            ("tarantula", 3.6f)
        };

        private const float DefaultScale = 2.8f;

        private static float ScaleFor(string name)
        {
            foreach (var (kind, scale) in Scales)
            {
                if (kind == name) return scale;
            }

            return DefaultScale;
        }

        /// <summary>Какие клипы пака во что превращаются. Порядок совпадает с <see cref="SpiderClip"/>.</summary>
        private static readonly (string Clip, bool Loop)[] Wanted =
        {
            ("walk", true),
            ("attack_1", false),
            ("dead", false)
        };

        /// <summary>
        /// Точка входа для batchmode: запечь набор по умолчанию и выйти с кодом.
        /// Редактор при этом должен быть закрыт — иначе проектный лок занят.
        /// </summary>
        public static void Run()
        {
            var code = 0;

            try
            {
                BakeDefault();
            }
            catch (System.Exception error)
            {
                Debug.LogError("ЗАПЕКАНИЕ УПАЛО: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        [MenuItem("Tools/Mine Generator/Пауки: запечь анимацию в текстуры")]
        private static void BakeDefault()
        {
            var prefabs = new List<GameObject>();

            foreach (var name in DefaultKinds)
            {
                var path = $"{PrefabFolder}/{name}.prefab";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                if (prefab == null)
                {
                    Debug.LogWarning($"Паук не найден: {path}");
                    continue;
                }

                prefabs.Add(prefab);
            }

            Bake(prefabs);
        }

        [MenuItem("Tools/Mine Generator/Пауки: запечь выделенные префабы")]
        private static void BakeSelection()
        {
            var prefabs = Selection.GetFiltered<GameObject>(SelectionMode.Assets)
                .Where(go => go.GetComponentInChildren<SkinnedMeshRenderer>() != null)
                .ToList();

            if (prefabs.Count == 0)
            {
                Debug.LogWarning("Выделите в проекте префабы со SkinnedMeshRenderer.");
                return;
            }

            Bake(prefabs);
        }

        private static void Bake(List<GameObject> prefabs)
        {
            if (prefabs.Count == 0) return;

            EnsureFolder(OutputFolder);

            var shader = Shader.Find(ShaderName);

            if (shader == null)
            {
                Debug.LogError($"Не найден шейдер «{ShaderName}». Он лежит рядом с породой, " +
                               "в Catacombs/Rendering/CaveCrowd.shader.");
                return;
            }

            var report = new StringBuilder();
            var baked = new List<SpiderKind>();

            try
            {
                for (var i = 0; i < prefabs.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("Запекание пауков", prefabs[i].name,
                        i / (float)prefabs.Count);

                    var kind = BakeOne(prefabs[i], shader, report);

                    if (kind != null) baked.Add(kind);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            report.AppendLine(AssignToScene(baked));

            Debug.Log($"Запечено видов: {baked.Count}{System.Environment.NewLine}{report}");
        }

        private static SpiderKind BakeOne(GameObject prefab, Shader shader, StringBuilder report)
        {
            var source = prefab.GetComponentInChildren<SkinnedMeshRenderer>();

            if (source == null || source.sharedMesh == null)
            {
                report.AppendLine($"  {prefab.name}: нет SkinnedMeshRenderer — пропущен");
                return null;
            }

            var modelPath = AssetDatabase.GetAssetPath(source.sharedMesh);
            var clips = LoadClips(modelPath);

            var plan = new List<(AnimationClip Clip, bool Loop, int Frames)>();
            var totalRows = 0;

            foreach (var (name, loop) in Wanted)
            {
                if (!clips.TryGetValue(name, out var clip))
                {
                    report.AppendLine($"  {prefab.name}: нет клипа «{name}» — вид пропущен");
                    return null;
                }

                // Кадров ровно столько, сколько в исходнике: пак нарисован на 25 кадрах
                // в секунду, и брать чаще нечего — промежуточных поз в клипе нет.
                var frames = Mathf.Clamp(Mathf.RoundToInt(clip.length * clip.frameRate), 2, 128);

                plan.Add((clip, loop, frames));
                totalRows += frames;
            }

            var vertexCount = source.sharedMesh.vertexCount;

            var positions = new Color[vertexCount * totalRows];
            var normals = new Color[vertexCount * totalRows];

            var instance = Object.Instantiate(prefab);
            instance.hideFlags = HideFlags.HideAndDontSave;
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            instance.transform.localScale = Vector3.one;

            var renderer = instance.GetComponentInChildren<SkinnedMeshRenderer>();

            var snapshot = new Mesh { name = "snapshot" };
            var ranges = new SpiderClipRange[plan.Count];

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            Vector3[] firstFrame = null;
            Vector3[] firstNormals = null;

            AnimationMode.StartAnimationMode();

            try
            {
                var row = 0;

                for (var c = 0; c < plan.Count; c++)
                {
                    var (clip, loop, frames) = plan[c];

                    ranges[c] = new SpiderClipRange
                    {
                        Name = clip.name,
                        StartRow = row,
                        FrameCount = frames,
                        Length = clip.length,
                        Loop = loop
                    };

                    for (var f = 0; f < frames; f++)
                    {
                        // У кольцевого клипа последний кадр совпадает с нулевым, поэтому
                        // шкала берётся без правого конца; у одноразового конец нужен —
                        // это финальная поза, на которой клип замирает.
                        var t = loop
                            ? clip.length * f / frames
                            : clip.length * f / Mathf.Max(1, frames - 1);

                        AnimationMode.BeginSampling();
                        AnimationMode.SampleAnimationClip(instance, clip, t);
                        AnimationMode.EndSampling();

                        // useScale = false: масштаб придёт вместе с матрицей трансформа
                        // ниже, а учтённый дважды он даёт паука в квадрате размера.
                        renderer.BakeMesh(snapshot, false);

                        // BakeMesh возвращает вершины в системе самого рендерера, а меш
                        // в паке лежит под дочерним объектом со своим сдвигом. Без перевода
                        // в систему корня префаба вся толпа оказывалась бы смещена
                        // относительно точки, за которую её двигает поведение.
                        var toRoot = instance.transform.worldToLocalMatrix *
                                     renderer.transform.localToWorldMatrix;

                        var verts = snapshot.vertices;
                        var norms = snapshot.normals;

                        for (var v = 0; v < vertexCount; v++)
                        {
                            var p = toRoot.MultiplyPoint3x4(verts[v]);
                            var n = toRoot.MultiplyVector(v < norms.Length ? norms[v] : Vector3.up).normalized;

                            var index = row * vertexCount + v;

                            positions[index] = new Color(p.x, p.y, p.z, 1f);
                            normals[index] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);

                            min = Vector3.Min(min, p);
                            max = Vector3.Max(max, p);
                        }

                        if (firstFrame == null)
                        {
                            firstFrame = new Vector3[vertexCount];
                            firstNormals = new Vector3[vertexCount];

                            for (var v = 0; v < vertexCount; v++)
                            {
                                firstFrame[v] = toRoot.MultiplyPoint3x4(verts[v]);
                                firstNormals[v] = toRoot.MultiplyVector(v < norms.Length ? norms[v] : Vector3.up)
                                    .normalized;
                            }
                        }

                        row++;
                    }
                }
            }
            finally
            {
                AnimationMode.StopAnimationMode();

                Object.DestroyImmediate(snapshot);
                Object.DestroyImmediate(instance);
            }

            var safeName = prefab.name.Replace(' ', '_');

            var bounds = new Bounds();
            bounds.SetMinMax(min, max);

            var positionMap = WriteTexture($"{OutputFolder}/{safeName} Positions.asset", vertexCount, totalRows,
                TextureFormat.RGBAHalf, positions);

            var normalMap = WriteTexture($"{OutputFolder}/{safeName} Normals.asset", vertexCount, totalRows,
                TextureFormat.RGBA32, normals);

            var mesh = WriteMesh($"{OutputFolder}/{safeName} Crowd.asset", safeName, source.sharedMesh,
                firstFrame, firstNormals, bounds);

            var material = WriteMaterial($"{OutputFolder}/{safeName} Crowd.mat", shader, source.sharedMaterial,
                positionMap, normalMap);

            var kind = WriteKind($"{OutputFolder}/{safeName}.asset", prefab.name, mesh, material, positionMap, normalMap,
                ranges, bounds);

            report.AppendLine($"  {prefab.name}: вершин {vertexCount}, строк {totalRows} " +
                              $"({string.Join(", ", ranges.Select(r => $"{r.Name} {r.FrameCount}"))}), " +
                              $"габариты {bounds.size.x:0.00}x{bounds.size.y:0.00}x{bounds.size.z:0.00}, " +
                              $"текстуры {Size(vertexCount * totalRows * 8)} + {Size(vertexCount * totalRows * 4)}");

            return kind;
        }

        private static Dictionary<string, AnimationClip> LoadClips(string modelPath)
        {
            var result = new Dictionary<string, AnimationClip>();

            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(modelPath))
            {
                // Превью-клипы Unity заводит сама для окна предпросмотра; они обрезаны
                // до секунды и в игру не годятся.
                if (asset is not AnimationClip clip || clip.name.StartsWith("__preview__")) continue;

                result[clip.name] = clip;
            }

            return result;
        }

        /// <summary>
        /// Пишет карту анимации ассетом, а не картинкой.
        ///
        /// Именно ассетом: тексель здесь не цвет, а координата, и любой импортёр
        /// картинок по дороге применил бы к ней гамму, сжатие и мип-уровни. Из PNG
        /// половина точности позиции ушла бы в пересчёт цветового пространства ещё
        /// до первого кадра.
        /// </summary>
        private static Texture2D WriteTexture(string path, int width, int height, TextureFormat format,
            Color[] pixels)
        {
            var texture = new Texture2D(width, height, format, false, true)
            {
                name = Path.GetFileNameWithoutExtension(path),

                // Точечная фильтрация обязательна: соседние тексели по горизонтали — это
                // РАЗНЫЕ вершины, и смешивать их означает тянуть вершину к соседу
                // по номеру в буфере.
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0
            };

            texture.SetPixels(pixels);
            texture.Apply(false, false);

            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            if (existing != null)
            {
                EditorUtility.CopySerialized(texture, existing);
                Object.DestroyImmediate(texture);

                EditorUtility.SetDirty(existing);
                return existing;
            }

            AssetDatabase.CreateAsset(texture, path);
            return texture;
        }

        private static Mesh WriteMesh(string path, string name, Mesh source, Vector3[] vertices, Vector3[] normals,
            Bounds bounds)
        {
            var mesh = new Mesh { name = $"{name} Crowd", indexFormat = source.indexFormat };

            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);

            var uv = source.uv;
            if (uv != null && uv.Length == vertices.Length) mesh.SetUVs(0, uv);

            // UV1 несёт одно число — столбец вершины в карте анимации. Через SV_VertexID
            // было бы короче, но это лишнее требование к платформе там, где хватает
            // обычного атрибута, который всё равно едет в вершинном буфере.
            var vat = new Vector2[vertices.Length];

            for (var i = 0; i < vertices.Length; i++)
            {
                vat[i] = new Vector2((i + 0.5f) / vertices.Length, 0.5f);
            }

            mesh.SetUVs(1, vat);

            mesh.subMeshCount = 1;
            mesh.SetTriangles(source.triangles, 0);

            // Границы по ВСЕМ кадрам, а не по первому: иначе на замахе паук вылезает
            // за свои границы и Unity отсекает его как невидимого ровно в момент удара.
            mesh.bounds = bounds;

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);

            if (existing != null)
            {
                EditorUtility.CopySerialized(mesh, existing);
                Object.DestroyImmediate(mesh);

                EditorUtility.SetDirty(existing);
                return existing;
            }

            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        private static Material WriteMaterial(string path, Shader shader, Material source, Texture positions,
            Texture normals)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;

            var albedo = source != null
                ? source.HasProperty("_BaseMap") ? source.GetTexture("_BaseMap") :
                  source.HasProperty("_MainTex") ? source.GetTexture("_MainTex") : null
                : null;

            if (albedo != null) material.SetTexture("_BaseMap", albedo);

            material.SetTexture("_VatPositions", positions);
            material.SetTexture("_VatNormals", normals);

            // Без этого RenderMeshInstanced рисует партию одним экземпляром: свойства
            // инстанса просто некуда положить.
            material.enableInstancing = true;

            EditorUtility.SetDirty(material);
            return material;
        }

        private static SpiderKind WriteKind(string path, string prefabName, Mesh mesh, Material material, Texture2D positions,
            Texture2D normals, SpiderClipRange[] clips, Bounds bounds)
        {
            var kind = AssetDatabase.LoadAssetAtPath<SpiderKind>(path);
            var created = kind == null;

            if (created)
            {
                kind = ScriptableObject.CreateInstance<SpiderKind>();
                AssetDatabase.CreateAsset(kind, path);
            }

            kind.Mesh = mesh;
            kind.Material = material;
            kind.Positions = positions;
            kind.Normals = normals;
            kind.Clips = clips;
            kind.RestBounds = bounds;

            // Числа поведения трогаем при создании и когда набор в ассете устарел.
            // Просто «всегда» нельзя: повторная запечка сбрасывала бы настройку,
            // которую подбирали руками по ощущению от игры. Просто «только при создании»
            // тоже нельзя: правка умолчаний в коде до готовых ассетов не доходит,
            // и на этом в проекте уже обжигались (грабли №8).
            if (created || kind.IsTuningOutdated)
            {
                var size = bounds.size;
                var span = Mathf.Max(size.x, size.z);

                kind.Scale = ScaleFor(prefabName);

                // Половина поперечника: тела соприкасаются, но не срастаются.
                //
                // Было 0.35, и при пауке в юнит это выглядело правильно — особи слегка
                // наползали друг на друга, решётки не получалось, а перекрытие в полметра
                // глазом не ловилось. После увеличения в три с лишним раза то же самое
                // перекрытие стало метровым: на снятом кадре толпа читалась не как
                // множество пауков, а как сплошная масса хитина, в которой тела слиты.
                //
                // Выше половины поднимать не стоит: тогда особи держат дистанцию
                // по описанной окружности и в узком ходе выстраиваются решёткой.
                kind.BodyRadius = Mathf.Max(0.12f, span * 0.5f);
                kind.AttackRange = kind.BodyRadius * 2f + 0.6f;
                kind.StrideLength = Mathf.Max(0.3f, size.z * 1.1f);
                // Зазор до камня. Не косметика: глубина до поверхности интерполируется
                // линейно по клеткам, и в вогнутом углу она завышена — особь, которую
                // туда впихнули соседи, по полю стоит снаружи, а по плотности уже в камне.
                // Зазор это покрывает. Замер: при 0.06 в породе оказывалось 5.1% толпы
                // после усиления расталкивания, при 0.12 — вдвое меньше.
                kind.Hover = 0.12f;

                kind.MarkTuned();
            }

            EditorUtility.SetDirty(kind);
            return kind;
        }

        /// <summary>Подставляет запечённые виды в толпу на сцене, если она там есть.</summary>
        private static string AssignToScene(List<SpiderKind> baked)
        {
            if (baked.Count == 0) return "  толпа на сцене не тронута: запекать было нечего";

            var crowd = Object.FindFirstObjectByType<SpiderCrowd>();

            if (crowd == null) return "  толпы на сцене нет — добавьте её пунктом «Пауки: добавить толпу на сцену»";

            var serialized = new SerializedObject(crowd);
            var array = serialized.FindProperty("kinds");

            array.arraySize = baked.Count;

            for (var i = 0; i < baked.Count; i++)
            {
                array.GetArrayElementAtIndex(i).objectReferenceValue = baked[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(crowd.gameObject.scene);

            return $"  в толпу на сцене подставлено видов: {baked.Count} (сохраните сцену)";
        }

        [MenuItem("Tools/Mine Generator/Пауки: добавить толпу на сцену")]
        private static void AddCrowd()
        {
            var world = Object.FindFirstObjectByType<CatacombWorld>();

            if (world == null)
            {
                Debug.LogWarning("На сцене нет CatacombWorld. Сначала соберите тестовую сцену.");
                return;
            }

            // Пересоздаём, а не донастраиваем, и это тот же урок, что с «Починить свет»:
            // значения полей живут в сохранённой сцене, и правка умолчаний в коде до них
            // не доходит. Настроенная однажды толпа осталась бы с прежним размером клетки
            // навсегда, а его подбирали перебором и он менялся.
            var existing = Object.FindFirstObjectByType<SpiderCrowd>();

            if (existing != null) Object.DestroyImmediate(existing.gameObject);

            var go = new GameObject("Spider Crowd");
            go.transform.SetParent(world.transform.parent, false);

            var crowd = go.AddComponent<SpiderCrowd>();

            var serialized = new SerializedObject(crowd);
            serialized.FindProperty("world").objectReferenceValue = world;

            var rig = Object.FindFirstObjectByType<CatacombTestRig>();
            if (rig != null) serialized.FindProperty("target").objectReferenceValue = rig.transform;

            var kinds = AssetDatabase.FindAssets("t:SpiderKind", new[] { OutputFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<SpiderKind>)
                .Where(k => k != null && k.IsReady)
                .ToList();

            var array = serialized.FindProperty("kinds");
            array.arraySize = kinds.Count;

            for (var i = 0; i < kinds.Count; i++)
            {
                array.GetArrayElementAtIndex(i).objectReferenceValue = kinds[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();

            Selection.activeObject = go;
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);

            Debug.Log(kinds.Count > 0
                ? $"Толпа добавлена, видов подставлено: {kinds.Count}. Сохраните сцену."
                : "Толпа добавлена, но видов нет — запустите «Пауки: запечь анимацию в текстуры».");
        }

        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || Directory.Exists(folder)) return;

            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
        }

        private static string Size(long bytes) => $"{bytes / 1024f:N0} КБ";
    }
}
