using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Прогон толпы: построить уровень, заселить его пауками, погонять поведение
    /// и померить то, что руками не видно.
    ///
    /// Главная проверка здесь — «сколько особей стоит внутри породы», и она сделана
    /// ПО ПОЛЮ ПЛОТНОСТИ, а не физикой. Физика на этот вопрос отвечает неверно:
    /// коллайдеры есть только на поверхностях ходов, и посреди сплошного камня любой
    /// запрос рапортует «свободно». Толпа, наполовину утонувшая в стенах, прошла бы
    /// такую проверку с отличием.
    ///
    /// Вторая проверка — «доходят ли». Поле потока легко построить так, что оно
    /// красиво выглядит в гизмо и при этом ведёт в тупик: заливка достаёт до клетки,
    /// а особь до неё не доходит, потому что застревает на стыке. Поэтому меряется
    /// не связность сетки, а падение среднего расстояния до игрока за время прогона.
    /// </summary>
    public static class CatacombCrowdCheck
    {
        private const string ScenePath = "Assets/Scenes/test.unity";
        private const string KindFolder = "Assets/Spiders";

        /// <summary>Секунд поведения, которые прогоняются.</summary>
        private const float Seconds = 6f;

        private const float Step = 1f / 60f;

        public static void Run()
        {
            var code = 0;

            try
            {
                code = Report();
            }
            catch (Exception error)
            {
                Debug.LogError("ПРОГОН ТОЛПЫ УПАЛ: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        /// <summary>То же без выхода из процесса — для вызова через run_script при открытом редакторе.</summary>
        public static int Report()
        {
            var text = new StringBuilder();
            var failed = 0;

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var world = UnityEngine.Object.FindFirstObjectByType<CatacombWorld>();
            if (world == null) throw new Exception("НЕТ CatacombWorld на сцене " + ScenePath);

            world.GenerateImmediate();

            var crowd = EnsureCrowd(world, text, ref failed);
            if (crowd == null)
            {
                Debug.LogError($"ПРОВЕРКА ТОЛПЫ НЕ ПРОШЛА:{Environment.NewLine}{text}");
                return 1;
            }

            var player = UnityEngine.Object.FindFirstObjectByType<CatacombTestRig>();
            if (player == null) throw new Exception("НЕТ CatacombTestRig — за кем бежать толпе, неизвестно");

            // Игрока ставим на точку входа: в сохранённой сцене он висит посреди мира,
            // и половина замеров ушла бы на то, что он в породе.
            if (world.TryGetSpawnPoint(out var spawn)) player.transform.position = spawn;

            var build = Stopwatch.StartNew();
            crowd.Rebuild();
            build.Stop();

            if (!crowd.HasField)
            {
                text.AppendLine("  сетка навигации не построена — дальше мерить нечего");
                Debug.LogError($"ПРОВЕРКА ТОЛПЫ НЕ ПРОШЛА:{Environment.NewLine}{text}");
                return 1;
            }

            text.AppendLine("=== Сетка навигации ===");
            text.AppendLine($"  {crowd.Field.Describe()}");
            text.AppendLine($"  построена за {build.ElapsedMilliseconds} мс");

            var reachable = ReachableShare(crowd);
            text.AppendLine($"  доля проходимых клеток, достижимых от игрока: {reachable:P1}");

            // Разрез по высоте отвечает на главный вопрос при плохой связности:
            // это дырки по всему уровню или целые этажи, до которых нет лестницы.
            // Гадать тут нельзя — в этом проекте несколько диагнозов подряд
            // оказались неверными именно из-за гадания по общей цифре.
            text.AppendLine(HeightProfile(crowd));
            text.AppendLine(crowd.Field.DescribeFrontier());

            // Ниже этого поле потока разрезано: часть уровня толпе недоступна, и волна
            // оттуда не придёт никогда. Полной единицы не бывает — отдельные клетки
            // отсекаются шумом стен на краю мира.
            if (reachable < 0.9f)
            {
                text.AppendLine("  ПЛОХО: сетка разрезана, до части уровня толпа не дойдёт");
                failed++;
            }

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();

            crowd.CopyPositions(positions);
            crowd.CopyNormals(normals);
            var startCount = positions.Count;
            var startDistance = MeanDistance(positions, player.transform.position);

            text.AppendLine("=== Заселение ===");
            text.AppendLine($"  особей после заселения: {startCount}");
            text.AppendLine($"  среднее расстояние до игрока: {startDistance:0.0} юнита");

            if (startCount == 0)
            {
                text.AppendLine("  ПЛОХО: не появилось ни одной особи");
                failed++;
            }

            var solidAtSpawn = InsideRock(world, positions, normals);
            text.AppendLine($"  из них внутри породы: {solidAtSpawn} ({Share(solidAtSpawn, startCount):P1})");

            if (Share(solidAtSpawn, startCount) > 0.02f)
            {
                text.AppendLine("  ПЛОХО: спавн кладёт особей в камень — проверьте посадку на поверхность");
                failed++;
            }

            var steps = Mathf.RoundToInt(Seconds / Step);
            var clock = Stopwatch.StartNew();

            for (var i = 0; i < steps; i++) crowd.Simulate(Step);

            clock.Stop();

            crowd.CopyPositions(positions);
            crowd.CopyNormals(normals);

            var endCount = positions.Count;
            var endDistance = MeanDistance(positions, player.transform.position);
            var solid = InsideRock(world, positions, normals);

            text.AppendLine("=== Поведение ===");
            text.AppendLine($"  прогнано {Seconds:0.0} с за {steps} шагов, " +
                            $"{clock.Elapsed.TotalMilliseconds / steps:0.000} мс на шаг " +
                            $"при {endCount} особях");
            text.AppendLine($"  среднее расстояние до игрока: было {startDistance:0.0}, стало {endDistance:0.0}");
            text.AppendLine($"  средняя скорость особи: {crowd.MeanSpeed():0.00} юнита в секунду");

            // Толпа обязана СБЛИЖАТЬСЯ. Если поле потока ведёт не туда или особи
            // застревают на стыках клеток, расстояние стоит на месте — и это ровно
            // тот отказ, который в гизмо не виден.
            if (endDistance >= startDistance - 1f)
            {
                text.AppendLine("  ПЛОХО: толпа не приближается — поле потока не ведёт к игроку");
                failed++;
            }

            text.AppendLine($"  внутри породы после прогона: {solid} ({Share(solid, endCount):P1})");

            // Порог не ноль: клетка сетки крупнее вокселя, и особь на самом краю хода
            // может краем задеть камень. Заметная доля означает, что отжим от стен
            // не работает и толпа размазывается по породе.
            if (Share(solid, endCount) > 0.05f)
            {
                text.AppendLine("  ПЛОХО: толпа тонет в стенах — проверьте прижим к поверхности (Cling и знак глубины)");
                failed++;
            }

            text.AppendLine($"  добежало вплотную к игроку (ближе 3 юнитов): {Near(positions, player.transform.position, 3f)}");

            // Ради этого поле и переписали с высотного на поверхностное: толпа обязана
            // заполнять ход, а не идти по его дну в одну линию. Считается по нормали
            // поверхности, за которую особь держится, а не по её высоте.
            text.AppendLine(Surfaces(crowd, ref failed));

            // Два отказа, которые видно глазами мгновенно, а числами прогона — никак:
            // толпа бежит задом наперёд и толпа вертится на месте. Связность, посадка
            // и скорость при обоих ровно те же, поэтому меряем отдельно.
            crowd.MeasureOrientation(Step, out var heading, out var turn);

            text.AppendLine($"  угол между взглядом и бегом: {heading:0} градусов");
            text.AppendLine($"  средняя угловая скорость: {turn:0} градусов в секунду");

            // Порог щедрый: в давке особь и правда бежит боком, пока её пропихивают.
            // Ловится не это, а разворот целиком — модель, повёрнутая не той стороной,
            // даёт около 180.
            if (heading > 90f)
            {
                text.AppendLine("  ПЛОХО: толпа бежит не лицом вперёд — проверьте SpiderKind.FacesMinusZ");
                failed++;
            }

            // Верхняя граница — сама скорость доворота: если толпа держится у неё,
            // значит цель поворота скачет каждый кадр, и в кадре это вертящиеся волчки.
            if (turn > 200f)
            {
                text.AppendLine("  ПЛОХО: толпа крутится на месте — цель поворота неустойчива");
                failed++;
            }

            text.AppendLine("=== Отрисовка ===");

            var camera = player.GetComponent<Camera>();

            if (camera != null)
            {
                crowd.Submit(camera);
                text.AppendLine($"  в кадре {crowd.Drawn} из {crowd.Alive} за {crowd.Batches} вызовов");

                if (crowd.Drawn > 0 && crowd.Batches == 0)
                {
                    text.AppendLine("  ПЛОХО: особи есть, а вызовов отрисовки нет");
                    failed++;
                }
            }
            else
            {
                text.AppendLine("  камеры на игроке нет — отрисовка не проверена");
            }

            foreach (var material in Materials(crowd))
            {
                if (ShaderUtil.ShaderHasError(material.shader))
                {
                    text.AppendLine($"  ПЛОХО: шейдер {material.shader.name} не собрался");
                    failed++;
                }

                if (material.enableInstancing) continue;

                text.AppendLine($"  ПЛОХО: у материала {material.name} выключен инстансинг");
                failed++;
            }

            text.AppendLine("=== Поражение ===");

            var before = crowd.Alive;
            var killed = crowd.DamageAt(player.transform.position, 12f, 99);

            text.AppendLine($"  взрыв радиусом 12 у игрока: убито {killed} из {before}");

            if (killed == 0 && Near(positions, player.transform.position, 12f) > 0)
            {
                text.AppendLine("  ПЛОХО: в радиусе взрыва были особи, а убитых нет");
                failed++;
            }

            EditorSceneManager.SaveScene(scene);

            if (failed > 0)
            {
                Debug.LogError($"ПРОВЕРКА ТОЛПЫ НЕ ПРОШЛА, проблемных пунктов {failed}:" +
                               $"{Environment.NewLine}{text}");
                return 1;
            }

            Debug.Log($"Проверка толпы пройдена:{Environment.NewLine}{text}");
            return 0;
        }

        private static SpiderCrowd EnsureCrowd(CatacombWorld world, StringBuilder text, ref int failed)
        {
            // Толпа ПЕРЕСОЗДАЁТСЯ каждый прогон, а не берётся со сцены.
            //
            // Иначе проверка меряла бы не то, что написано в коде: настройки живут
            // в сохранённой сцене, и толпа, однажды созданная со старым размером клетки,
            // так с ним и осталась бы — а прогон рапортовал бы про связность, которой
            // при нынешних умолчаниях нет. Тот же урок, что с «Починить свет».
            var stale = UnityEngine.Object.FindFirstObjectByType<SpiderCrowd>();
            if (stale != null) UnityEngine.Object.DestroyImmediate(stale.gameObject);

            var go = new GameObject("Spider Crowd");
            go.transform.SetParent(world.transform.parent, false);

            var crowd = go.AddComponent<SpiderCrowd>();

            // Голос орды. AudioSource придёт сам по RequireComponent.
            go.AddComponent<CaveCrowdAudio>();

            var serialized = new SerializedObject(crowd);
            serialized.FindProperty("world").objectReferenceValue = world;

            var rig = UnityEngine.Object.FindFirstObjectByType<CatacombTestRig>();
            if (rig != null) serialized.FindProperty("target").objectReferenceValue = rig.transform;

            serialized.ApplyModifiedPropertiesWithoutUndo();

            text.AppendLine("  толпа на сцене пересоздана с умолчаниями из кода");

            var kinds = AssetDatabase.FindAssets("t:SpiderKind", new[] { KindFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<SpiderKind>)
                .Where(k => k != null && k.IsReady)
                .ToList();

            if (kinds.Count == 0)
            {
                text.AppendLine($"  НЕТ запечённых видов в {KindFolder} — " +
                                "запустите Tools/Mine Generator/Пауки: запечь анимацию в текстуры");
                failed++;
                return null;
            }

            var target = new SerializedObject(crowd);
            var array = target.FindProperty("kinds");

            array.arraySize = kinds.Count;

            for (var i = 0; i < kinds.Count; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = kinds[i];

            target.ApplyModifiedPropertiesWithoutUndo();

            text.AppendLine($"  видов подключено: {kinds.Count} ({string.Join(", ", kinds.Select(k => k.name))})");

            return crowd;
        }

        private static IEnumerable<Material> Materials(SpiderCrowd crowd)
        {
            var serialized = new SerializedObject(crowd);
            var array = serialized.FindProperty("kinds");

            for (var i = 0; i < array.arraySize; i++)
            {
                if (array.GetArrayElementAtIndex(i).objectReferenceValue is SpiderKind kind && kind.Material != null)
                {
                    yield return kind.Material;
                }
            }
        }

        /// <summary>Проходимо и достижимо по слоям высоты, полосами по восемь слоёв.</summary>
        private static string HeightProfile(SpiderCrowd crowd)
        {
            var field = crowd.Field;
            var dim = field.Dim;

            const int band = 8;
            var bands = (dim.y + band - 1) / band;

            var walkable = new int[bands];
            var reachable = new int[bands];

            for (var i = 0; i < field.Walkable.Length; i++)
            {
                if (field.Walkable[i] == 0) continue;

                var y = i / dim.x % dim.y;
                var slot = y / band;

                walkable[slot]++;
                if (field.Distance[i] != SpiderFlowField.Unreachable) reachable[slot]++;
            }

            var text = new StringBuilder("  по высоте (слои сетки: проходимо / достижимо)");

            for (var i = 0; i < bands; i++)
            {
                if (walkable[i] == 0) continue;

                var low = i * band * field.CellSize;
                var high = math.min((i + 1) * band, dim.y) * field.CellSize;

                text.AppendLine();
                text.Append($"    Y {low:0}-{high:0} юнита: {walkable[i]} / {reachable[i]}");
            }

            return text.ToString();
        }

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

        /// <summary>
        /// Как толпа распределилась по поверхности: пол, стены, потолок.
        ///
        /// Раскладка по наклону нормали. Пол — нормаль вверх (выше 45 градусов),
        /// потолок — вниз, всё между ними стена. Если на стенах и потолке пусто,
        /// значит поле снова свелось к полу, и толпа идёт по дну в одну линию —
        /// ровно то, ради чего эту сетку и переделывали.
        /// </summary>
        private static string Surfaces(SpiderCrowd crowd, ref int failed)
        {
            var normals = new List<Vector3>();
            crowd.CopyNormals(normals);

            if (normals.Count == 0) return "  по поверхностям: особей нет";

            var floor = 0;
            var wall = 0;
            var ceiling = 0;

            foreach (var n in normals)
            {
                if (n.y > 0.5f) floor++;
                else if (n.y < -0.5f) ceiling++;
                else wall++;
            }

            var offFloor = (wall + ceiling) / (float)normals.Count;

            var text = $"  по поверхностям: пол {floor} ({floor / (float)normals.Count:P0}), " +
                       $"стены {wall} ({wall / (float)normals.Count:P0}), " +
                       $"потолок {ceiling} ({ceiling / (float)normals.Count:P0})";

            // Порог невысокий намеренно: пола в пещере больше, чем стен и потолка,
            // и большинство особей будет на нём всегда. Проверяется не равенство,
            // а сам факт, что вне пола толпа бывает.
            if (offFloor >= 0.15f) return text;

            failed++;
            return text + System.Environment.NewLine +
                   "  ПЛОХО: толпа сидит на полу — по стенам и потолку почти никого";
        }

        /// <summary>
        /// Сколько особей стоит внутри породы. По полю плотности, не физикой (грабли №1).
        ///
        /// Щуп идёт вдоль НОРМАЛИ особи, а не вверх. Вверх было верно, пока толпа ходила
        /// по полу; для паука на потолке «вверх» это прямо в камень, и такая проверка
        /// записывала в утонувшие всех, кто висит над головой. Замер показывал 97%
        /// утонувших там, где на деле тонули единицы.
        /// </summary>
        private static int InsideRock(CatacombWorld world, List<Vector3> positions, List<Vector3> normals)
        {
            var count = 0;

            for (var i = 0; i < positions.Count; i++)
            {
                // Отступ от самой точки посадки обязателен: она лежит ровно на поверхности,
                // где плотность по определению около изоуровня, и часть толпы попадала бы
                // в «камень» из-за округления.
                var away = i < normals.Count ? normals[i] : Vector3.up;

                if (world.IsSolid(positions[i] + away * 0.25f)) count++;
            }

            return count;
        }

        private static float MeanDistance(List<Vector3> positions, Vector3 from)
        {
            if (positions.Count == 0) return 0f;

            var sum = 0f;
            foreach (var point in positions) sum += Vector3.Distance(point, from);

            return sum / positions.Count;
        }

        private static int Near(List<Vector3> positions, Vector3 from, float radius)
        {
            var count = 0;
            var radiusSq = radius * radius;

            foreach (var point in positions)
            {
                if ((point - from).sqrMagnitude <= radiusSq) count++;
            }

            return count;
        }

        private static float Share(int part, int total) => total == 0 ? 0f : part / (float)total;
    }
}
