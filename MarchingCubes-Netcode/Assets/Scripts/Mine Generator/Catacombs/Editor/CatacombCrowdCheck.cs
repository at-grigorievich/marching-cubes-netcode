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

        /// <summary>Сколько ещё гонять после основного прогона, прежде чем мерить натиск.</summary>
        private const float PressSeconds = 10f;

        /// <summary>
        /// В каком радиусе мерить натиск. Пятнадцать — чуть шире прежнего ближнего круга
        /// (12): стоявшие за его краем в очереди и есть те, на кого жаловались.
        /// </summary>
        private const float PressRadius = 15f;

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

            // Третий отказ того же рода: толпа бьёт с дистанции, на которой до игрока
            // не дотянуться. Прогон этого не видел — связность, посадка и скорость
            // при этом ровно те же, а в кадре орда «стреляет» с четырёх юнитов.
            var attacking = crowd.MeasureAttackGap(out var gapMean, out var gapMin, out var gapMax);

            if (attacking == 0)
            {
                text.AppendLine("  бьющих особей в этот момент нет — зазор удара не мерен");
            }
            else
            {
                text.AppendLine($"  бьют {attacking}, зазор до игрока: средний {gapMean:0.00}, " +
                                $"от {gapMin:0.00} до {gapMax:0.00} юнита");

                // Порог по верхней границе зазора, а не по среднему: бьёт вся дуга
                // вокруг игрока, и одна особь, замахнувшаяся из глубины толпы,
                // видна в кадре так же, как и весь передний ряд.
                if (gapMax > 1.2f)
                {
                    text.AppendLine("  ПЛОХО: особь бьёт, не дотянувшись — проверьте SpiderKind.AttackRange");
                    failed++;
                }
            }

            // Натиск: что делает толпа, когда дошла. Ещё десять секунд, чтобы у игрока
            // собрались все, а не только первые добежавшие.
            for (var i = 0; i < Mathf.RoundToInt(PressSeconds / Step); i++) crowd.Simulate(Step);

            var press = crowd.MeasureEngagement(PressRadius);
            var engaged = press.Attacking + press.Climbing + press.Moving;
            var active = press.Near - press.Lurking;

            text.AppendLine($"=== Натиск (через {Seconds + PressSeconds:0} с, ближе {PressRadius:0} юнитов) ===");
            text.AppendLine($"  рядом {press.Near}: бьют {press.Attacking}, лезут поверх {press.Climbing} " +
                            $"(выше всех на {press.MaxClimb:0.0}), бегут {press.Moving}, " +
                            $"толкутся {press.Jostling}, стоят {press.Stalled}, в засаде {press.Lurking}");
            text.AppendLine($"  стоят без дела: {Share(press.Stalled, active):P0} от не-засадников");
            text.AppendLine($"  держатся: пол {press.OnFloor}, стены {press.OnWall}, свод {press.OnCeiling} " +
                            $"({Share(press.OnWall + press.OnCeiling, press.Near):P0} не на полу)");
            text.AppendLine($"  поднимаются сейчас {press.Rising}: ставят лапы на спину соседа стоя {press.Rearing}, " +
                            $"лифтом (стоя и с ровным телом) {press.Elevator}");
            text.AppendLine($"  на верхних поверхностях (свод, стены выше глаз): {press.Upper} " +
                            $"({Share(press.Upper, press.Near):P0}); " +
                            $"куча в трёх юнитах от игрока высотой до {press.CloseClimb:0.0}");

            var inside = crowd.CountBodyOverlaps(PressRadius, out var nearForOverlap, out var deep);

            text.AppendLine($"  телом в другой особи (на одном ярусе): {inside} из {nearForOverlap} " +
                            $"({Share(inside, nearForOverlap):P0}), из них глубоко, почти целиком: {deep} " +
                            $"({Share(deep, nearForOverlap):P0})");

            // Жалоба «пауки залезают друг в друга, а не друг на друга». До твёрдых тел
            // у игрока сидели телом в другой 92%, и глубоко — большинство.
            if (Share(deep, nearForOverlap) > 0.1f)
            {
                text.AppendLine("  ПЛОХО: тела проходят друг в друга — проверьте ядра тел в Flock и Block");
                failed++;
            }

            var riders = crowd.MeasureRiders(PressRadius, out var floating, out var onAttackers, out var stacked);

            text.AppendLine($"  наездников {riders} ({Share(riders, press.Near):P0} от тех, кто рядом): " +
                            $"висят в воздухе, выше горба спины под собой, {floating} ({Share(floating, riders):P0}); " +
                            $"лежат на спинах бьющих {onAttackers}, на других наездниках (третий ярус и выше) {stacked}");

            // Жалоба «должны залазить лапками друг на друга, а не висеть в воздухе». До горба
            // спины наездник стоял на полной её высоте и над кончиками лап соседа, и перед ним,
            // пока поднимался на дыбах: из 77 у игрока на теле соседа лежали 27.
            if (Share(floating, riders) > 0.1f)
            {
                text.AppendLine("  ПЛОХО: наездники висят в воздухе — проверьте горб (SpiderTuning.Hump) и UpdateClimb");
                failed++;
            }

            // Стенолазы: пользователь просил, чтобы часть толпы ползла по верхним стенкам
            // и своду, обволакивала их. До стенолазов там было 34%, с ними 56-57%.
            if (Share(press.Upper, press.Near) < 0.4f)
            {
                text.AppendLine("  ПЛОХО: толпа у игрока не лезет на стены и свод — проверьте ClimberShare");
                failed++;
            }

            // Заход в толпу: игрок идёт к самому плотному месту рядом на скорости ходьбы.
            // Стоит глазами на своей высоте, а не на полу, как в прогоне выше: там точка
            // входа кладёт трансформ (то есть глаза) прямо в пол, и пол вокруг считался бы
            // «у самой камеры». Жалоба была ровно на это: зашёл внутрь — и на экране
            // «куча-мала» из лап.
            text.AppendLine(WalkIn(world, crowd, player, ref failed));

            // Дрожь: у дальних небьющих позиция не должна скакать туда-обратно от кадра к кадру.
            // Жалоба «дальние пауки синхронно трясутся» пришла после твёрдых тел: все
            // раздвигались разом по позициям прошлого кадра, и тряслись 68-99% особей.
            // У толпы без твёрдых тел этот замер даёт 8%: толкотня стаи подрагивает всегда.
            var shaking = Shaking(crowd, out var watched);

            text.AppendLine($"  дрожат (позиция скачет больше 2 см за кадр) дальше шести юнитов: " +
                            $"{Share(shaking, watched):P0} из {watched}");

            if (Share(shaking, watched) > 0.25f)
            {
                text.AppendLine("  ПЛОХО: толпа трясётся — проверьте контакт и раздвигание тел в Flock");
                failed++;
            }

            // Жалоба «доходят до игрока и плавно взлетают вверх»: подъём обязан идти на ходу.
            if (press.Elevator > press.Rising / 10)
            {
                text.AppendLine("  ПЛОХО: особи поднимаются стоя, как на лифте — проверьте UpdateClimb");
                failed++;
            }

            // Порог — по жалобе «только малая часть атакует, остальные айдлят вдалеке».
            // До ярусов стояло 40%; стоящих в давке ноль не будет никогда — кто-то всегда
            // упирается в спину соседа, пока не полез, — но это должны быть единицы.
            if (Share(press.Stalled, active) > 0.2f)
            {
                text.AppendLine("  ПЛОХО: толпа у игрока стоит, а не лезет — проверьте ярусы (Flock, UpdateClimb)");
                failed++;
            }

            if (press.Climbing == 0)
            {
                text.AppendLine("  ПЛОХО: никто не лезет по спинам — куча не собирается");
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
            var gibbed = new List<Vector3>();

            // Журнал убийств: по нему в игре считаются опыт, валюта и задания, поэтому он обязан
            // совпасть с тем, что вернул удар, — поштучно, с разорванными и с источником.
            crowd.FlushKills();

            var journal = new List<KillRecord>();
            void Collect(IReadOnlyList<KillRecord> batch) => journal.AddRange(batch);
            crowd.Killed += Collect;

            var debugBefore = crowd.KillsBy(DamageSource.Debug);
            var killed = crowd.DamageAt(player.transform.position, 12f, 99, gibbed);

            crowd.FlushKills();
            crowd.Killed -= Collect;

            text.AppendLine($"  взрыв радиусом 12 у игрока: убито {killed} из {before}, из них разорвано на куски {gibbed.Count}");

            var journalTorn = journal.Count(k => k.Gibbed);
            var journalDebug = journal.Count(k => k.Source == DamageSource.Debug);

            text.AppendLine($"  журнал убийств: записей {journal.Count}, разорванных {journalTorn}, " +
                            $"от отладки {journalDebug}, счётчик источника +{crowd.KillsBy(DamageSource.Debug) - debugBefore}");

            if (journal.Count != killed || journalTorn != gibbed.Count || journalDebug != killed ||
                crowd.KillsBy(DamageSource.Debug) - debugBefore != killed || crowd.PendingKills != 0)
            {
                text.AppendLine("  ПЛОХО: журнал убийств разошёлся с ударом — опыт и задания посчитаются неверно");
                failed++;
            }

            if (killed == 0 && Near(positions, player.transform.position, 12f) > 0)
            {
                text.AppendLine("  ПЛОХО: в радиусе взрыва были особи, а убитых нет");
                failed++;
            }

            // Рвёт на куски только ближних к эпицентру (gibShare радиуса), край сферы
            // убивает целиком: труп, отлетевший от взрыва, тоже должен остаться в кадре.
            if (killed > 0 && (gibbed.Count == 0 || gibbed.Count == killed))
            {
                text.AppendLine("  ПЛОХО: разрыв на куски не отделяет эпицентр от края — рвёт всех или никого");
                failed++;
            }

            text.AppendLine(Crater(world, crowd, player, ref failed));

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

        /// <summary>
        /// Воронка от взрыва — тем же путём, что в игре (<see cref="CaveBlastMarks.Blast"/>):
        /// копает ли, видит ли яму коллайдер, не углубляет ли её повторный взрыв и, главное,
        /// совпадает ли переразмеченный кусок сетки навигации с полной постройкой
        /// клетка в клетку. Переразметка куска, которая разошлась бы с полной, — это толпа,
        /// ходящая над ямой по старой поверхности, и глазами на застывшем кадре это
        /// не видно.
        ///
        /// Мир после этого остаётся с ямой: плотность в сцену не пишется и восстановится
        /// из сида при следующей генерации.
        /// </summary>
        private static string Crater(CatacombWorld world, SpiderCrowd crowd, CatacombTestRig player, ref int failed)
        {
            var text = new StringBuilder("=== Воронка ===");

            // Пол под точкой входа — ровное место, где взрыв гарантированно лежит на камне.
            var eye = player.transform.position + Vector3.up * 1.2f;

            if (!FloorBelow(eye, player, out var floor))
            {
                text.AppendLine();
                text.Append("  ПЛОХО: под точкой входа не нашлось пола");
                failed++;
                return text.ToString();
            }

            var go = new GameObject("Crater Probe") { hideFlags = HideFlags.HideAndDontSave };
            var marks = go.AddComponent<CaveBlastMarks>();
            marks.Bind(world, crowd, player.transform, 1.7f, 0.7f, 3f);

            try
            {
                var point = floor.point + floor.normal * 0.12f;

                // Снимок сетки до ямы: сверка ниже что-то доказывает, только если яма
                // сетку действительно поменяла.
                var depthBefore = crowd.Field.Depth.ToArray();
                var walkableBefore = crowd.Field.Walkable.ToArray();

                var timer = Stopwatch.StartNew();
                var dug = marks.Blast(point);
                timer.Stop();

                // В редакторе меш и коллайдер чанка перестраиваются сразу, в игре — по бюджету кадра.
                var sunk = FloorBelow(eye, player, out var after) ? after.distance - floor.distance : 0f;

                text.AppendLine();
                text.Append($"  взрыв на полу: воронка {(dug ? "выкопана" : "НЕТ")}, пол просел на {sunk:0.00} юнита " +
                            $"(копание и переразметка сетки {timer.Elapsed.TotalMilliseconds:0.0} мс)");

                if (!dug || sunk < 0.3f)
                {
                    text.AppendLine();
                    text.Append("  ПЛОХО: взрыв вплотную к полу не оставил ямы, которую видит коллайдер");
                    failed++;
                }

                // Второй взрыв в дно той же ямы не должен её углублять.
                var again = marks.Blast(after.point + after.normal * 0.12f);

                text.AppendLine();
                text.Append($"  повторный взрыв в дно: {(again ? "КОПАЕТ ГЛУБЖЕ" : "пропущен")}, пропусков {marks.CratersSkipped}");

                if (again)
                {
                    text.AppendLine();
                    text.Append("  ПЛОХО: очередь гранат в одну точку прокопает колодец");
                    failed++;
                }

                // Сверка куска сетки с полной постройкой по той же, уже изрытой, плотности.
                var field = crowd.Field;
                var fresh = new SpiderFlowField();

                try
                {
                    var full = Stopwatch.StartNew();
                    fresh.Build(world, field.CellSize, field.AttachRange);
                    full.Stop();

                    var mismatched = 0;
                    var changed = 0;

                    for (var i = 0; i < fresh.CellCount; i++)
                    {
                        var same = field.Open[i] == fresh.Open[i]
                                   && field.Walkable[i] == fresh.Walkable[i]
                                   && field.FaceOpen[i] == fresh.FaceOpen[i]
                                   && math.lengthsq(field.Normal[i] - fresh.Normal[i]) < 1e-8f
                                   && math.abs(field.Depth[i] - fresh.Depth[i]) < 1e-4f;

                        if (!same) mismatched++;

                        if (field.Walkable[i] != walkableBefore[i] || math.abs(field.Depth[i] - depthBefore[i]) > 0.05f)
                            changed++;
                    }

                    text.AppendLine();
                    text.Append($"  сетка навигации после ямы: расходится с полной постройкой в {mismatched} клетках из {fresh.CellCount}, " +
                                $"проходимых {field.WalkableCount} против {fresh.WalkableCount} (полная постройка {full.ElapsedMilliseconds} мс)");

                    if (mismatched > 0 || field.WalkableCount != fresh.WalkableCount)
                    {
                        text.AppendLine();
                        text.Append("  ПЛОХО: переразметка куска разошлась с полной — толпа ходит над ямой по старой поверхности");
                        failed++;
                    }

                    // Контроль самого контроля: яма обязана была поменять сетку, иначе сверка
                    // выше ничего не доказывает.
                    text.AppendLine();
                    text.Append($"  клеток, которые яма поменяла (проходимость или глубина до камня больше 5 см): {changed}");

                    if (changed == 0)
                    {
                        text.AppendLine();
                        text.Append("  ПЛОХО: яма не поменяла ни одной клетки — сверка с полной постройкой ничего не доказывает");
                        failed++;
                    }
                }
                finally
                {
                    fresh.Dispose();
                }
            }
            finally
            {
                marks.Release();
                UnityEngine.Object.DestroyImmediate(go);
            }

            return text.ToString();
        }

        /// <summary>
        /// Пол под точкой — лучом, но мимо коллайдеров самого игрока. Луч из глаз вниз
        /// первым встречает его капсулу, и первая версия раздела копала воронку от макушки
        /// игрока и ею же меряла, просел ли пол.
        /// </summary>
        private static bool FloorBelow(Vector3 from, CatacombTestRig player, out RaycastHit floor)
        {
            floor = default;
            var best = float.MaxValue;

            foreach (var hit in Physics.RaycastAll(from, Vector3.down, 10f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.transform.IsChildOf(player.transform) || hit.distance >= best) continue;

                best = hit.distance;
                floor = hit;
            }

            return best < float.MaxValue;
        }

        /// <summary>Собирает толпу заново. Общая с проверкой света: две копии этого разъехались бы.</summary>
        /// <summary>
        /// Заход в толпу: полторы секунды шага к самому плотному месту рядом и секунда
        /// стояния внутри. Меряет, расступается ли толпа — сколько особей в личном
        /// пространстве игрока, — и возвращает игрока, где он стоял.
        /// </summary>
        private static string WalkIn(CatacombWorld world, SpiderCrowd crowd, CatacombTestRig player, ref int failed)
        {
            var start = player.transform.position;
            var eye = start + Vector3.up * 1.2f;

            player.transform.position = eye;

            var positions = new List<Vector3>();
            crowd.CopyPositions(positions);

            var near = positions.Where(p => (p - eye).magnitude < 12f).ToList();
            var centre = near.Count > 0 ? near.Aggregate(Vector3.zero, (a, b) => a + b) / near.Count : eye;
            var direction = Vector3.ProjectOnPlane(centre - eye, Vector3.up).normalized;

            for (var i = 0; i < 90; i++)
            {
                var next = player.transform.position + direction * (6f * Step);

                // По полю плотности, а не физикой: грабли №1.
                if (!world.IsSolid(next) && !world.IsSolid(next + Vector3.down)) player.transform.position = next;

                crowd.Simulate(Step);
            }

            var walked = (player.transform.position - eye).magnitude;

            for (var i = 0; i < 60; i++) crowd.Simulate(Step);

            var inside = crowd.MeasureEngagement(PressRadius);

            player.transform.position = start;

            var line = $"  зашёл в толпу на {walked:0.0} юнита: рядом {inside.Near}, в личном пространстве " +
                       $"{inside.Intruding}, куча в трёх юнитах до {inside.CloseClimb:0.0}";

            // Порог — единицы процента: кто-то всегда оказывается вплотную в момент замера,
            // пока его сдвигает. До личного пространства было 70-75 при стоящем игроке.
            if (inside.Intruding > inside.Near * 0.03f)
            {
                failed++;
                return line + Environment.NewLine +
                       "  ПЛОХО: толпа не расступается — проверьте KeepOut в SpiderMoveJob";
            }

            return line;
        }

        /// <summary>
        /// Доля дрожащих: по трём кадрам подряд, вторая разность позиции на камне больше 2 см.
        /// У плавного движения она близка к нулю, у колебания «туда-сюда» с частотой кадра —
        /// порядка удвоенного сдвига. Берутся дальние (дальше шести юнитов), небьющие и не в засаде.
        /// </summary>
        private static int Shaking(SpiderCrowd crowd, out int watched)
        {
            var shaking = 0;
            watched = 0;

            for (var pass = 0; pass < 10; pass++)
            {
                var frames = new List<Dictionary<int, SpiderState>>();

                for (var f = 0; f < 3; f++)
                {
                    if (f > 0) crowd.Simulate(Step);

                    var list = new List<SpiderState>();
                    crowd.CopyStates(list);

                    var map = new Dictionary<int, SpiderState>();
                    foreach (var s in list) map[s.Serial] = s;

                    frames.Add(map);
                }

                var target = (float3)crowd.TargetLocal;

                foreach (var pair in frames[2])
                {
                    var s2 = pair.Value;

                    if (s2.Clip != (int)SpiderClip.Walk || s2.Ebb != 0) continue;
                    if (math.distance(s2.Position, target) < 6f) continue;
                    if (!frames[0].TryGetValue(pair.Key, out var s0) || !frames[1].TryGetValue(pair.Key, out var s1)) continue;

                    var g0 = s0.Position - s0.Up * s0.Climb;
                    var g1 = s1.Position - s1.Up * s1.Climb;
                    var g2 = s2.Position - s2.Up * s2.Climb;

                    watched++;
                    if (math.length(g2 - 2f * g1 + g0) > 0.02f) shaking++;
                }
            }

            return shaking;
        }

        internal static SpiderCrowd EnsureCrowd(CatacombWorld world, StringBuilder text, ref int failed)
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
