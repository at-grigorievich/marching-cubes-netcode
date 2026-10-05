using System;
using System.Diagnostics;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Прогон особых пауков (M3.2).
    ///
    /// Вопросы, на которые глазами не ответить:
    /// 1. Сколько особых приходит за стадию и не появляется ли хоть один в поле зрения игрока.
    /// 2. Доходит ли каждый до своего действия: плевун плюёт и оставляет лужу, ловчий прыгает
    ///    и прижимает, ткач цепляет нитью и тянет, бомбардир лопается.
    /// 3. Работают ли ответы: рывок в момент визга — промах ловчего, парирование кирки
    ///    отбивает плевок, бомбардир, убитый на подходе, косит пауков вокруг себя.
    /// </summary>
    public static class SpecialsCheck
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
                Debug.LogError("ПРОГОН ОСОБЫХ УПАЛ: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        public static int Report()
        {
            var text = new StringBuilder();
            var failed = 0;
            var clock = Stopwatch.StartNew();

            var scene = CheckScene.Open(text, ref failed);
            if (scene == null) return Fail(text, failed + 1);

            var specials = UnityEngine.Object.FindAnyObjectByType<CaveSpecials>();
            var arsenal = scene.Player.GetComponent<PlayerArsenal>();

            if (specials == null || arsenal == null)
            {
                text.AppendLine("  ПЛОХО: нет CaveSpecials или арсенала — «Починить свет» их не создал");
                return Fail(text, failed + 1);
            }

            specials.EnsureLinks();
            arsenal.Prepare();
            arsenal.Context.Audio.Muted = true;

            failed += Schedule(scene, specials, text);
            failed += Spitter(scene, specials, arsenal, text);
            failed += Lurker(scene, specials, text);
            failed += Weaver(scene, specials, text);
            failed += Bomber(scene, specials, text);
            failed += Affixes(scene, specials, text);
            failed += Queens(scene, specials, text);

            specials.Clear();
            specials.ScheduleEnabled = true;

            text.AppendLine();
            text.AppendLine($"прогон занял {clock.Elapsed.TotalSeconds:0.0} с");

            if (failed > 0) return Fail(text, failed);

            Debug.Log($"Проверка особых пройдена:{Environment.NewLine}{text}");
            return 0;
        }

        // ------------------------------------------------------------------ расписание

        /// <summary>Стадия целиком под неуязвимостью: сколько особых пришло, где, когда.</summary>
        private static int Schedule(CheckScene scene, CaveSpecials specials, StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Расписание: стадия целиком, игрок стоит на точке входа, бессмертен ===");

            scene.RestartAt(0f, 0f, Step);
            specials.Clear();
            specials.ScheduleEnabled = true;
            scene.Vitals.HoldInvulnerable(InvulnReason.Debug, 9999f);

            var peakAlive = 0;
            var inSwarm = 0;
            var projectiles = scene.Player.GetComponent<PlayerArsenal>().Context.Projectiles;

            for (var time = 0f; time < scene.Director.StageSeconds; time += Step)
            {
                scene.PlaceBody(scene.Entry);
                var before = Total(specials.Spawned);

                scene.Advance(Step);
                specials.Tick(Step);
                projectiles.Tick(Step);

                // Игрок снимает особого через 12 с: бессмертный бот, никого не убивающий, держал бы
                // лимит живых особыми, пришедшими первыми, и расписание стояло бы всю стадию.
                foreach (var special in specials.All)
                {
                    if (!special.Dead && special.Age > 12f) special.TakeHit(99999f, HitKind.Bullet, DamageSource.Debug, scene.Entry);
                }

                if (Total(specials.Spawned) > before && scene.Director.SwarmActive) inSwarm++;
                peakAlive = Mathf.Max(peakAlive, specials.Alive);
            }

            scene.Vitals.HoldInvulnerable(InvulnReason.Debug, 0f);

            var total = Total(specials.Spawned);

            text.AppendLine($"  пришло {total}: плевунов {specials.Spawned[0]}, ловчих {specials.Spawned[1]}, " +
                            $"ткачей {specials.Spawned[2]}, бомбардиров {specials.Spawned[3]}; живых одновременно до {peakAlive}; " +
                            $"в поле зрения {specials.SpawnedInView}, в рой {inSwarm}");

            // Стадия 7 минут, первый на 45-й секунде, пауза 20–40 с: от 8 до 20.
            CheckScene.Expect(total >= 8 && total <= 20, "за стадию приходит 8–20 особых", text, ref failed);
            CheckScene.Expect(specials.SpawnedInView == 0, "ни один не появляется в поле зрения", text, ref failed);
            CheckScene.Expect(inSwarm == 0, "в рой особые не приходят", text, ref failed);
            CheckScene.Expect(peakAlive <= 2, "на ярусе 1 живых не больше двух", text, ref failed);

            return failed;
        }

        private static int Total(int[] counts)
        {
            var sum = 0;
            foreach (var count in counts) sum += count;
            return sum;
        }

        // ------------------------------------------------------------------ по одному

        /// <summary>Свежая раскладка: толпы нет, особых нет, игрок на точке входа, полное здоровье.</summary>
        private static void Fresh(CheckScene scene, CaveSpecials specials)
        {
            // Снаряды прошлого раздела — прочь: плевки, выпущенные в расписании, долетали
            // до игрока уже в следующем разделе и удваивали счёт попаданий.
            scene.Player.GetComponent<PlayerArsenal>().Context.Projectiles.Clear();

            // И тяга прошлых нитей: ход стенда в прогоне не идёт, и она копилась бы до первого замера.
            scene.Player.ConsumeDrag();

            scene.RestartAt(0f, 0f, Step);
            scene.Crowd.ClearAll();
            specials.Clear();
            specials.ScheduleEnabled = false;
            scene.Vitals.ResetForRun();
        }

        /// <summary>Шаг особых и снарядов без толпы и директора — особый один на один с игроком.</summary>
        private static void Tick(CheckScene scene, CaveSpecials specials, PlayerArsenal arsenal, PlayerCommands commands)
        {
            scene.PlaceBody(scene.Entry);
            scene.Player.TickStatus(Step);
            scene.Vitals.Tick(Step);

            specials.Tick(Step);

            if (arsenal != null) arsenal.Tick(commands, Step, true);

            var projectiles = scene.Player.GetComponent<PlayerArsenal>().Context.Projectiles;
            projectiles.Tick(Step);

            scene.Crowd.StepStatic(Step);
            scene.Crowd.FlushKills();
        }

        /// <summary>То же, но тело игрока стоит в заданной точке пола.</summary>
        private static void TickAt(CheckScene scene, CaveSpecials specials, Vector3 feet)
        {
            scene.PlaceBody(feet);
            scene.Player.TickStatus(Step);
            scene.Vitals.Tick(Step);
            specials.Tick(Step);
            scene.Player.GetComponent<PlayerArsenal>().Context.Projectiles.Tick(Step);
            scene.Crowd.StepStatic(Step);
            scene.Crowd.FlushKills();
        }

        /// <summary>Особого — на поверхность в 10–16 шагах от игрока.</summary>
        private static SoloSpider Place(CheckScene scene, CaveSpecials specials, SpecialKind kind, int minSteps, int maxSteps,
            float minUp, float maxUp)
        {
            var random = new System.Random(7 + (int)kind);

            if (!scene.Crowd.TryPickSurfaceCell(minSteps, maxSteps, minUp, maxUp, random, null, out var foot, out var up)) return null;

            return specials.SpawnAt(kind, foot, up);
        }

        private static int Spitter(CheckScene scene, CaveSpecials specials, PlayerArsenal arsenal, StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Плевун ===");

            Fresh(scene, specials);
            var spitter = Place(scene, specials, SpecialKind.Spitter, 10, 16, -1f, 0.6f);

            if (spitter == null)
            {
                text.AppendLine("  ПЛОХО: некуда поставить плевуна");
                return 1;
            }

            var before = scene.Vitals.Health;
            var spits = 0;
            var hits = specials.AcidHits;
            var puddles = 0;
            var fired = arsenal.Context.Projectiles.Fired;

            specials.Cued += Count;

            for (var time = 0f; time < 20f; time += Step)
            {
                Tick(scene, specials, null, PlayerCommands.Empty);
                puddles = Mathf.Max(puddles, specials.PuddleCount);
            }

            specials.Cued -= Count;

            text.AppendLine($"  за 20 с: замахов {spits}, плевков {arsenal.Context.Projectiles.Fired - fired}, попаданий кислотой {specials.AcidHits - hits}, луж до {puddles}, " +
                            $"здоровье {before:0} → {scene.Vitals.Health:0}");

            CheckScene.Expect(spits >= 3, "плевун плюёт (раз в 4 с, с замахом)", text, ref failed);
            CheckScene.Expect(specials.AcidHits - hits > 0 && scene.Vitals.Health < before, "кислота ранит стоящего", text, ref failed);

            // Парирование: кирка в руках, ПКМ в момент, когда плевок рядом.
            Fresh(scene, specials);
            spitter = Place(scene, specials, SpecialKind.Spitter, 10, 16, -1f, 0.6f);

            arsenal.ClearLoadout();
            arsenal.Give(WeaponId.PickHammer, 3);

            var parried = 0;
            var hitsBefore = specials.AcidHits;
            var reflectedBefore = arsenal.Context.Projectiles.ParriedTotal;

            for (var time = 0f; time < 20f; time += Step)
            {
                var commands = PlayerCommands.Empty;

                // Смотрит на плевуна, а ПКМ жмёт, когда плевок в двух юнитах.
                if (spitter != null) scene.Player.transform.rotation = Quaternion.LookRotation(spitter.Centre - scene.Player.transform.position);
                if (arsenal.Context.Projectiles.HasHostileNear(scene.Player.BodyCentre, 2.6f))
                {
                    commands.AltPressed = true;
                    commands.AltHeld = true;
                    parried++;
                }

                Tick(scene, specials, arsenal, commands);
            }

            var landed = specials.AcidHits - hitsBefore;
            var reflected = arsenal.Context.Projectiles.ParriedTotal - reflectedBefore;

            text.AppendLine($"  парирование киркой (откат пробоя 8 с): нажатий у плевка {parried}, отбито {reflected}, " +
                            $"попало {landed}");
            CheckScene.Expect(reflected > 0, "парирование отбивает плевок", text, ref failed);

            arsenal.ClearLoadout();

            return failed;

            void Count(SoloSpider special, SpecialCue cue)
            {
                if (cue == SpecialCue.Spit) spits++;
            }
        }

        private static int Lurker(CheckScene scene, CaveSpecials specials, StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Ловчий ===");

            Fresh(scene, specials);
            var lurker = (LurkerSpider)Place(scene, specials, SpecialKind.Lurker, 10, 14, 0.7f, 1.01f);

            if (lurker == null)
            {
                text.AppendLine("  ПЛОХО: некуда поставить ловчего");
                return 1;
            }

            var pinned = 0f;

            for (var time = 0f; time < 25f && lurker.Pins == 0; time += Step)
            {
                Tick(scene, specials, null, PlayerCommands.Empty);
            }

            for (var time = 0f; time < 3f; time += Step)
            {
                if (scene.Player.IsPinned) pinned += Step;
                Tick(scene, specials, null, PlayerCommands.Empty);
            }

            text.AppendLine($"  прижимов {lurker.Pins}, промахов {lurker.Misses}, игрок прижат {pinned:0.0} с");
            CheckScene.Expect(lurker.Pins > 0 && pinned >= 1.2f, "ловчий доходит, прыгает и прижимает", text, ref failed);

            // Рывок в момент визга — прыжок уходит в пустоту.
            Fresh(scene, specials);
            lurker = (LurkerSpider)Place(scene, specials, SpecialKind.Lurker, 10, 14, 0.7f, 1.01f);

            var shrieked = false;
            var feet = scene.Entry;
            var dodges = new System.Collections.Generic.List<Vector3>();
            specials.Cued += OnCue;

            for (var time = 0f; time < 25f && lurker.Pins + lurker.Misses == 0; time += Step)
            {
                // Рывок на визг: тело уходит на 4–6 шагов в сторону — туда, где прыжок не ждёт.
                if (shrieked && scene.Mobility != null && scene.Mobility.Dash.TryStart())
                {
                    scene.Vitals.HoldInvulnerable(InvulnReason.Dash, scene.Mobility.Dash.Duration + scene.Mobility.Dash.Grace);
                    scene.Crowd.PickFloorPoints(scene.Entry + Vector3.up, 1, 4, 6, null, dodges);
                    if (dodges.Count > 0) feet = dodges[0];
                    shrieked = false;
                }

                scene.Mobility?.Dash.Step(Step);
                TickAt(scene, specials, feet);
            }

            specials.Cued -= OnCue;

            text.AppendLine($"  с рывком на визг: прижимов {lurker.Pins}, промахов {lurker.Misses}");
            CheckScene.Expect(lurker.Misses > 0 && lurker.Pins == 0, "рывок на визг — промах ловчего", text, ref failed);

            return failed;

            void OnCue(SoloSpider special, SpecialCue cue)
            {
                // Рывок — когда до прыжка осталось меньше, чем длится рывок: в самом конце визга.
                if (cue == SpecialCue.Shriek) shrieked = true;
            }
        }

        private static int Weaver(CheckScene scene, CaveSpecials specials, StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Ткач ===");

            Fresh(scene, specials);
            var weaver = (WeaverSpider)Place(scene, specials, SpecialKind.Weaver, 12, 16, 0.7f, 1.01f);

            if (weaver == null)
            {
                text.AppendLine("  ПЛОХО: некуда поставить ткача");
                return 1;
            }

            var pulled = 0f;
            var dragged = 0f;

            for (var time = 0f; time < 25f; time += Step)
            {
                Tick(scene, specials, null, PlayerCommands.Empty);

                if (weaver.Pulling)
                {
                    pulled += Step;
                    dragged += scene.Player.ConsumeDrag().magnitude * Step;
                }
            }

            text.AppendLine($"  поймал {weaver.Catches} раз, тянул {pulled:0.0} с, вытянул бы на {dragged:0.0} юнита");
            CheckScene.Expect(weaver.Catches > 0 && pulled > 1f && dragged > 3f, "нить цепляет и тянет", text, ref failed);

            return failed;
        }

        private static int Bomber(CheckScene scene, CaveSpecials specials, StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Бомбардир ===");

            // Сам доходит и лопается.
            Fresh(scene, specials);
            var bomber = (BomberSpider)Place(scene, specials, SpecialKind.Bomber, 6, 10, 0.7f, 1.01f);

            if (bomber == null)
            {
                text.AppendLine("  ПЛОХО: некуда поставить бомбардира");
                return 1;
            }

            var before = scene.Vitals.Health;

            for (var time = 0f; time < 20f && !bomber.Dead; time += Step) Tick(scene, specials, null, PlayerCommands.Empty);

            text.AppendLine($"  дошёл и лопнул: {(bomber.Dead ? "да" : "НЕТ")}, здоровье {before:0} → {scene.Vitals.Health:0}");
            CheckScene.Expect(bomber.Dead && scene.Vitals.Health < before, "бомбардир доходит и лопается рядом с игроком", text, ref failed);

            // Убит на подходе — облако косит пауков вокруг него.
            Fresh(scene, specials);
            bomber = (BomberSpider)Place(scene, specials, SpecialKind.Bomber, 12, 16, 0.7f, 1.01f);

            var around = 0;
            for (var i = 0; i < 12; i++)
            {
                var offset = Quaternion.Euler(0f, i * 30f, 0f) * Vector3.forward * (1.2f + 0.15f * i);
                if (scene.Crowd.SpawnAt(bomber.Centre + offset, 1, 1f) >= 0) around++;
            }

            scene.Crowd.StepStatic(0f);
            bomber.TakeHit(9999f, HitKind.Bullet, DamageSource.Revolver, scene.Player.BodyCentre);
            scene.Crowd.FlushKills();

            text.AppendLine($"  убит выстрелом: пауков вокруг {around}, облако убило {bomber.CloudKills}, " +
                            $"в журнале револьвера {scene.Crowd.KillsBy(DamageSource.Revolver)}");
            CheckScene.Expect(bomber.CloudKills >= around / 2, "облако убитого бомбардира косит пауков вокруг", text, ref failed);

            return failed;
        }

        // ------------------------------------------------------------------ элита

        /// <summary>
        /// Аффиксы элиты (M3.3), каждый по отдельности: панцирь режет удар в лоб и не режет
        /// в спину, яд ранит стоящего рядом, кладка плодит мелких, прыгучая бросается к игроку,
        /// вожак ускоряет толпу вокруг.
        /// </summary>
        private static int Affixes(CheckScene scene, CaveSpecials specials, StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Элита с аффиксами ===");

            var crowd = scene.Crowd;
            var affixes = scene.Director.Affixes;

            for (var a = 0; a < (int)EliteAffix.Count; a++)
            {
                var affix = (EliteAffix)a;

                Fresh(scene, specials);
                affixes.Clear();

                var handle = crowd.SpawnElite(1.45f, 12f, 1.1f, Vector3.one);
                if (!handle.IsValid || !crowd.TryGetSpider(handle, out var position, out var health))
                {
                    text.AppendLine($"  ПЛОХО: {affix} — элита не завелась");
                    failed++;
                    continue;
                }

                affixes.Attach(crowd, handle, new[] { affix });

                switch (affix)
                {
                    case EliteAffix.Armored:
                    {
                        crowd.TryGetFacing(handle, out var front);
                        var centre = position + Vector3.up * 0.8f;

                        var before = health;
                        crowd.Hit(HitSpec.Ray(centre + front * 4f, -front, 4f, 0.5f, 100f, DamageSource.Debug));
                        crowd.TryGetSpider(handle, out _, out var afterFront);

                        crowd.Hit(HitSpec.Ray(centre - front * 4f, front, 4f, 0.5f, 100f, DamageSource.Debug));
                        crowd.TryGetSpider(handle, out _, out var afterBack);

                        var frontDealt = before - afterFront;
                        var backDealt = afterFront - afterBack;

                        text.AppendLine($"  панцирная: удар 100 HP в лоб снял {frontDealt:0}, в спину {backDealt:0}");
                        CheckScene.Expect(frontDealt > 30f && frontDealt < 50f && backDealt > 90f, "панцирь держит удар спереди и не держит сзади", text, ref failed);
                        break;
                    }

                    case EliteAffix.Poison:
                    {
                        var start = scene.Vitals.Health;
                        for (var t = 0f; t < 2f; t += Step)
                        {
                            scene.PlaceBody(position);
                            affixes.Tick(crowd, scene.Player, Step);
                            scene.Vitals.Tick(Step);
                        }

                        text.AppendLine($"  ядовитая: стоя рядом 2 с — здоровье {start:0} → {scene.Vitals.Health:0}");
                        CheckScene.Expect(scene.Vitals.Health < start - 5f, "яд ранит стоящего рядом", text, ref failed);
                        break;
                    }

                    case EliteAffix.Brood:
                    {
                        var alive = crowd.Alive;
                        for (var t = 0f; t < 6.5f; t += Step) affixes.Tick(crowd, scene.Player, Step);

                        text.AppendLine($"  кладка: за 6.5 с живых {alive} → {crowd.Alive}");
                        CheckScene.Expect(crowd.Alive >= alive + 3, "кладка плодит мелких", text, ref failed);
                        break;
                    }

                    case EliteAffix.Leaper:
                    {
                        // Игрок — в 8 юнитах по полу от элиты.
                        var points = new System.Collections.Generic.List<Vector3>();
                        crowd.PickFloorPoints(position + Vector3.up, 1, 7, 9, null, points);
                        if (points.Count > 0) scene.PlaceBody(points[0]);

                        for (var t = 0f; t < 5.5f; t += Step) affixes.Tick(crowd, scene.Player, Step);

                        text.AppendLine($"  прыгучая: рывков за 5.5 с {affixes.Fired[(int)affix]}");
                        CheckScene.Expect(affixes.Fired[(int)affix] > 0, "прыгучая бросается к игроку", text, ref failed);
                        break;
                    }

                    case EliteAffix.Leader:
                    {
                        crowd.SpawnAround(position, 12, 4, 1);
                        affixes.Tick(crowd, scene.Player, Step);

                        text.AppendLine($"  вожак: кадров с ускорением толпы {affixes.Fired[(int)affix]}");
                        CheckScene.Expect(affixes.Fired[(int)affix] > 0, "вожак ускоряет толпу рядом", text, ref failed);
                        break;
                    }
                }
            }

            affixes.Clear();
            return failed;
        }

        // ------------------------------------------------------------------ Матки ярусов

        /// <summary>
        /// Матки ярусов 2 и 3 (M3.5): разбудить, полминуты постоять рядом (здоровье подливается —
        /// мерится урон, а не смерть) и посчитать атаки профиля. Праматерь — ещё и провести через
        /// оба щита: в третьей фазе гаснет свет.
        /// </summary>
        private static int Queens(CheckScene scene, CaveSpecials specials, StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Матки ярусов 2 и 3 ===");

            var queen = scene.Queen;

            for (var tier = 2; tier <= 3; tier++)
            {
                Fresh(scene, specials);

                queen.SetTier(tier);
                queen.Rebuild();
                scene.Director.Tier = tier;

                var points = new System.Collections.Generic.List<Vector3>();
                scene.Crowd.PickFloorPoints(queen.LairPosition + Vector3.up, 1, 6, 8, null, points);
                var stand = points.Count > 0 ? points[0] : queen.LairPosition;

                // Будят вплотную (useRange), бьются — отойдя.
                for (var i = 0; i < 20; i++) Fight(scene, specials, queen.LairPosition);

                if (!scene.Director.TrySummonQueen(scene.Player.transform.position))
                {
                    text.AppendLine($"  ПЛОХО: Матка яруса {tier} не просыпается");
                    failed++;
                    continue;
                }

                var taken = scene.Vitals.Model.TotalTaken;

                for (var time = 0f; time < 30f; time += Step)
                {
                    Fight(scene, specials, stand);
                    if (scene.Vitals.Health < 50f) scene.Vitals.Heal(100f);
                }

                taken = scene.Vitals.Model.TotalTaken - taken;

                text.AppendLine($"  {Loc.T(queen.Profile.NameKey)} (×{queen.Profile.SizeScale:0.0}, живучесть {queen.MaxHealth:0} HP): " +
                                $"за 30 с луж {queen.PoolsLaid}, бомбардиров {queen.BombersHatched}, бросков {queen.Charges} " +
                                $"(попало {queen.ChargesLanded}), дождя {queen.RainDrops}; урон по игроку {taken:0} HP");

                CheckScene.Expect(taken > 0f, $"ярус {tier}: Матка ранит игрока", text, ref failed);
                CheckScene.Expect(queen.PoolsLaid > 0, $"ярус {tier}: лужи кислоты", text, ref failed);
                CheckScene.Expect(queen.BombersHatched > 0, $"ярус {tier}: выводок бомбардиров", text, ref failed);
                CheckScene.Expect(queen.Charges > 0, $"ярус {tier}: бросок через зал", text, ref failed);
                if (tier == 3) CheckScene.Expect(queen.RainDrops > 0, "ярус 3: дождь со свода", text, ref failed);

                if (tier != 3) continue;

                // Праматерь через оба щита: в третьей фазе свет гаснет.
                for (var phase = 0; phase < 2; phase++)
                {
                    for (var guard = 0; guard < 10000 && queen.State == CaveQueen.QueenState.Awake; guard++)
                    {
                        queen.DamageSphere(queen.Centre, 1f, CombatUnits.HitPoints);
                    }

                    foreach (var pylon in queen.PylonPositions)
                    {
                        for (var time = 0f; time < 6f && queen.State == CaveQueen.QueenState.Shielded; time += Step)
                        {
                            Fight(scene, specials, pylon);
                            if (scene.Vitals.Health < 50f) scene.Vitals.Heal(100f);
                        }
                    }
                }

                text.AppendLine($"  после двух щитов: Матка {queen.State}, {queen.HealthFraction:P0}, свет {(queen.LightsAreOut ? "погас" : "ГОРИТ")}");
                CheckScene.Expect(queen.LightsAreOut, "ярус 3: в третьей фазе гаснет свет", text, ref failed);
            }

            queen.SetTier(1);
            queen.Rebuild();
            scene.Director.Tier = 1;

            return failed;
        }

        /// <summary>Шаг боя у логова: директор, Матка, толпа, снаряды, особые, здоровье.</summary>
        private static void Fight(CheckScene scene, CaveSpecials specials, Vector3 feet)
        {
            scene.PlaceBody(feet);
            scene.Advance(Step);
            specials.Tick(Step);
        }

        private static int Fail(StringBuilder text, int failed)
        {
            Debug.LogError($"ПРОВЕРКА ОСОБЫХ НЕ ПРОШЛА, проблемных пунктов {failed}:{Environment.NewLine}{text}");
            return 1;
        }
    }
}
