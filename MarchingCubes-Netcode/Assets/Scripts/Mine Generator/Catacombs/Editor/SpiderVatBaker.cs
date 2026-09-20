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
        /// Все пауки пака. Одиннадцатый префаб в папке — `Ground`, это декорация
        /// без скиннинга, и в толпу он не идёт.
        ///
        /// Сначала бралось четыре из экономии: каждый вид это своя пара текстур,
        /// и полный набор стоит около семи мегабайт в загрузке WebGL. Отказ от шести
        /// видов эти мегабайты экономил, но ради чего — непонятно: разнообразие орды
        /// это ровно то, что игрок видит ВСЁ ВРЕМЯ, в отличие от почти любого другого
        /// ассета. Четыре вида при восьми сотнях особей означают две сотни копий
        /// каждого в кадре.
        ///
        /// Если мегабайты понадобятся, резать надо не число видов, а точность карт:
        /// позиции лежат в половинной точности на вершину, и квантование в байт
        /// на канал срезало бы вдвое. Это отдельная правка с отдельной проверкой
        /// по кадру — см. открытые вопросы.
        /// </summary>
        private static readonly string[] DefaultKinds =
        {
            "little_spider",
            "karakurt",
            "haymaking",
            "spider peacock",
            "spider jumper",
            "Argiope",
            "yellow floral spider",
            "spider_cross",
            "blue tarantula",
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
            ("karakurt", 2.6f),
            ("little_spider", 2.2f),
            ("haymaking", 2.4f),
            ("spider peacock", 2.4f),
            ("spider jumper", 2.6f),
            ("Argiope", 2.8f),
            ("yellow floral spider", 2.8f),
            ("spider_cross", 3.1f),
            ("blue tarantula", 3.4f),
            ("tarantula", 3.6f)
        };

        private const float DefaultScale = 2.8f;

        /// <summary>
        /// Сколько мировых юнитов остаётся между телом особи и осью игрока в момент удара.
        ///
        /// Радиус капсулы игрока 0.4 плюс зазор 0.15: особь бьёт, когда её тело
        /// касается игрока, а не когда она замечает его. В единицы меша переводится
        /// делением на Scale — иначе константа уезжает вместе с размером особи.
        /// </summary>
        private const float PlayerContact = 0.55f;

        private static float ScaleFor(string name)
        {
            foreach (var (kind, scale) in Scales)
            {
                if (kind == name) return scale;
            }

            return DefaultScale;
        }

        /// <summary>
        /// Какие клипы пака во что превращаются. Порядок совпадает с <see cref="SpiderClip"/>.
        ///
        /// Потолок кадров задан отдельно, потому что клипы пака очень разной длины.
        /// Стояние идёт четыре секунды на 25 кадрах — сотня строк в текстуре ради того,
        /// что почти не шевелится. Прорежаем до двух десятков: у неподвижной твари
        /// разница не видна, а сотня лишних строк это треть мегабайта на вид в загрузке.
        /// </summary>
        private static readonly (string Clip, bool Loop, int MaxFrames)[] Wanted =
        {
            ("walk", true, 128),
            ("attack_1", false, 128),
            ("dead", false, 128),
            ("idle", true, 24)
        };

        /// <summary>
        /// Роли: кто в орде быстрый и хрупкий, а кто медленный и живучий.
        ///
        /// Разброс по скорости здесь важнее, чем кажется: он сам расслаивает толпу
        /// на набегающую волну и отстающий хвост. При одинаковой скорости орда идёт
        /// сплошной стеной и через минуту перестаёт читаться как угроза.
        ///
        /// Доля засады разведена по тому же принципу: крупным сидеть на своде страшнее,
        /// мелким свойственнее бежать.
        /// </summary>
        private static readonly (string Kind, float Speed, int Health, float Lurk)[] Roles =
        {
            // Мелочь: быстрая, с одного попадания, бежит и почти не сидит в засаде.
            ("haymaking", 5.4f, 1, 0.10f),
            ("karakurt", 5.2f, 1, 0.15f),
            ("spider peacock", 4.8f, 1, 0.20f),
            ("little_spider", 4.6f, 1, 0.10f),

            // Прыгун — единственный, кто по повадке засадник: доля ожидания у него
            // самая высокая среди быстрых.
            ("spider jumper", 5.0f, 1, 0.35f),

            // Середина: держит два попадания, ползает заметно медленнее.
            ("yellow floral spider", 3.8f, 2, 0.25f),
            ("Argiope", 3.6f, 2, 0.30f),
            ("spider_cross", 3.0f, 2, 0.30f),

            // Тяжёлые: идут первыми и держат удар, но догнать отступающего игрока
            // сами не могут — этим и задаётся ритм отхода по коридору.
            ("blue tarantula", 2.4f, 3, 0.35f),
            ("tarantula", 2.2f, 4, 0.40f)
        };

        private static (float Speed, int Health, float Lurk) RoleFor(string name)
        {
            foreach (var (kind, speed, health, lurk) in Roles)
            {
                if (kind == name) return (speed, health, lurk);
            }

            return (3.2f, 1, 0.2f);
        }

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

            foreach (var (name, loop, maxFrames) in Wanted)
            {
                if (!clips.TryGetValue(name, out var clip))
                {
                    report.AppendLine($"  {prefab.name}: нет клипа «{name}» — вид пропущен");
                    return null;
                }

                // Кадров столько, сколько в исходнике: пак нарисован на 25 кадрах
                // в секунду, и брать чаще нечего — промежуточных поз в клипе нет.
                // Сверху ограничено потолком клипа, см. Wanted.
                var frames = Mathf.Clamp(Mathf.RoundToInt(clip.length * clip.frameRate), 2, maxFrames);

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

            // Нормали — RGB24, а не RGBA32: четвёртый канал в них не несёт ничего,
            // а стоит четверти веса карты. Потеря нулевая, экономия на полном наборе
            // видов — около полумегабайта загрузки.
            var normalMap = WriteTexture($"{OutputFolder}/{safeName} Normals.asset", vertexCount, totalRows,
                TextureFormat.RGB24, normals);

            var headMask = BuildHeadMask(firstFrame, out var eyes);

            report.AppendLine($"    голова: {eyes} вершин из {vertexCount}");

            var mesh = WriteMesh($"{OutputFolder}/{safeName} Crowd.asset", safeName, source.sharedMesh,
                firstFrame, firstNormals, headMask, bounds);

            var material = WriteMaterial($"{OutputFolder}/{safeName} Crowd.mat", shader, source.sharedMaterial,
                positionMap, normalMap);

            // Истинный ход лапы — в отчёт, чтобы видеть, насколько каденция от него ушла.
            var footTravel = MeasureStride(positions, vertexCount, ranges[0]);

            var kind = WriteKind($"{OutputFolder}/{safeName}.asset", prefab.name, mesh, material, positionMap, normalMap,
                ranges, bounds, footTravel);

            report.AppendLine($"  {prefab.name}: шаг {kind.StrideLength:0.00} при истинном ходе лапы " +
                              $"{footTravel:0.00}, каденция {WalkCadence:0.0} Гц");

            report.AppendLine($"  {prefab.name}: вершин {vertexCount}, строк {totalRows} " +
                              $"({string.Join(", ", ranges.Select(r => $"{r.Name} {r.FrameCount}"))}), " +
                              $"габариты {bounds.size.x:0.00}x{bounds.size.y:0.00}x{bounds.size.z:0.00}, " +
                              $"текстуры {Size(vertexCount * totalRows * 8)} + {Size(vertexCount * totalRows * 3)}");

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

        /// <summary>
        /// UV1 меша: x — столбец вершины в карте анимации, y — маска головы под блик глаз.
        ///
        /// Считается по ПОЗЕ БЕГА, а не по габаритам всех кадров. Габариты объединяют
        /// и клип смерти, где паук переворачивается, отчего вертикальный размах
        /// раздувается вдвое и «верхняя половина» уезжает выше тела целиком. Замер это
        /// и поймал: у тарантула с каракуртом в маску попадало РОВНО НОЛЬ вершин,
        /// то есть блик у них не рисовался вовсе, и заметить это по кадру было нельзя —
        /// выглядело просто как «блик слабый».
        ///
        /// Порог по высоте берётся от передней дольки, а не от всей особи: у паука
        /// лапы расходятся широко и вниз, и половина роста всей туши приходится на них.
        ///
        /// Почему маска геометрией, а не по текстуре: глаза в альбедо это просто тёмные
        /// пятна, и порогом их не отличить от таких же на брюшке. А где голова, меш
        /// знает точно — передняя часть, и «перёд» у пака известен (минус Z, проверено
        /// съёмкой).
        /// </summary>
        private static Vector2[] BuildHeadMask(Vector3[] vertices, out int eyes)
        {
            var minZ = float.MaxValue;
            var maxZ = float.MinValue;

            foreach (var v in vertices)
            {
                minZ = Mathf.Min(minZ, v.z);
                maxZ = Mathf.Max(maxZ, v.z);
            }

            // Передняя четверть по длине: головогрудь с хелицеями.
            var frontZ = minZ + (maxZ - minZ) * 0.25f;

            var frontMinY = float.MaxValue;
            var frontMaxY = float.MinValue;

            foreach (var v in vertices)
            {
                if (v.z > frontZ) continue;

                frontMinY = Mathf.Min(frontMinY, v.y);
                frontMaxY = Mathf.Max(frontMaxY, v.y);
            }

            var headY = Mathf.Lerp(frontMinY, frontMaxY, 0.5f);

            var uv = new Vector2[vertices.Length];
            eyes = 0;

            for (var i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                var mask = v.z <= frontZ && v.y >= headY ? 1f : 0f;

                if (mask > 0f) eyes++;

                uv[i] = new Vector2((i + 0.5f) / vertices.Length, mask);
            }

            return uv;
        }

        private static Mesh WriteMesh(string path, string name, Mesh source, Vector3[] vertices, Vector3[] normals,
            Vector2[] headMask, Bounds bounds)
        {
            var mesh = new Mesh { name = $"{name} Crowd", indexFormat = source.indexFormat };

            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);

            var uv = source.uv;
            if (uv != null && uv.Length == vertices.Length) mesh.SetUVs(0, uv);

            // UV1.x — столбец вершины в карте анимации. Через SV_VertexID было бы короче,
            // но это лишнее требование к платформе там, где хватает обычного атрибута,
            // который всё равно едет в вершинном буфере.
            //
            mesh.SetUVs(1, headMask);

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

            // Эталонные значения кромки и глаз проставляются ЯВНО, а не оставляются
            // на умолчания шейдера. Умолчание работает ровно один раз — при создании
            // материала; дальше значение живёт в ассете и правку в коде не подхватывает.
            // На этом в проекте уже обжигались (грабли №8), и тот же приём применён
            // к материалу породы в CatacombWorldEditor.ApplyCaveMaterialPreset.
            //
            // Кромка ЧЁРНАЯ по просьбе пользователя: светлая давала мультяшный контур
            // вокруг каждого тела. Тёмная обводит силуэт, не высветляя его.
            material.SetColor("_RimColor", Color.black);
            material.SetFloat("_RimPower", 2.6f);
            material.SetFloat("_RimStrength", 0.55f);

            material.SetColor("_EyeColor", new Color(1f, 0.55f, 0.15f));
            material.SetFloat("_EyeGlow", 2.2f);
            material.SetFloat("_EyeFocus", 18f);

            // Без этого RenderMeshInstanced рисует партию одним экземпляром: свойства
            // инстанса просто некуда положить.
            material.enableInstancing = true;

            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// Длина шага: сколько юнитов проходит особь за один цикл бега, в единицах меша.
        ///
        /// МЕРЯЕТСЯ по запечённым кадрам, а не берётся от габаритов тела. Раньше здесь
        /// стояло `size.z * 1.1` — догадка «паук проходит за цикл чуть больше своей
        /// длины», и она заметно занижала шаг: у каракурта давала 0.68 при измеренных
        /// по клипу значениях втрое больше. Фаза анимации ведётся пройденным путём
        /// и делится на эту величину, поэтому заниженный шаг гонит лапы во столько же
        /// раз быстрее, чем надо, — в кадре это читается как «паук семенит на месте»,
        /// хотя формально скольжения нет.
        ///
        /// Как меряется. Клип бега сделан НА МЕСТЕ: тело стоит, а лапа за цикл уезжает
        /// назад (опора) и возвращается вперёд (перенос). Размах этого хода вдоль оси
        /// движения и есть длина шага. Берём размах по Z у каждой вершины за все кадры
        /// бега и из них девяностую процентиль: максимум по всем вершинам поймал бы
        /// одинокий выброс на кончике лапы, а медиана — неподвижное туловище.
        /// </summary>
        /// <summary>
        /// Сколько раз в секунду особь перебирает лапами на своей крейсерской скорости.
        ///
        /// Это компромисс, и важно понимать, между чем. Замер клипа бега показал, что
        /// лапа за цикл уезжает всего на 0.15–0.19 единиц меша при теле в 0.61–1.05:
        /// пак нарисован под медленное переползание. Игра же гоняет пауков на 2.2–5.2
        /// юнита в секунду. Честно совпасть эти два числа не могут — при истинном ходе
        /// лапы каденция вышла бы около одиннадцати герц, то есть лапы слились бы в муть.
        ///
        /// Поэтому шаг задаётся ОТ КАДЕНЦИИ, а не от геометрии: сколько циклов в секунду
        /// должно читаться глазами. Ноги при этом проскальзывают — иначе никак, клип
        /// этого не умеет, — но фаза по-прежнему ведётся пройденным путём, поэтому
        /// главное свойство сохраняется: тормозя в давке, особь замедляет и лапы.
        ///
        /// Что было до этого. Шаг считался как 1.1 длины тела — догадка, дававшая РАЗНУЮ
        /// каденцию у разных видов: от 2.0 Гц у тарантула (лапы еле шевелятся при полном
        /// ходе) до 7.5 у haymaking (мельтешение). Общая ручка выравнивает всех.
        ///
        /// Число подбирается глазами: четыре с половиной — быстрый уверенный перебор.
        /// </summary>
        private const float WalkCadence = 4.5f;

        /// <summary>
        /// Длина шага в единицах меша: столько юнитов особь проходит за цикл бега.
        /// Деление на Scale — потому что в джобе шаг обратно на него умножается.
        /// </summary>
        private static float StrideFor(float moveSpeed, float scale) =>
            Mathf.Max(0.05f, moveSpeed / (WalkCadence * Mathf.Max(0.01f, scale)));

        /// <summary>
        /// Истинный ход лапы за цикл, по запечённым кадрам. В расчёт шага не идёт
        /// (см. <see cref="WalkCadence"/>), но печатается в отчёте: это единственный
        /// способ увидеть, насколько выбранная каденция ушла от честной.
        /// </summary>
        private static float MeasureStride(Color[] positions, int vertexCount, SpiderClipRange walk)
        {
            if (vertexCount <= 0 || walk.FrameCount < 2) return 0.9f;

            var spans = new float[vertexCount];

            for (var v = 0; v < vertexCount; v++)
            {
                var min = float.MaxValue;
                var max = float.MinValue;

                for (var f = 0; f < walk.FrameCount; f++)
                {
                    var z = positions[(walk.StartRow + f) * vertexCount + v].b;

                    if (z < min) min = z;
                    if (z > max) max = z;
                }

                spans[v] = max - min;
            }

            System.Array.Sort(spans);

            return spans[Mathf.Clamp(Mathf.RoundToInt(vertexCount * 0.9f), 0, vertexCount - 1)];
        }

        private static SpiderKind WriteKind(string path, string prefabName, Mesh mesh, Material material, Texture2D positions,
            Texture2D normals, SpiderClipRange[] clips, Bounds bounds, float footTravel)
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

                var role = RoleFor(prefabName);

                kind.MoveSpeed = role.Speed;
                kind.Health = role.Health;
                kind.LurkShare = role.Lurk;
                kind.LurkTrigger = 7f;

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

                // Дистанция удара задаётся В МИРОВЫХ ЮНИТАХ и делится на Scale, потому что
                // в джобе она обратно на него умножается. Было `BodyRadius * 2 + 0.6`
                // в единицах меша — грабли №20 в чистом виде: вверх с размером особи
                // уезжали ОБА слагаемых. Полный поперечник вместо половины плюс константа,
                // разросшаяся в 2.2-3.6 раза, давали в мире от 3.2 до 5.9 юнита при
                // собственном теле особи в 0.8-1.9. Тарантул заносил лапу в четырёх юнитах
                // чистого воздуха от игрока — при ширине коридора 3.5 это читается
                // не как укус, а как выстрел.
                //
                // Правильная дистанция — «тела соприкоснулись»: половина длины особи
                // плюс радиус капсулы игрока с небольшим зазором.
                kind.AttackRange = kind.BodyRadius + PlayerContact / Mathf.Max(0.01f, kind.Scale);
                kind.StrideLength = StrideFor(kind.MoveSpeed, kind.Scale);
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

            // Голос орды. AudioSource придёт сам по RequireComponent.
            go.AddComponent<CaveCrowdAudio>();

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
