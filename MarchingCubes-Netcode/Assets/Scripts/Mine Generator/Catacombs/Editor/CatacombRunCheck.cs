using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Прогон забега: вся стадия по таймеру в ускоренной симуляции, финальный рой
    /// и бой с Маткой от пробуждения до смерти.
    ///
    /// Семь минут стадии глазами не проверить ни разу за вечер, а вопросы здесь такие,
    /// что на глаз и не ответить:
    ///
    /// 1. Идёт ли давление по заказанной форме — лёгкая, сложная, средняя, сложная.
    ///    Сама кривая проверяется числами, а то, что за ней идёт ТОЛПА, — живыми особями.
    ///    Толпу мерить надо с игроком, который убивает: пауки сами не исчезают, и без
    ///    убийств затишье не отличить от гребня — население просто стоит на пике.
    ///    Это и есть реактивный спавн Megabonk: затишье — это когда убитых досылают медленнее.
    /// 2. Приходят ли рои и элита по расписанию, и рой ли это — то есть один вид.
    /// 3. Снимает ли финальный рой ближний круг и выходит ли на потолок.
    /// 4. Бой: пробуждается ли Матка только у логова и с живучестью по времени;
    ///    встаёт ли щит ровно на пороге, не пробивается ли, лечится ли; где встают
    ///    генераторы — на полу, по ходам от Матки, разнесённые; снимается ли щит
    ///    зарядкой; достают ли игрока волна и плевок; наступает ли победа
    ///    и прекращается ли пополнение.
    /// </summary>
    public static class CatacombRunCheck
    {
        private const string ScenePath = "Assets/Scenes/test.unity";
        private const string StyleMenu = "Tools/Mine Generator/Свет: как в DRG (тьма и цвет)";

        /// <summary>
        /// Шаг симуляции. Крупнее кадра, потому что гоняется вся стадия: семь минут
        /// по 1/60 это двадцать пять тысяч шагов толпы. Одна двадцатая — тот предел,
        /// на котором поведение толпы замерялось без сбоев.
        /// </summary>
        private const float Step = 1f / 20f;

        /// <summary>
        /// Как часто «игрок» стреляет, секунды, и радиус взрыва. Без убийств население
        /// только копится, и кривую по живым особям не прочитать.
        ///
        /// Стреляет по ближайшему пауку взрывом в пять юнитов — то есть урон у него
        /// ОГРАНИЧЕН, как у гранатомёта. Первая модель раз в секунду выкашивала всё
        /// в радиусе девяти вокруг себя, и пока толпа ждала в очереди за двенадцатью
        /// юнитами, это сходило с рук. Когда очередь сняли и все полезли к игроку,
        /// такой «игрок» убивал каждого добежавшего: убитых стало 5633 вместо 4025,
        /// а живых на втором гребне меньше, чем на первом, — рой второго гребня
        /// самый быстрый и умирал первым. Мерилась скорость бега, а не кривая.
        /// </summary>
        private const float KillEvery = 0.75f;

        private const float KillRadius = 5f;

        /// <summary>Дальше этого «игрок» не стреляет: цели нет, взрыв ушёл бы в стену.</summary>
        private const float KillReach = 14f;

        /// <summary>
        /// Окно усреднения живых особей вокруг опорной точки, секунды. Ровно период
        /// короткой ряби толпы (26 с): окно в 12 секунд попадало то на её гребень,
        /// то на провал, и сравнение двух точек кривой мерило фазу ряби — 899 живых
        /// на первом гребне при цели 667 против 817 на втором при цели 807.
        /// </summary>
        private const float Window = 26f;

        public static void Run()
        {
            var code = 0;

            try
            {
                code = Report();
            }
            catch (Exception error)
            {
                Debug.LogError("ПРОГОН ЗАБЕГА УПАЛ: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        /// <summary>То же без выхода из процесса — для run_script при открытом редакторе.</summary>
        public static int Report()
        {
            var text = new StringBuilder();
            var failed = 0;
            var clock = Stopwatch.StartNew();

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // Компоненты создаёт пункт меню, а не прогон: иначе проверка мерила бы
            // вчерашние умолчания из сохранённой сцены (грабли №8, №25).
            if (!EditorApplication.ExecuteMenuItem(StyleMenu)) throw new Exception("не найден пункт меню: " + StyleMenu);

            var world = UnityEngine.Object.FindFirstObjectByType<CatacombWorld>();
            if (world == null) throw new Exception("НЕТ CatacombWorld на сцене " + ScenePath);

            world.GenerateImmediate();

            var fixtures = UnityEngine.Object.FindFirstObjectByType<CaveFixtures>();
            var director = UnityEngine.Object.FindFirstObjectByType<CaveRunDirector>();
            var queen = UnityEngine.Object.FindFirstObjectByType<CaveQueen>();
            var player = UnityEngine.Object.FindFirstObjectByType<CatacombTestRig>();

            if (director == null) throw new Exception("НЕТ CaveRunDirector — «Починить свет» его не создал");
            if (queen == null) throw new Exception("НЕТ CaveQueen — «Починить свет» её не создал");
            if (player == null) throw new Exception("НЕТ CatacombTestRig — за кем бежать толпе, неизвестно");

            // Игрок на точку входа, иначе прогон не воспроизводится (грабли №30).
            if (!world.TryGetSpawnPoint(out var entry)) throw new Exception("у уровня нет точки входа");
            player.transform.position = entry + Vector3.up * 1.2f;

            var crowd = CatacombCrowdCheck.EnsureCrowd(world, text, ref failed);
            if (crowd == null) return Fail(text);

            crowd.Rebuild();
            if (!crowd.HasField)
            {
                text.AppendLine("  ПЛОХО: сетка навигации не построена — толпу мерить нечем");
                return Fail(text, failed + 1);
            }

            // Толпу пересоздали — прежние ссылки у Матки и директора указывают
            // на удалённый объект, и EnsureLinks найдёт новую сам.
            queen.EnsureLinks();
            queen.Rebuild();
            director.EnsureLinks();
            director.Restart();

            failed += CheckCurve(director, text);
            failed += CheckStage(director, crowd, player, entry, text);
            failed += CheckQueen(world, director, queen, crowd, fixtures, player, entry, text);

            text.AppendLine();
            text.AppendLine($"прогон занял {clock.Elapsed.TotalSeconds:0.0} с");

            if (failed > 0) return Fail(text, failed);

            Debug.Log($"Проверка забега пройдена:{Environment.NewLine}{text}");
            return 0;
        }

        // ------------------------------------------------------------------ кривая

        private static int CheckCurve(CaveRunDirector director, StringBuilder text)
        {
            var failed = 0;
            var curve = director.Curve;

            float I(float t) => curve.Intensity(t);

            var start = I(0f);
            var first = I(curve.firstPeak);
            var lull = I(curve.lull);
            var second = I(curve.secondPeak);
            var end = I(1f);

            text.AppendLine("=== Кривая давления (рост × синус по опорным точкам) ===");
            text.AppendLine($"  старт {start:0.00} -> 1-й гребень {first:0.00} -> передышка {lull:0.00} " +
                            $"-> 2-й гребень {second:0.00} -> конец стадии {end:0.00}");

            // Сама форма, заказанная словами: лёгкая — сложная — средняя — сложная.
            // «Средняя» значит строго между: тяжелее старта, легче первого гребня.
            Expect(first > start * 1.5f, "первый гребень заметно тяжелее старта", text, ref failed);
            Expect(lull < first && lull > start, "передышка между стартом и первым гребнем", text, ref failed);
            Expect(second > first, "второй гребень тяжелее первого", text, ref failed);
            Expect(end < second, "после второго гребня спад — подготовка к Матке", text, ref failed);

            // Гладкость: наибольший скачок между соседними сэмплами. Излом на стыке
            // кусков фазы выдал бы себя скачком производной — здесь его ловим по
            // второй разности.
            var worstKink = 0f;
            const int samples = 400;

            for (var i = 1; i < samples - 1; i++)
            {
                var a = I((i - 1) / (float)samples);
                var b = I(i / (float)samples);
                var c = I((i + 1) / (float)samples);

                worstKink = Mathf.Max(worstKink, Mathf.Abs(a - 2f * b + c));
            }

            text.AppendLine($"  наибольшая вторая разность на шаге 1/{samples}: {worstKink:0.00000}");
            Expect(worstKink < 0.002f, "волна гладкая, без изломов на стыках", text, ref failed);

            text.AppendLine("  по минутам (доля, участок, давление):");

            for (var s = 0f; s <= director.StageSeconds + 0.01f; s += 30f)
            {
                var t = s / director.StageSeconds;
                text.AppendLine($"    {Clock(s)}  {t:0.00}  {curve.Section(t),-12} {I(t):0.00}");
            }

            return failed;
        }

        // ------------------------------------------------------------------ стадия

        private sealed class Sample
        {
            public float Time;
            public int Alive;
            public int Target;

            /// <summary>Сколько появилось с начала прогона — по разнице считается темп досыла.</summary>
            public int Spawned;
        }

        private static int CheckStage(CaveRunDirector director, SpiderCrowd crowd, CatacombTestRig player,
            Vector3 entry, StringBuilder text)
        {
            var failed = 0;
            var samples = new List<Sample>();

            var killTimer = 0f;
            var sampleTimer = 0f;

            var swarmKind = -1;
            var swarmFrom = 0f;
            var swarmShareStart = 0f;
            var swarmShareEnd = 0f;
            var swarmWeak = 0;
            var swarmReport = new StringBuilder();

            var eliteReport = new StringBuilder();
            var elitesSeen = 0;

            var spawnedAtStart = crowd.SpawnedTotal;
            var lateLooked = false;
            var lateReport = "";
            var aim = new List<Vector3>();
            var killed = 0;

            var stage = director.StageSeconds;
            var total = stage + 60f;

            text.AppendLine();
            text.AppendLine($"=== Стадия {Clock(stage)} + минута финального роя, шаг {Step:0.00} с, " +
                            $"игрок бьёт взрывом {KillRadius:0} по ближайшему раз в {KillEvery:0.00} с ===");

            for (var time = 0f; time < total; time += Step)
            {
                director.Tick(Step);

                // Элиту смотрим сразу по появлению, до шага толпы и до выкоса: иначе
                // «игрок» успевает убить её, заведшуюся в полосе у самых ног, раньше замера.
                if (director.ElitesSpawned > elitesSeen)
                {
                    elitesSeen = director.ElitesSpawned;

                    var handle = director.Elites.Count > 0 ? director.Elites[director.Elites.Count - 1] : SpiderHandle.None;
                    crowd.TryGetSpider(handle, out var at, out var health);

                    eliteReport.AppendLine($"    элита #{elitesSeen} на {Clock(director.Elapsed)}: " +
                                           $"живучесть {health}, в {(at - entry).magnitude:0} юнитах от игрока");
                }

                crowd.Simulate(Step);
                player.TickStatus(Step);

                killTimer += Step;

                if (killTimer >= KillEvery)
                {
                    killTimer = 0f;

                    if (TryNearest(crowd, player.BodyCentre, KillReach, aim, out var target))
                    {
                        killed += crowd.DamageAt(target, KillRadius, 999);
                    }
                }

                // Рой: доля его вида среди живых на старте и в последний шаг роя.
                // Рой — это когда один вид вдруг заполняет коридор, и доля должна
                // вырасти в разы, а не на проценты.
                if (director.SwarmActive && swarmKind < 0)
                {
                    swarmKind = director.SwarmKind;
                    swarmFrom = director.Elapsed;
                    swarmShareStart = Share(crowd.CountAlive(swarmKind), crowd.Alive);
                }

                if (swarmKind >= 0 && director.SwarmActive)
                {
                    swarmShareEnd = Share(crowd.CountAlive(swarmKind), crowd.Alive);
                }

                if (swarmKind >= 0 && !director.SwarmActive)
                {
                    swarmReport.AppendLine($"    рой на {Clock(swarmFrom)}: {crowd.KindAt(swarmKind).name}, " +
                                           $"доля вида среди живых {swarmShareStart:P0} -> {swarmShareEnd:P0}");

                    // Критерий относительный — доля растёт в разы. Абсолютный порог (30%)
                    // при игроке, который стреляет, мерил скорость бега вида: рой самого
                    // быстрого добегает и гибнет первым, и его доля среди живых ниже.
                    if (swarmShareEnd < swarmShareStart * 2f) swarmWeak++;

                    swarmKind = -1;
                }

                // Разбивка всей толпы перед финалом: кто эти живые, если досыла нет.
                if (!lateLooked && director.Elapsed >= stage * 0.97f)
                {
                    lateLooked = true;
                    var all = crowd.MeasureEngagement(10000f);

                    lateReport = $"  вся толпа на {Clock(director.Elapsed)}: живых {all.Near}, бьют {all.Attacking}, " +
                                 $"лезут {all.Climbing}, бегут {all.Moving}, толкутся в очереди {all.Jostling}, " +
                                 $"стоят {all.Stalled}, в засаде {all.Lurking}";
                }

                sampleTimer += Step;

                if (sampleTimer >= 1f)
                {
                    sampleTimer = 0f;
                    samples.Add(new Sample
                    {
                        Time = director.Elapsed,
                        Alive = crowd.Alive,
                        Target = crowd.Directive.Population,
                        Spawned = crowd.SpawnedTotal
                    });
                }
            }

            // --- кривая по живым особям ---
            var curve = director.Curve;

            float Around(float t, Func<Sample, int> pick)
            {
                var centre = t * stage;
                var picked = samples.Where(s => Mathf.Abs(s.Time - centre) <= Window * 0.5f).ToList();

                return picked.Count == 0 ? 0f : (float)picked.Average(s => pick(s));
            }

            // Темп досыла в окне, особей в секунду: сколько пауков на игрока ПРИХОДИТ.
            float Rate(float t)
            {
                var centre = t * stage;
                var picked = samples.Where(s => Mathf.Abs(s.Time - centre) <= Window * 0.5f).ToList();

                if (picked.Count < 2) return 0f;

                var span = picked[picked.Count - 1].Time - picked[0].Time;
                return span > 0f ? (picked[picked.Count - 1].Spawned - picked[0].Spawned) / span : 0f;
            }

            // Старт мерится не в нуле, а через полминуты: толпа в начале растёт с нуля
            // досылом, и в первые секунды живых меньше цели просто потому, что не успели.
            var points = new[]
            {
                ("затишье", 0.08f),
                ("1-й гребень", curve.firstPeak),
                ("передышка", curve.lull),
                ("2-й гребень", curve.secondPeak),
                ("перед бурей", 0.97f)
            };

            text.AppendLine("  живые особи вокруг опорных точек (среднее за окно " +
                            $"{Window:0} с; цель директора в скобках):");

            var alive = new float[points.Length];
            var rate = new float[points.Length];

            for (var i = 0; i < points.Length; i++)
            {
                alive[i] = Around(points[i].Item2, s => s.Alive);
                rate[i] = Rate(points[i].Item2);
                var target = Around(points[i].Item2, s => s.Target);

                text.AppendLine($"    {points[i].Item1,-12} {Clock(points[i].Item2 * stage)}: " +
                                $"{alive[i]:0} ({target:0}), приходит {rate[i]:0.0}/с");
            }

            // Пороги с запасом в пятую часть, а не «строго меньше»: передышка, которой
            // не видно, — не передышка. Прежде отлива замер давал 722 живых в передышке
            // против 872 на гребне, и строгое сравнение это пропускало.
            Expect(alive[1] > alive[0] * 1.4f, "на первом гребне живых заметно больше, чем в затишье", text, ref failed);
            Expect(alive[2] < alive[1] * 0.8f, "в передышке живых заметно меньше, чем на первом гребне", text, ref failed);
            Expect(alive[3] > alive[2] * 1.25f, "на втором гребне снова заметно больше", text, ref failed);
            // Что второй гребень тяжелее первого, проверяет сама кривая (выше). От живых
            // здесь нужно другое: толпа ВЫХОДИТ на цель директора на обоих гребнях,
            // несмотря на то что игрок стреляет. Сравнивать гребни между собой по живым
            // или по темпу досыла нельзя, пробовали оба: при игроке с ограниченным
            // уроном толпа приходит в равновесие — сколько убил, столько дослали, — и оба
            // числа меряют, сколько игрок успевает убить в плотной куче и какой вид выпал
            // рою (засадники копятся, быстрые гибнут первыми). От прогона к прогону
            // живые скакали 869/821, 822/770, 789/817, темп — 29/24, 16/24.
            var target1 = Around(curve.firstPeak, s => s.Target);
            var target2 = Around(curve.secondPeak, s => s.Target);

            Expect(alive[1] >= target1 * 0.7f && alive[3] >= target2 * 0.7f,
                "на обоих гребнях толпа выходит на цель директора, хоть игрок и стреляет", text, ref failed);
            Expect(alive[4] < alive[3] * 0.8f, "перед финалом толпа спадает — подготовка к Матке", text, ref failed);

            text.AppendLine(lateReport);
            text.AppendLine($"  досыл стоял на паузе после крупных убийств: {crowd.HoldsTriggered} раз, " +
                            $"{crowd.HeldSeconds:0} с из {stage + 60f:0}");
            text.AppendLine($"  досылано за стадию: {crowd.SpawnedTotal - spawnedAtStart}, убито игроком: {killed}, " +
                            $"ушло отливом: {crowd.EbbVanished}, вернулось из отлива, не скрывшись: {crowd.EbbReturned}");

            // --- рои ---
            text.AppendLine($"  роёв пришло: {director.SwarmsFired}");
            text.Append(swarmReport);
            Expect(director.SwarmsFired == 2, "два роя на двух гребнях", text, ref failed);
            Expect(swarmWeak == 0, "рой — это один вид: его доля среди живых растёт в разы", text, ref failed);

            // --- элита ---
            text.AppendLine($"  элит пришло: {director.ElitesSpawned}");
            text.Append(eliteReport);
            Expect(director.ElitesSpawned == 2, "две элиты по расписанию", text, ref failed);

            // --- финальный рой ---
            var directive = crowd.Directive;

            text.AppendLine($"  финальный рой через минуту: фаза {director.Phase}, ступень {director.FinalTier + 1}, " +
                            $"цель {directive.Population} из потолка {crowd.Capacity}, " +
                            $"ближний круг {directive.NearCap} (обычно {crowd.DefaultNearCap}), " +
                            $"скорость ×{directive.SpeedScale:0.00}, досыл {directive.SpawnRate:0}/с, " +
                            $"живых {crowd.Alive}");

            Expect(director.Phase == CaveRunDirector.RunPhase.FinalSwarm, "после таймера — финальный рой", text, ref failed);
            Expect(directive.Population >= crowd.Capacity * 0.95f, "финальный рой выходит на потолок", text, ref failed);
            // Потолка ближнего круга нет вовсе (0) — или, если его вернули, финал его снимает.
            Expect(directive.NearCap == 0 || directive.NearCap > crowd.DefaultNearCap * 3,
                "в финальном рое никто не ждёт в очереди к игроку", text, ref failed);
            Expect(directive.Frenzy, "в финальном рое засад нет", text, ref failed);

            return failed;
        }

        // ------------------------------------------------------------------ Матка

        private static int CheckQueen(CatacombWorld world, CaveRunDirector director, CaveQueen queen,
            SpiderCrowd crowd, CaveFixtures fixtures, CatacombTestRig player, Vector3 entry, StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Матка ===");

            if (!queen.HasLair)
            {
                text.AppendLine("  ПЛОХО: логова нет — идти игроку некуда");
                return 1;
            }

            var lair = queen.LairPosition;

            text.AppendLine($"  логово в ({lair.x:0}, {lair.y:0}, {lair.z:0}), " +
                            $"от входа {(lair - entry).magnitude:0} юнитов по прямой");

            Expect(!world.IsSolid(lair + Vector3.up * 0.8f), "логово стоит в пустоте, а не в породе", text, ref failed);

            // Свежая стадия, перемотанная на середину: Матку будят на половине таймера.
            // Толпу заселяем заново: после финального роя она стоит на потолке,
            // и выводку Матки не нашлось бы ни одного свободного слота — прогон
            // так и показал «появилось особей 1» за двенадцать секунд боя.
            player.transform.position = entry + Vector3.up * 1.2f;

            director.Restart();
            crowd.Rebuild();
            director.SkipAhead(director.StageSeconds * 0.5f);

            for (var time = 0f; time < 15f; time += Step) Advance(director, queen, crowd, player);

            text.AppendLine($"  толпа заселена заново под середину стадии: живых {crowd.Alive}");

            Expect(!director.TrySummonQueen(entry), "от входа Матку не разбудить", text, ref failed);

            // Игрок у логова — в шести юнитах, в пустоте, ногами на полу.
            var stand = FindStandPoint(world, lair, 6f);
            PlaceBody(player, stand);

            // Поток должен переехать к игроку: пилоны и выводок ищут клетки заливкой,
            // а без пересчёта она осталась бы у точки входа.
            for (var i = 0; i < 20; i++) Advance(director, queen, crowd, player);

            if (!director.TrySummonQueen(player.transform.position))
            {
                text.AppendLine("  ПЛОХО: у логова Матка не просыпается");
                return failed + 1;
            }

            var expected = 36f * (1f + 3f * director.Progress);

            text.AppendLine($"  разбужена на {Clock(director.Elapsed)}: живучесть {queen.MaxHealth:0} " +
                            $"(ждали около {expected:0} при умолчаниях)");

            Expect(queen.MaxHealth > 36f * 1.5f, "разбуженная позже — живучее базовой", text, ref failed);

            // --- атаки: стоим рядом двенадцать секунд ---
            var spawnedBefore = crowd.SpawnedTotal;
            var pushedBefore = player.TimesPushed;
            var entangledBefore = player.TimesEntangled;

            for (var time = 0f; time < 12f; time += Step)
            {
                PlaceBody(player, stand);
                Advance(director, queen, crowd, player);
            }

            text.AppendLine($"  за 12 с рядом: волна достала {player.TimesPushed - pushedBefore} раз, " +
                            $"плевок спутал {player.TimesEntangled - entangledBefore} раз, " +
                            $"появилось особей {crowd.SpawnedTotal - spawnedBefore} (выводок и досыл)");

            Expect(player.TimesPushed > pushedBefore, "ударная волна отбрасывает стоящего рядом", text, ref failed);
            Expect(player.TimesEntangled > entangledBefore, "плевок попадает в стоящего", text, ref failed);

            // --- две фазы щита ---
            for (var phase = 0; phase < 2; phase++)
            {
                failed += CheckShield(world, queen, crowd, fixtures, player, stand, director, phase, text);
                if (queen.State != CaveQueen.QueenState.Awake) break;
            }

            // --- добивание ---
            var litBefore = fixtures != null ? fixtures.LitCount : 0;

            for (var guard = 0; guard < 10000 && queen.State == CaveQueen.QueenState.Awake; guard++)
            {
                queen.DamageAt(queen.Centre, 1f, 1f);
            }

            Expect(queen.State == CaveQueen.QueenState.Dead, "Матка умирает, когда здоровье кончилось", text, ref failed);

            // Победу директор узнаёт из события Матки — его и проверяем, а не состояние.
            Expect(director.Phase == CaveRunDirector.RunPhase.Victory, "смерть Матки — победа стадии", text, ref failed);

            var spawnedAtVictory = crowd.SpawnedTotal;

            for (var time = 0f; time < 5f; time += Step) Advance(director, queen, crowd, player);

            text.AppendLine($"  после победы: светильников горит {(fixtures != null ? fixtures.LitCount : 0)} " +
                            $"из {(fixtures != null ? fixtures.Count : 0)} (было {litBefore}), " +
                            $"новых особей за 5 с: {crowd.SpawnedTotal - spawnedAtVictory}");

            if (fixtures != null)
            {
                Expect(fixtures.LitCount == fixtures.Count, "убита Матка — загорается весь уровень", text, ref failed);
            }

            Expect(crowd.SpawnedTotal == spawnedAtVictory, "после победы пополнения нет", text, ref failed);

            return failed;
        }

        private static int CheckShield(CatacombWorld world, CaveQueen queen, SpiderCrowd crowd, CaveFixtures fixtures,
            CatacombTestRig player, Vector3 stand, CaveRunDirector director, int phase, StringBuilder text)
        {
            var failed = 0;
            var threshold = phase == 0 ? 2f / 3f : 1f / 3f;

            // Бьём по одному, пока не встанет щит: порог обязан поймать здоровье ровно на себе.
            for (var guard = 0; guard < 10000 && queen.State == CaveQueen.QueenState.Awake; guard++)
            {
                queen.DamageAt(queen.Centre, 1f, 1f);
            }

            text.AppendLine($"  щит #{phase + 1}: на {queen.HealthFraction:P1} здоровья " +
                            $"(порог {threshold:P1}), генераторов {queen.PylonsInPhase}");

            Expect(queen.State == CaveQueen.QueenState.Shielded, $"щит #{phase + 1} встаёт на пороге", text, ref failed);
            Expect(Mathf.Abs(queen.HealthFraction - threshold) < 0.01f, "здоровье упирается в порог, а не проскакивает",
                text, ref failed);

            if (queen.State != CaveQueen.QueenState.Shielded) return failed;

            // Неуязвимость: удар попадает, урона нет.
            var before = queen.Health;
            queen.DamageAt(queen.Centre, 1f, 50f);
            Expect(Mathf.Approximately(queen.Health, before), "под щитом урона нет", text, ref failed);

            // Лечение: пять секунд у неё.
            for (var time = 0f; time < 5f; time += Step)
            {
                PlaceBody(player, stand);
                Advance(director, queen, crowd, player);
            }

            text.AppendLine($"    за 5 с под щитом вылечилась с {before:0} до {queen.Health:0}");
            Expect(queen.Health > before, "под щитом лечится", text, ref failed);

            // Генераторы: где встали.
            var pylons = queen.PylonPositions.ToList();

            Expect(pylons.Count == 3, "три генератора", text, ref failed);

            var steps = new Dictionary<int, int>();
            var field = crowd.Field;
            var local = (float3)world.transform.InverseTransformPoint(queen.LairPosition + Vector3.up);
            field.Reach(local, 60, steps);

            var minSpacing = float.MaxValue;

            for (var i = 0; i < pylons.Count; i++)
            {
                var p = pylons[i];
                var cell = field.FindNearestWalkable((float3)world.transform.InverseTransformPoint(p + Vector3.up * 0.3f), 2);
                var path = cell >= 0 && steps.TryGetValue(cell, out var s) ? s : -1;

                for (var j = i + 1; j < pylons.Count; j++) minSpacing = Mathf.Min(minSpacing, (p - pylons[j]).magnitude);

                var solid = world.IsSolid(p + Vector3.up * 0.6f);

                text.AppendLine($"    генератор {i + 1}: до Матки {(p - queen.LairPosition).magnitude:0.0} по прямой, " +
                                $"{(path >= 0 ? path + " шагов по ходам" : "НЕ СВЯЗАН ходами")}, " +
                                $"{(solid ? "В ПОРОДЕ" : "в пустоте")}");

                Expect(path >= 0, $"генератор {i + 1} связан с логовом ходами", text, ref failed);
                Expect(!solid, $"генератор {i + 1} не в породе", text, ref failed);
            }

            if (pylons.Count > 1)
            {
                text.AppendLine($"    наименьшее расстояние между генераторами: {minSpacing:0.0}");
                Expect(minSpacing >= 4f, "генераторы разнесены, а не свалены в кучу", text, ref failed);
            }

            // Зарядка: встаём у каждого по очереди.
            var litZoneSpawnsBefore = crowd.SpawnedInLitZones;
            var seconds = 0f;

            foreach (var pylon in pylons)
            {
                for (var time = 0f; time < 5f && queen.State == CaveQueen.QueenState.Shielded; time += Step)
                {
                    PlaceBody(player, pylon);
                    Advance(director, queen, crowd, player);
                    seconds += Step;
                }
            }

            text.AppendLine($"    все генераторы запущены за {seconds:0.0} с стояния, " +
                            $"Матка теперь {queen.State}, здоровье {queen.HealthFraction:P0}");

            Expect(queen.State == CaveQueen.QueenState.Awake, "запущенные генераторы снимают щит", text, ref failed);

            // Вернуться к Матке и постоять: в освещённом генераторами пополнения быть не должно.
            for (var time = 0f; time < 5f; time += Step)
            {
                PlaceBody(player, stand);
                Advance(director, queen, crowd, player);
            }

            var intruders = crowd.SpawnedInLitZones - litZoneSpawnsBefore;
            text.AppendLine($"    заведшихся в свете генераторов: {intruders}");
            Expect(intruders == 0, "в свете генераторов пополнение не заводится", text, ref failed);

            return failed;
        }

        // ------------------------------------------------------------------ помощники

        private static void Advance(CaveRunDirector director, CaveQueen queen, SpiderCrowd crowd, CatacombTestRig player)
        {
            director.Tick(Step);
            queen.Tick(Step);
            crowd.Simulate(Step);
            player.TickStatus(Step);
        }

        /// <summary>Ставит стенд так, чтобы подошвы капсулы стояли на полу под точкой.</summary>
        private static void PlaceBody(CatacombTestRig player, Vector3 floor)
        {
            var controller = player.GetComponent<CharacterController>();
            var feet = controller.center.y - controller.height * 0.5f;

            player.transform.rotation = Quaternion.identity;
            player.transform.position = floor + Vector3.up * (controller.skinWidth - feet + 0.05f);
        }

        /// <summary>
        /// Точка на полу в нескольких юнитах от логова, в пустоте. Перебором направлений:
        /// зал бывает узкий, и в одну сторону шесть юнитов — это уже стена.
        /// Луч вниз начат в пустоте (проверено по плотности) — грабли №1 это разрешают.
        /// </summary>
        private static Vector3 FindStandPoint(CatacombWorld world, Vector3 lair, float distance)
        {
            for (var reach = distance; reach >= 2f; reach -= 1f)
            {
                for (var k = 0; k < 16; k++)
                {
                    var angle = k * Mathf.PI * 2f / 16f;
                    var probe = lair + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * reach + Vector3.up * 1.5f;

                    if (world.IsSolid(probe) || world.IsSolid(probe + Vector3.up * 0.8f)) continue;

                    if (Physics.Raycast(probe, Vector3.down, out var hit, 4f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        return hit.point;
                    }
                }
            }

            return lair;
        }

        private static void Expect(bool ok, string what, StringBuilder text, ref int failed)
        {
            text.AppendLine(ok ? $"  ок: {what}" : $"  ПЛОХО: {what}");
            if (!ok) failed++;
        }

        private static int Fail(StringBuilder text, int failed = 1)
        {
            Debug.LogError($"ПРОВЕРКА ЗАБЕГА НЕ ПРОШЛА, проблемных пунктов {failed}:{Environment.NewLine}{text}");
            return 1;
        }

        private static float Share(int part, int whole) => whole > 0 ? part / (float)whole : 0f;

        /// <summary>Ближайший живой паук — куда «игрок» наводит взрыв.</summary>
        private static bool TryNearest(SpiderCrowd crowd, Vector3 from, float reach, List<Vector3> buffer,
            out Vector3 nearest)
        {
            nearest = from;
            crowd.CopyPositions(buffer);

            var best = reach * reach;
            var found = false;

            foreach (var position in buffer)
            {
                var distance = (position - from).sqrMagnitude;
                if (distance >= best) continue;

                best = distance;
                nearest = position;
                found = true;
            }

            return found;
        }

        private static string Clock(float seconds)
        {
            seconds = Mathf.Max(0f, seconds);
            return $"{(int)(seconds / 60f):00}:{(int)(seconds % 60f):00}";
        }
    }
}
