using System;
using System.Diagnostics;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Прогон выживания (M1): неподвижный, нестреляющий игрок под гребнем кривой.
    ///
    /// Вопросы, на которые глазами не ответить:
    /// 1. Кусают ли вообще — и с какой частотой, когда окружён.
    /// 2. Срабатывает ли потолок урона в секунду и сколько он срезает.
    /// 3. За сколько умирает окружённый: на первом гребне цель 4–7 с, в финальном рое 2.5–4 с.
    ///    Это время на рывок; быстрее — смерть читается как сбой, медленнее — толпа не страшна.
    /// 4. Даёт ли рывок неуязвимость на всю свою длительность.
    /// 5. Держит ли толпа: сколько касается капсулы и во сколько это замедляет ходьбу.
    /// 6. Бьёт ли Матка — волна и плевок с уроном, а не только толчком.
    /// </summary>
    public static class SurvivalCheck
    {
        private const float Step = 1f / 30f;

        public static void Run()
        {
            var code = 0;

            try
            {
                code = Report();
            }
            catch (Exception error)
            {
                Debug.LogError("ПРОГОН ВЫЖИВАНИЯ УПАЛ: " + error);
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

            text.AppendLine(SpiderVatBaker.RetuneExisting(false));

            var scene = CheckScene.Open(text, ref failed);
            if (scene == null) return Fail(text, failed + 1);

            var curve = scene.Director.Curve;

            failed += Surrounded(scene, "1-й гребень", curve.firstPeak, 4f, 7f, text);
            failed += Surrounded(scene, "финальный рой (через минуту)", 1f + 60f / scene.Director.StageSeconds,
                2.5f, 4f, text);
            failed += CheckDash(scene, text);
            failed += CheckQueen(scene, text);

            text.AppendLine();
            text.AppendLine($"прогон занял {clock.Elapsed.TotalSeconds:0.0} с");

            if (failed > 0) return Fail(text, failed);

            Debug.Log($"Проверка выживания пройдена:{Environment.NewLine}{text}");
            return 0;
        }

        // ------------------------------------------------------------------ окружён

        private static int Surrounded(CheckScene scene, string label, float stageShare, float minDeath,
            float maxDeath, StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine($"=== {label}: стоит и не стреляет ===");

            scene.RestartAt(stageShare, 25f, Step);

            var crowd = scene.Crowd;
            var vitals = scene.Vitals;
            var model = vitals.Model;

            var bitesBefore = crowd.BitesTotal;
            var rawBefore = crowd.BiteDamageTotal;
            var takenBefore = model.TotalTaken;
            var cappedBefore = model.TotalCapped;

            var firstBite = -1f;
            var time = 0f;
            var touchingSum = 0f;
            var touchingSamples = 0;
            var maxIntake = 0f;

            while (!vitals.IsDead && time < 60f)
            {
                scene.PlaceBody(scene.Entry);
                scene.Advance(Step);
                time += Step;

                if (firstBite < 0f && crowd.BitesTotal > bitesBefore) firstBite = time;

                if (scene.Mobility != null)
                {
                    scene.Mobility.MeasureHold(crowd, scene.Player);
                    touchingSum += scene.Mobility.Touching;
                    touchingSamples++;
                }

                maxIntake = Mathf.Max(maxIntake, model.RecentIntake);
            }

            var bites = crowd.BitesTotal - bitesBefore;
            var raw = crowd.BiteDamageTotal - rawBefore;
            var taken = model.TotalTaken - takenBefore;
            var capped = model.TotalCapped - cappedBefore;

            var fight = firstBite >= 0f ? time - firstBite : 0f;
            var touching = touchingSamples > 0 ? touchingSum / touchingSamples : 0f;
            var hold = scene.Mobility != null ? Mathf.Max(0.35f, 1f - 0.09f * touching) : 1f;
            var near = crowd.MeasureEngagement(15f);

            text.AppendLine($"  живых {crowd.Alive}, рядом (15 юнитов) {near.Near}, бьют {near.Attacking}, " +
                            $"множитель укуса ×{crowd.Directive.DamageScale:0.00}");
            text.AppendLine($"  первый укус через {firstBite:0.0} с, " +
                            $"{(vitals.IsDead ? $"умер через {fight:0.0} с после него" : $"ЖИВ через {time:0} с")}");
            text.AppendLine($"  укусов {bites} ({(fight > 0f ? bites / fight : 0f):0.0} в секунду), " +
                            $"урон укусов до брони и потолка {raw:0} HP ({(fight > 0f ? raw / fight : 0f):0.0} HP/с), " +
                            $"прошло {taken:0} HP, потолок срезал {capped:0} HP");
            text.AppendLine($"  наибольший урон за скользящую секунду: {maxIntake:0.0} HP " +
                            $"(потолок {model.CapShare * model.MaxHealth:0} HP в полную силу)");
            text.AppendLine($"  касаются капсулы в среднем {touching:0.0} — ходьба ×{hold:0.00}");

            CheckScene.Expect(bites > 0, "окружённого кусают", text, ref failed);
            CheckScene.Expect(capped > 0f, "потолок урона в секунду срабатывает", text, ref failed);
            CheckScene.Expect(vitals.IsDead && fight >= minDeath && fight <= maxDeath,
                $"окружённый умирает за {minDeath:0.#}–{maxDeath:0.#} с", text, ref failed);

            return failed;
        }

        // ------------------------------------------------------------------ рывок

        private static int CheckDash(CheckScene scene, StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Рывок: неуязвимость на всю длительность ===");

            if (scene.Mobility == null)
            {
                text.AppendLine("  ПЛОХО: нет PlayerMobility — «Починить свет» его не создал");
                return 1;
            }

            scene.RestartAt(scene.Director.Curve.firstPeak, 25f, Step);

            var vitals = scene.Vitals;
            var dash = scene.Mobility.Dash;
            var crowd = scene.Crowd;

            // Подождать первого укуса, чтобы рывок пришёлся на окружение, а не на пустой коридор.
            var before = crowd.BitesTotal;
            for (var t = 0f; t < 10f && crowd.BitesTotal == before; t += Step)
            {
                scene.PlaceBody(scene.Entry);
                scene.Advance(Step);
            }

            var started = dash.TryStart();
            if (started) vitals.HoldInvulnerable(InvulnReason.Dash, dash.Duration + dash.Grace);

            var health = vitals.Health + vitals.Temp;
            var bites = crowd.BitesTotal;

            for (var t = 0f; t < dash.Duration; t += Step)
            {
                scene.PlaceBody(scene.Entry);
                dash.Step(Step);
                scene.Advance(Step);
            }

            var lost = health - (vitals.Health + vitals.Temp);

            text.AppendLine($"  рывок {(started ? "начат" : "НЕ НАЧАТ")}: зарядов было {dash.MaxCharges}, " +
                            $"за {dash.Duration:0.00} с укусов пришло {crowd.BitesTotal - bites}, потеряно {lost:0.0} HP");

            CheckScene.Expect(started, "рывок стартует с полными зарядами", text, ref failed);
            CheckScene.Expect(lost <= 0.001f, "за длительность рывка урона нет", text, ref failed);

            // Заряды: второй рывок есть, третьего нет, через 1.6 с один вернулся.
            var second = dash.TryStart();
            for (var t = 0f; t < dash.Duration + 0.01f; t += Step) dash.Step(Step);
            var third = dash.TryStart();

            for (var t = 0f; t < dash.Recharge + 0.05f; t += Step) dash.Step(Step);
            var recharged = dash.Ready;

            text.AppendLine($"  второй рывок {(second ? "есть" : "нет")}, третий подряд {(third ? "ЕСТЬ" : "нет")}, " +
                            $"через {dash.Recharge:0.0} с готово {recharged}");

            CheckScene.Expect(second && !third, "два заряда, третьего подряд нет", text, ref failed);
            CheckScene.Expect(recharged >= 1, "заряд восстанавливается", text, ref failed);

            return failed;
        }

        // ------------------------------------------------------------------ Матка

        private static int CheckQueen(CheckScene scene, StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Матка бьёт ===");

            var queen = scene.Queen;
            if (!queen.HasLair)
            {
                text.AppendLine("  ПЛОХО: логова нет");
                return 1;
            }

            scene.RestartAt(0.5f, 10f, Step);

            var stand = FindStandPoint(scene.World, queen.LairPosition, 6f);

            for (var i = 0; i < 20; i++)
            {
                scene.PlaceBody(stand);
                scene.Advance(Step);
            }

            if (!scene.Director.TrySummonQueen(scene.Player.transform.position))
            {
                text.AppendLine("  ПЛОХО: у логова Матка не просыпается");
                return 1;
            }

            var vitals = scene.Vitals;
            vitals.ResetForRun();

            // Толпа у логова загрызает стоящего быстрее, чем Матка успеет ударить: здесь мерится
            // её урон, а не выживание, поэтому здоровья с запасом — потолок урона при этом тоже
            // растёт, и удары проходят целиком.
            vitals.Model.SetMaxHealth(100000f);

            var shockDamage = 0f;
            var spitDamage = 0f;

            void OnDamaged(DamageHit hit, DamageResult result)
            {
                if (hit.Kind == DamageKind.Shock) shockDamage += result.Dealt;
                if (hit.Kind == DamageKind.Spit) spitDamage += result.Dealt;
            }

            vitals.Damaged += OnDamaged;

            try
            {
                for (var t = 0f; t < 12f && !vitals.IsDead; t += Step)
                {
                    scene.PlaceBody(stand);
                    scene.Advance(Step);
                }
            }
            finally
            {
                vitals.Damaged -= OnDamaged;
            }

            text.AppendLine($"  за 12 с рядом: волна сняла {shockDamage:0} HP, плевки {spitDamage:0} HP, " +
                            $"здоровье {vitals.Health:0}/{vitals.MaxHealth:0}{(vitals.IsDead ? " (УМЕР)" : "")}");

            CheckScene.Expect(shockDamage > 0f, "ударная волна ранит", text, ref failed);
            CheckScene.Expect(spitDamage > 0f, "плевок ранит", text, ref failed);

            return failed;
        }

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

        private static int Fail(StringBuilder text, int failed)
        {
            Debug.LogError($"ПРОВЕРКА ВЫЖИВАНИЯ НЕ ПРОШЛА, проблемных пунктов {failed}:{Environment.NewLine}{text}");
            return 1;
        }
    }
}
