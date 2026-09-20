using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Прогон механики света: построить уровень, найти генератор, запустить, дожечь
    /// и померить то, что глазами за один раз не увидишь.
    ///
    /// Зачем отдельный прогон, а не пункт в <see cref="CatacombBatchCheck"/>. Тот меряет
    /// СТИЛЬ на полностью зажжённом уровне — долю тёмных пикселей, полосу яркости,
    /// максимум в кадре. Здесь же проверяется ровно обратное: что уровень стартует
    /// погашенным и светлеет от действий игрока. Смешивать это в одном прогоне значит
    /// получить набор чисел, про который непонятно, какое из них про что.
    ///
    /// Четыре вопроса, на которые прогон отвечает, и все четыре глазами проверяются плохо:
    ///
    /// 1. Стартует ли уровень действительно погашенным. Тёмный кадр бывает и от того,
    ///    что светильники не расставились вовсе, — это надо различать по счётчику,
    ///    а не по картинке.
    /// 2. Зажигается ли ровно район, а не весь уровень и не три лампы. Радиус района
    ///    и разрядка генераторов подобраны так, чтобы районы не перекрывались,
    ///    и если они всё же перекрылись, видно это только числом.
    /// 3. Перестаёт ли заводиться пополнение в освещённом. Запрет живёт в отборе клеток
    ///    спавна, и промах там выглядит как «пауки всё равно откуда-то берутся» —
    ///    то есть неотличимо от забежавших снаружи, которым забегать можно.
    /// 4. Разбегается ли орда от вспышки. Это единственная награда за самую тяжёлую
    ///    часть забега, и если паника не доехала, кадр показывает просто толпу.
    /// </summary>
    public static class CatacombLightCheck
    {
        private const string ScenePath = "Assets/Scenes/test.unity";
        private const string StyleMenu = "Tools/Mine Generator/Свет: как в DRG (тьма и цвет)";

        /// <summary>
        /// Секунд поведения после вспышки — вся паника целиком, а не её середина.
        ///
        /// Мерить на середине было ошибкой: к этому моменту толпа только начала
        /// расходиться, и замер показывал успех по среднему расстоянию при том, что
        /// в кадре не менялось ничего.
        /// </summary>
        private const float PanicSeconds = 10f;

        /// <summary>
        /// Секунд осады перед вспышкой.
        ///
        /// Двадцать, а не три, и это не запас «на всякий случай». Прогон обязан подойти
        /// к вспышке с той плотностью, с какой к ней подходит ИГРОК: он держит генератор
        /// полторы минуты, всплеск тянет население вверх, и к моменту вспышки вокруг него
        /// стоит потолок в <c>capacity</c> — на сцене это 1200 особей. Три секунды
        /// добавляли к четырём сотням полторы, и прогон мерил панику на трети настоящей
        /// толпы. Разница не количественная: на 400 особях паника проходила с отличием,
        /// а на 1200 толпа не уходила из района ВООБЩЕ — 1180 особей в сфере до вспышки
        /// и 1165 через десять секунд.
        ///
        /// Досыл идёт по <c>spawnRate</c> (на сцене 45 в секунду), поэтому от четырёх
        /// сотен до потолка нужно около восемнадцати секунд.
        /// </summary>
        private const float SurgeSeconds = 20f;

        /// <summary>
        /// Секунд после ухода игрока из отвоёванного района.
        ///
        /// Дюжина, а не три: паук идёт три-пять юнитов в секунду, а уйти ему надо
        /// за радиус района. За три секунды он не успел бы, и замер показал бы
        /// «район не пустеет» там, где толпа просто ещё в пути.
        /// </summary>
        private const float LeaveSeconds = 12f;

        private const float Step = 1f / 60f;

        /// <summary>
        /// Радиус, по которому судится разбегание, юниты.
        ///
        /// Ближний круг, а не весь район. Игрок стоит у генератора и видит тварей вокруг
        /// себя, а не средний радиус толпы по району в тридцать два юнита: те, кто
        /// в двадцати пяти, в кадре просто не читаются — их закрывает порода и туман.
        /// Совпадает с nearRadius самой толпы, по которому она и так ограничивает
        /// давку вокруг игрока.
        /// </summary>
        private const float SightRadius = 12f;

        public static void Run()
        {
            var code = 0;

            try
            {
                code = Report();
            }
            catch (Exception error)
            {
                Debug.LogError("ПРОГОН СВЕТА УПАЛ: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        /// <summary>То же без выхода из процесса — для вызова через run_script при открытом редакторе.</summary>
        public static int Report()
        {
            var text = new StringBuilder();
            var failed = 0;

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // Компоненты создаёт пункт меню, а не прогон: значения полей живут
            // в сохранённой сцене, и проверка на них мерила бы вчерашние умолчания
            // (грабли №8). Тот же приём, что у проверки толпы.
            if (!EditorApplication.ExecuteMenuItem(StyleMenu)) throw new Exception("не найден пункт меню: " + StyleMenu);

            var world = UnityEngine.Object.FindFirstObjectByType<CatacombWorld>();
            if (world == null) throw new Exception("НЕТ CatacombWorld на сцене " + ScenePath);

            world.GenerateImmediate();

            var fixtures = UnityEngine.Object.FindFirstObjectByType<CaveFixtures>();
            if (fixtures == null) throw new Exception("НЕТ CaveFixtures — светильники не расставлены");

            var generators = UnityEngine.Object.FindFirstObjectByType<CaveGenerators>();
            if (generators == null) throw new Exception("НЕТ CaveGenerators — цели у забега нет");

            generators.EnsureLinks();

            text.AppendLine("=== Старт уровня ===");
            text.AppendLine($"  светильников расставлено: {fixtures.Count}");
            text.AppendLine($"  из них горит: {fixtures.LitCount}");
            text.AppendLine($"  генераторов: {generators.Count}, зажжено {generators.LitCount}");

            if (fixtures.Count == 0)
            {
                text.AppendLine("  ПЛОХО: светильников нет вовсе — гасить и зажигать нечего");
                failed++;
            }

            // Главное свойство старта: мир тёмный. Если тут не ноль, значит startDark
            // не доехал до сцены, и весь забег пройдёт при включённом свете.
            if (fixtures.LitCount != 0)
            {
                text.AppendLine("  ПЛОХО: уровень стартует со включённым светом");
                failed++;
            }

            if (generators.Count == 0)
            {
                text.AppendLine("  ПЛОХО: ни одного генератора — идти игроку некуда");
                failed++;

                Debug.LogError($"ПРОВЕРКА СВЕТА НЕ ПРОШЛА:{Environment.NewLine}{text}");
                return 1;
            }

            var player = UnityEngine.Object.FindFirstObjectByType<CatacombTestRig>();
            if (player == null) throw new Exception("НЕТ CatacombTestRig — за кем бежать толпе, неизвестно");

            // Игрока на точку входа, иначе прогон не воспроизводится: «ближайший
            // генератор» считается от его позиции, а она живёт в сохранённой сцене
            // и меняется от любого другого прогона, который сцену сохранил. Два
            // прогона подряд так выбрали РАЗНЫЕ генераторы и дали разные числа.
            if (world.TryGetSpawnPoint(out var entry)) player.transform.position = entry;

            var crowd = CatacombCrowdCheck.EnsureCrowd(world, text, ref failed);
            if (crowd == null)
            {
                Debug.LogError($"ПРОВЕРКА СВЕТА НЕ ПРОШЛА:{Environment.NewLine}{text}");
                return 1;
            }

            // Генераторы пересоздавались до толпы, поэтому ссылку на неё они не нашли.
            generators.EnsureLinks();

            if (!generators.TryGetNearestDark(player.transform.position, out var point, out _))
            {
                text.AppendLine("  ПЛОХО: незажжённых генераторов нет сразу после генерации");
                failed++;

                Debug.LogError($"ПРОВЕРКА СВЕТА НЕ ПРОШЛА:{Environment.NewLine}{text}");
                return 1;
            }

            // Ставим игрока вплотную к генератору: проверяем весь путь запуска целиком,
            // включая проверку дистанции, а не только то, что зажигание работает.
            player.transform.position = point;

            crowd.Rebuild();

            if (!crowd.HasField)
            {
                text.AppendLine("  ПЛОХО: сетка навигации не построена — толпу мерить нечем");
                failed++;

                Debug.LogError($"ПРОВЕРКА СВЕТА НЕ ПРОШЛА:{Environment.NewLine}{text}");
                return 1;
            }

            crowd.FillPopulation(400);

            var radius = generators.DistrictRadius;
            var positions = new List<Vector3>();

            crowd.CopyPositions(positions);

            var beforeNear = Near(positions, point, radius);
            var beforeMean = MeanDistance(positions, point);
            var beforeAlive = crowd.Alive;

            text.AppendLine();
            text.AppendLine("=== До запуска ===");
            text.AppendLine($"  живых особей: {beforeAlive}");
            text.AppendLine($"  из них в районе (радиус {radius:0}): {beforeNear}");
            text.AppendLine($"  среднее расстояние до генератора: {beforeMean:0.0}");

            // --- Запуск ---
            if (!generators.TryActivate(player.transform.position))
            {
                text.AppendLine("  ПЛОХО: генератор не запускается, стоя вплотную к нему");
                failed++;

                Debug.LogError($"ПРОВЕРКА СВЕТА НЕ ПРОШЛА:{Environment.NewLine}{text}");
                return 1;
            }

            // Осада меряется двумя половинами: нужно не «прошло двадцать секунд»,
            // а «население ВЫШЛО НА ПОЛКУ». Сколько там окажется особей, прогон знать
            // не должен — потолок задаётся capacity и spawnRate на сцене, и подставлять
            // сюда число значит мерить вчерашнюю настройку.
            Simulate(crowd, SurgeSeconds * 0.5f);

            var halfway = crowd.Alive;

            Simulate(crowd, SurgeSeconds * 0.5f);

            var surgeAlive = crowd.Alive;

            // Снимок ПЕРЕД вспышкой, и он же база для паники.
            //
            // Сравнивать разбегание с числом до запуска нельзя: всплеск за время
            // разгорания добавляет полторы сотни особей, и район после вспышки
            // честно содержит больше, чем содержал до осады, — при том что паника
            // отработала. Первый прогон на этом и споткнулся.
            positions.Clear();
            crowd.CopyPositions(positions);

            var chargeNear = Near(positions, point, radius);
            var chargeMean = MeanDistance(positions, point);
            var chargeSight = Near(positions, point, SightRadius);

            text.AppendLine();
            text.AppendLine("=== Разгорание ===");
            text.AppendLine($"  живых через {SurgeSeconds:0.0} с: {surgeAlive} " +
                            $"(было {beforeAlive}, на середине осады {halfway})");
            text.AppendLine($"  в районе перед вспышкой: {chargeNear}, среднее расстояние {chargeMean:0.0}");
            text.AppendLine($"  из них вплотную к игроку (ближе {SightRadius:0}): {chargeSight}");

            // Всплеск — это вся кульминация забега. Если население не растёт, запуск
            // генератора ничем не отличается от прогулки, и оборонять нечего.
            if (surgeAlive <= beforeAlive)
            {
                text.AppendLine("  ПЛОХО: орда не отвечает на запуск — населению некуда расти");
                failed++;
            }

            // Плотность здесь — УСЛОВИЕ замера, а не его результат. На четырёх сотнях
            // паника проходит с отличием и при поломке, которую на полутора тысячах
            // видно сразу, поэтому недобор толпы к вспышке означает не «толпа слабая»,
            // а «следующая проверка ничего не значит».
            if (surgeAlive > halfway * 1.05f)
            {
                text.AppendLine($"  ПЛОХО: население ещё росло к вспышке ({halfway} -> {surgeAlive}) — " +
                                "осада не вышла на полку, и паника меряется не на той толпе, " +
                                "что стоит вокруг игрока в игре");
                failed++;
            }

            // --- Вспышка ---
            if (!generators.ForceFinish())
            {
                text.AppendLine("  ПЛОХО: нечего дожигать — разгорание не началось");
                failed++;
            }

            var litAfter = fixtures.LitCount;

            text.AppendLine();
            text.AppendLine("=== Вспышка ===");
            text.AppendLine($"  светильников загорелось: {litAfter} из {fixtures.Count}");
            text.AppendLine($"  генераторов зажжено: {generators.LitCount} из {generators.Count}");
            text.AppendLine($"  точка старта следующего забега: " +
                            $"{(generators.Respawn.HasValue ? generators.Respawn.Value.ToString("0.0") : "НЕТ")}");

            if (litAfter == 0)
            {
                text.AppendLine("  ПЛОХО: район не загорелся — радиус не достаёт ни до одного светильника");
                failed++;
            }

            // Весь уровень разом — тоже поломка: районы должны перекрывать уровень
            // по частям, иначе первый же забег заканчивает игру.
            if (litAfter == fixtures.Count && generators.Count > 1)
            {
                text.AppendLine("  ПЛОХО: от одного генератора зажёгся ВЕСЬ уровень — район слишком широк");
                failed++;
            }

            if (!generators.Respawn.HasValue)
            {
                text.AppendLine("  ПЛОХО: зажжённый генератор не стал точкой старта");
                failed++;
            }

            // --- Паника ---
            Simulate(crowd, PanicSeconds);

            positions.Clear();
            crowd.CopyPositions(positions);

            var afterNear = Near(positions, point, radius);
            var afterMean = MeanDistance(positions, point);
            var afterSight = Near(positions, point, SightRadius);

            text.AppendLine();
            text.AppendLine("=== Паника ===");
            text.AppendLine($"  в районе через {PanicSeconds:0.0} с: {afterNear} (перед вспышкой {chargeNear})");
            text.AppendLine($"  среднее расстояние до генератора: {afterMean:0.0} (перед вспышкой {chargeMean:0.0})");

            // Разбегание меряется расстоянием, а не числом живых: убийств здесь нет,
            // и упасть число в районе может только за счёт того, что особи ушли.
            //
            // Порог не «стало больше нуля», а пятая часть. Первый прогон это показал
            // наглядно: среднее выросло с 18.8 до 23.1 и формально прошло, а число
            // особей в районе при этом ВЫРОСЛО с 850 до 907 — то есть толпа не
            // разбегалась, а продолжала прибывать, просто чуть шире размазавшись.
            // Слабый порог на хаотичной системе не значит ничего.
            var spread = chargeMean > 0.01f ? afterMean / chargeMean - 1f : 0f;

            text.AppendLine($"  разбежались на {spread:P0} от прежнего радиуса");

            if (spread < 0.2f)
            {
                text.AppendLine("  ПЛОХО: толпа не разбежалась — паника до особей не доехала");
                failed++;
            }

            // ГЛАВНОЕ число — сколько тварей осталось ВПЛОТНУЮ, а не средний радиус
            // и не остаток по всему району.
            //
            // Три метрики одного события расходятся до неузнаваемости, и выбрать надо ту,
            // что совпадает с глазами. Среднее расстояние по району растёт от того, что
            // дальние убежали далеко, и про ближних не говорит НИЧЕГО: оно показывало
            // +55% при падении ближнего круга на семь процентов, то есть рапортовало
            // успех там, где игрок не видел ровным счётом ничего. Остаток по району
            // в тридцать два юнита тоже мимо: особи в двадцати пяти юнитах закрыты
            // породой и туманом.
            var sightDrop = chargeSight > 0 ? 1f - afterSight / (float)chargeSight : 0f;
            var districtDrop = chargeNear > 0 ? 1f - afterNear / (float)chargeNear : 0f;

            text.AppendLine($"  вплотную к игроку: {afterSight} (было {chargeSight}), то есть минус {sightDrop:P0}");
            text.AppendLine($"  по всему району: минус {districtDrop:P0}");

            if (sightDrop < 0.66f)
            {
                text.AppendLine($"  ПЛОХО: вплотную осталось {afterSight} из {chargeSight} — " +
                                "разбегание в кадре не прочитается");
                failed++;
            }

            // Ближнего круга ОДНОГО мало, и это выяснилось дорого. Толпа умеет освободить
            // его, никуда при этом не уйдя: особи переливаются из середины района в его
            // же кольцо и стоят там. Замер поймал это в чистом виде — вплотную стало
            // меньше, а в сфере паники осталось ровно столько же (1180 до вспышки
            // и 1165 через десять секунд). Игрок в такой момент отходит на пару шагов
            // и упирается в ту же орду.
            if (districtDrop < 0.25f)
            {
                text.AppendLine($"  ПЛОХО: район не пустеет — {afterNear} из {chargeNear}. " +
                                "Толпа не разбежалась, а только раздалась вширь внутри района");
                failed++;
            }

            // --- Запрет пополнения ---
            //
            // Игрок УХОДИТ дальше, и это не упрощение замера, а единственный способ
            // проверить именно запрет спавна. Первый прогон мерил иначе — игрок стоял
            // у зажжённого генератора, — и район не пустел никогда: пополнение там
            // действительно не заводилось, но восемь сотен уже живых особей бежали
            // не в район, а на ИГРОКА, который в нём стоял. Числа выглядели как поломка
            // запрета, хотя запрет работал.
            //
            // Уход к следующему генератору — ровно то, что игрок делает в игре: район
            // отвоёван, здесь больше нечего делать.
            var away = point;

            if (generators.TryGetNearestDark(point, out var nextPoint, out _)) away = nextPoint;
            else if (world.TryGetSpawnPoint(out var spawn)) away = spawn;

            player.transform.position = away;

            // Считаем только то, что заведётся ПОСЛЕ ухода: всё, что появилось раньше,
            // появилось законно — район тогда ещё не горел.
            crowd.ResetLitZoneCounter();

            text.AppendLine();
            text.AppendLine($"  игрок ушёл из района на {(away - point).magnitude:0} юнитов");

            // Толпу НЕ пересобираем: Rebuild снёс бы всех живых и расселил заново вокруг
            // новой точки, и запрет проверялся бы на пустом месте. Поле потока
            // перестроится само на первом же шаге — игрок сменил клетку.
            Simulate(crowd, LeaveSeconds);

            positions.Clear();
            crowd.CopyPositions(positions);

            var refilled = Near(positions, point, radius);

            text.AppendLine();
            text.AppendLine("=== Запрет пополнения в освещённом ===");
            text.AppendLine($"  в районе через {LeaveSeconds:0.0} с после ухода игрока: {refilled} " +
                            $"(перед вспышкой {chargeNear})");
            text.AppendLine($"  живых всего: {crowd.Alive} — всплеск снят вспышкой");
            text.AppendLine($"  завелось внутри освещённого района: {crowd.SpawnedInLitZones} (должен быть ноль)");

            // Главная проверка запрета — ПРЯМАЯ: ни одна особь не должна завестись
            // внутри освещённого района.
            //
            // Порог по остатку («пусть останется меньше половины») пробовался и отвергнут:
            // он меряет не запрет, а геометрию. В проходном районе толпа вытекает
            // за десяток секунд, в тупиковом выбирается минуту, и на двух разных
            // генераторах один и тот же работающий запрет дал 402 и 563 из 985 —
            // то есть порог в половину был подогнан под первый попавшийся район.
            // Это грабли №13 в новом наряде: один замер на одном месте ничего не значит.
            if (crowd.SpawnedInLitZones > 0)
            {
                text.AppendLine($"  ПЛОХО: в освещённом районе завелось {crowd.SpawnedInLitZones} особей — " +
                                "запрет спавна не работает");
                failed++;
            }

            // Остаток — не гейт, а показание: он говорит, насколько быстро вытекает
            // именно этот район, и полезен глазами, а не порогом.
            if (refilled >= chargeNear)
            {
                text.AppendLine("  ПЛОХО: район не пустеет вовсе, хотя игрок ушёл");
                failed++;
            }

            text.AppendLine();
            text.AppendLine(failed == 0
                ? "ИТОГ: механика света работает целиком"
                : $"ИТОГ: провалов {failed}");

            if (failed > 0)
            {
                Debug.LogError($"ПРОВЕРКА СВЕТА НЕ ПРОШЛА:{Environment.NewLine}{text}");
                return 1;
            }

            Debug.Log($"ПРОВЕРКА СВЕТА ПРОШЛА:{Environment.NewLine}{text}");
            return 0;
        }

        private static void Simulate(SpiderCrowd crowd, float seconds)
        {
            var steps = Mathf.RoundToInt(seconds / Step);

            for (var i = 0; i < steps; i++) crowd.Simulate(Step);
        }

        private static int Near(List<Vector3> positions, Vector3 from, float radius)
        {
            var radiusSqr = radius * radius;
            var count = 0;

            foreach (var position in positions)
            {
                if ((position - from).sqrMagnitude <= radiusSqr) count++;
            }

            return count;
        }

        private static float MeanDistance(List<Vector3> positions, Vector3 from)
        {
            if (positions.Count == 0) return 0f;

            var sum = 0f;
            foreach (var position in positions) sum += (position - from).magnitude;

            return sum / positions.Count;
        }
    }
}
