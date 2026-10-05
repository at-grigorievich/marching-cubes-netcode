using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Симуляция прогресса (M6.1): вылазка из трёх ярусов ботом с настоящей пушкой, ростом, картами
    /// ярусов и кривой сложности — на нескольких сидах и трёх профилях игрока. Печатает время смерти,
    /// уровень по минутам, хитин, милосердие и сравнивает с окнами плана.
    ///
    /// Профили отличаются тем, чем отличаются живые игроки: точностью (промах — прицел уводится
    /// на 10–20° на четверть секунды), тем, когда игрок рвётся из окружения рывком, долей купленной меты
    /// и мягким стартом (новичок играет первые забеги). Бой с Маткой не симулируется: дожил до конца
    /// стадии — «дошёл до Матки», и вылазка продолжается на следующем ярусе с ростом, лечением
    /// привала (+30%) и новой картой. Это верхняя граница для слабых профилей: Матку они могли бы
    /// и не убить.
    ///
    /// Окна плана — стартовые, поэтому прогон их не валит, а отмечает «в окне / вне окна»: баланс
    /// настраивают по нему, а не прячут за порогом.
    ///
    /// Окна хитина выведены из главной цели экономики плана — вся мета за 10–15 часов: она стоит
    /// 47.4 тыс. хитина (17 улучшений с ростом цены и шесть пушек), это 40–60 вылазок по 15 минут,
    /// то есть 800–1200 хитина за вылазку у среднего. Плановые 150–300 этой же цели противоречили:
    /// при них мета проходилась бы 40–80 часов.
    /// </summary>
    public static class ProgressionSim
    {
        private static readonly int[] AllSeeds = { 1337, 2, 777, 55555, 4242 };

        /// <summary>Сколько сидов из пяти — прогон целиком идёт около получаса.</summary>
        public static int SeedCount = 5;

        /// <summary>Только этот профиль (имя) — для быстрых замеров после правки.</summary>
        public static string ProfileFilter;

        private sealed class SimProfile
        {
            public string Name;
            public float Accuracy;
            public int DashCrowd;
            public float MetaShare;
            public int RunsPlayed;

            /// <summary>Промах уводит прицел на столько градусов: у новичка — мимо толпы, а не по краю взрыва.</summary>
            public float MissMin = 10f;
            public float MissMax = 20f;

            public float Reposition = 0.5f;
            public bool RandomCards;
        }

        private static readonly SimProfile[] Profiles =
        {
            // Новичок — модель живого новичка, а не ослабленный бот: первая версия (промах 10–20°,
            // ноги и карточки как у сильного) проходила ярус 1 пять раз из пяти, потому что промах
            // на 15° у взрыва радиусом 5 — не промах, а идеальные ноги и выбор карточек — не новичок.
            new SimProfile { Name = "новичок", Accuracy = 0.4f, DashCrowd = 10, MetaShare = 0f, RunsPlayed = 0,
                MissMin = 25f, MissMax = 45f, Reposition = 1.2f, RandomCards = true },
            new SimProfile { Name = "средний", Accuracy = 0.7f, DashCrowd = 6, MetaShare = 0.3f, RunsPlayed = 10 },
            new SimProfile { Name = "сильный", Accuracy = 0.9f, DashCrowd = 5, MetaShare = 1f, RunsPlayed = 30 }
        };

        private sealed class TierResult
        {
            public int Tier;
            public float DiedAt = -1f;
            public float Stage;
            public int Level;
            public int Kills;
            public int Mercy;
            public int[] LevelAtMinute = new int[8];
        }

        private sealed class SortieResult
        {
            public readonly List<TierResult> Tiers = new List<TierResult>();
            public float Chitin;

            public bool Survived(int tier) => Tiers.Count >= tier && Tiers[tier - 1].DiedAt < 0f;

            /// <summary>Время вылазки до смерти или до конца третьего яруса.</summary>
            public float Seconds => Tiers.Sum(t => t.DiedAt >= 0f ? t.DiedAt : t.Stage);
        }

        public static void Run()
        {
            var code = 0;

            try
            {
                code = Report();
            }
            catch (Exception error)
            {
                Debug.LogError("СИМУЛЯЦИЯ ПРОГРЕССА УПАЛА: " + error);
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
            if (scene == null)
            {
                Debug.LogError("СИМУЛЯЦИЯ ПРОГРЕССА: сцена не собралась" + Environment.NewLine + text);
                return 1;
            }

            var controller = Object.FindAnyObjectByType<CaveRunController>();
            var ichor = Object.FindAnyObjectByType<IchorField>();
            var arsenal = scene.Player.GetComponent<PlayerArsenal>();

            controller.EnsureLinks();
            ichor.EnsureLinks();
            scene.Vitals.Bind();
            arsenal.Prepare();
            arsenal.Context.Audio.Muted = true;

            var baseSettings = scene.World.Settings;
            var original = scene.World.CurrentSeed;
            var copies = new List<CatacombSettings>();
            var seeds = AllSeeds.Take(Mathf.Clamp(SeedCount, 1, AllSeeds.Length)).ToArray();

            text.AppendLine();
            text.AppendLine($"=== Вылазка ботом: {seeds.Length} сидов × {Profiles.Length} профиля, ярусы 1–3, глубина 1 ===");

            var results = new Dictionary<string, List<SortieResult>>();

            try
            {
                foreach (var profile in Profiles)
                {
                    if (!string.IsNullOrEmpty(ProfileFilter) && profile.Name != ProfileFilter) continue;

                    var list = new List<SortieResult>();
                    results[profile.Name] = list;

                    foreach (var seed in seeds)
                    {
                        var sortie = Sortie(profile, seed, scene, controller, ichor, arsenal, baseSettings, copies);
                        list.Add(sortie);

                        var tiers = string.Join("; ", sortie.Tiers.Select(t =>
                            $"ярус {t.Tier}: {(t.DiedAt >= 0f ? "погиб на " + CheckScene.Clock(t.DiedAt) : "дошёл до Матки")}, ур. {t.Level}, " +
                            $"убито {t.Kills}{(t.Mercy > 0 ? $", милосердие {t.Mercy}" : "")}"));

                        text.AppendLine($"  {profile.Name,-8} сид {seed,5}: {tiers}; хитин {sortie.Chitin:0}");
                    }
                }
            }
            finally
            {
                scene.World.UseSettings(baseSettings);
                scene.World.GenerateImmediate(original);
                scene.Queen.Rebuild();
                scene.PlaceBody(scene.Entry);
                scene.Crowd.Rebuild();
                controller.BaseStats = null;
                controller.Difficulty = DifficultyScale.One;
                scene.Director.SetStageSeconds(420f);
                arsenal.ClearLoadout();

                foreach (var copy in copies) Object.DestroyImmediate(copy);
            }

            Summarize(results, text);

            text.AppendLine();
            text.AppendLine($"прогон занял {clock.Elapsed.TotalMinutes:0.0} мин");

            Debug.Log($"Симуляция прогресса:{Environment.NewLine}{text}");
            return 0;
        }

        // ------------------------------------------------------------------ сводка

        private static void Summarize(Dictionary<string, List<SortieResult>> results, StringBuilder text)
        {
            text.AppendLine();
            text.AppendLine("=== Против окон плана (M6.1) ===");

            foreach (var pair in results)
            {
                var list = pair.Value;
                if (list.Count == 0) continue;

                var tier1 = list.Select(s => s.Tiers[0]).ToList();
                var deaths1 = tier1.Where(t => t.DiedAt >= 0f).Select(t => t.DiedAt).OrderBy(t => t).ToList();
                var reach1 = list.Count(s => s.Survived(1));
                var reach3 = list.Count(s => s.Survived(3));
                var level5 = Median(tier1.Select(t => (float)t.LevelAtMinute[Mathf.Min(4, t.LevelAtMinute.Length - 1)]));
                var chitin = Median(list.Select(s => s.Chitin));
                var minutes = Median(list.Select(s => s.Seconds / 60f));

                text.AppendLine($"  {pair.Key}: ярус 1 пройден {reach1} из {list.Count}" +
                                (deaths1.Count > 0 ? $", смерть на ярусе 1 — медиана {CheckScene.Clock(Median(deaths1))} ({string.Join(", ", deaths1.Select(CheckScene.Clock))})" : "") +
                                $"; все три яруса — {reach3} из {list.Count}; уровень к 5:00 — {level5:0}; хитин за вылазку — {chitin:0}; вылазка — {minutes:0.0} мин");

                switch (pair.Key)
                {
                    case "новичок":
                        Window(deaths1.Count > list.Count / 2 && Median(deaths1) >= 180f && Median(deaths1) <= 270f, "новичок гибнет на ярусе 1 в 3:00–4:30", text);
                        Window(level5 >= 7 && level5 <= 9, "новичок к концу яруса 1 — уровень 7–9", text);
                        Window(chitin >= 100 && chitin <= 300, "новичок — 100–300 хитина (первое улучшение после первой смерти)", text);
                        break;

                    case "средний":
                        Window(reach1 * 2 > list.Count, "средний доходит до Ткачихи", text);
                        Window(level5 >= 9 && level5 <= 12, "средний к концу яруса 1 — уровень 9–12", text);
                        Window(list.Count(s => s.Survived(3)) * 2 >= list.Count, "средний: половина доходит до яруса 3", text);
                        Window(chitin >= 800 && chitin <= 1200, "средний — 800–1200 хитина (вся мета за 10–15 часов)", text);
                        break;

                    case "сильный":
                        Window(reach3 * 2 > list.Count, "сильный проходит вылазку", text);
                        Window(level5 >= 11 && level5 <= 13, "сильный к концу яруса 1 — уровень 11–13", text);
                        Window(minutes >= 18f && minutes <= 22f, "сильный — 18–22 минуты", text);
                        Window(chitin >= 1000, "сильный — 1000+ хитина", text);
                        break;
                }
            }
        }

        private static void Window(bool inside, string what, StringBuilder text) =>
            text.AppendLine($"    {(inside ? "в окне" : "ВНЕ ОКНА")}: {what}");

        private static float Median(IEnumerable<float> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count == 0 ? 0f : sorted[sorted.Count / 2];
        }

        // ------------------------------------------------------------------ вылазка

        private static SortieResult Sortie(SimProfile profile, int seed, CheckScene scene, CaveRunController controller,
            IchorField ichor, PlayerArsenal arsenal, CatacombSettings baseSettings, List<CatacombSettings> copies)
        {
            var result = new SortieResult();
            var random = new Random(seed * 31 + profile.Name.Length);

            // Мета — доля каждого улучшения от максимума; класс — Рейнджер с Барабаном, как у новичка.
            var meta = PlayerProfile.CreateNew(0);
            for (var i = 0; i < (int)MetaUpgrade.Count; i++)
            {
                var def = MetaUpgrades.Get((MetaUpgrade)i);
                meta.SetUpgradeLevel((MetaUpgrade)i, Mathf.RoundToInt(def.MaxLevel * profile.MetaShare));
            }

            controller.BaseStats = stats =>
            {
                PlayerClasses.Apply(PlayerClass.Ranger, stats);
                MetaUpgrades.Apply(meta, stats);
            };
            controller.StartWeapon = WeaponId.Launcher;

            for (var tier = 1; tier <= SortieDifficulty.MaxTier; tier++)
            {
                var recipe = LevelRecipe.For(tier, 1);
                var copy = Object.Instantiate(baseSettings);
                copy.hideFlags = HideFlags.DontSave;
                copy.ApplyRecipe(recipe);
                copies.Add(copy);

                scene.World.UseSettings(copy);

                // Генерацию слушает контроллер: без пометки «продолжение» новый уровень начинает новый
                // забег, и рост обнулялся бы на каждом ярусе — так первая версия и мерила.
                if (tier > 1)
                {
                    controller.Session.Tier = tier;
                    controller.ContinueOnNextLevel();
                }

                scene.World.GenerateImmediate(seed * 7 + tier);
                scene.Queen.Rebuild();
                scene.World.TryGetSpawnPoint(out var entry);
                scene.PlaceBody(entry);

                scene.Director.SetStageSeconds(recipe.StageSeconds);
                scene.Director.Restart();
                scene.Crowd.Rebuild();

                controller.Difficulty = SortieDifficulty.Scale(tier, 1, profile.RunsPlayed);

                if (tier == 1)
                {
                    controller.StartRun();
                    scene.Vitals.ResetForRun();
                }
                else
                {
                    // Привал: +30% здоровья и бесплатная карта (ContinueRun уже позвала генерация).
                    scene.Vitals.Heal(scene.Vitals.MaxHealth * 0.3f);

                    var rest = CardOffer.Make(controller.Session, controller.PoolSource?.Invoke() ?? CardPool.Everything(), random);
                    if (rest.Count > 0) controller.Apply(rest[CatacombRunCheck.PickCard(rest, arsenal.Active != null ? arsenal.Active.Id : WeaponId.Launcher)]);
                }

                ichor.Clear();
                controller.RecomposeStats();

                var outcome = PlayTier(profile, tier, recipe.StageSeconds, scene, controller, ichor, arsenal, random);
                result.Tiers.Add(outcome);

                if (outcome.DiedAt >= 0f) break;
            }

            result.Chitin = controller.Session.Chitin;
            return result;
        }

        private static TierResult PlayTier(SimProfile profile, int tier, float stage, CheckScene scene, CaveRunController controller,
            IchorField ichor, PlayerArsenal arsenal, Random random)
        {
            var run = new TierResult { Tier = tier, Stage = stage };
            var director = scene.Director;
            var crowd = scene.Crowd;
            var vitals = scene.Vitals;
            var player = scene.Player;
            var step = CatacombRunCheck.GunStep;

            scene.World.TryGetSpawnPoint(out var entry);
            var legs = new CatacombRunCheck.BotLegs(entry, player.GetComponent<PlayerMobility>())
            {
                DashCrowd = profile.DashCrowd,
                Reposition = profile.Reposition
            };
            var near = new List<int>();
            var visible = new List<Vector3>();
            var killsBefore = arsenal.TotalKills;
            var minute = 0;
            var wasFiring = false;
            var holding = 0f;
            var missLeft = 0f;
            var miss = Quaternion.identity;

            for (var time = 0f; time < stage; time += step)
            {
                legs.Step(crowd, vitals, controller.Stats.MoveScale, step);
                scene.PlaceBody(legs.Feet);

                director.Tick(step);
                crowd.Simulate(step);
                crowd.FlushKills();
                ichor.Tick(step);
                vitals.Tick(step);
                player.TickStatus(step);
                controller.Tick(step, step);

                var active = arsenal.Active != null ? arsenal.Active.Id : WeaponId.Launcher;

                if (controller.Offering)
                {
                    controller.Choose(profile.RandomCards ? random.Next(controller.Cards.Count) : CatacombRunCheck.PickCard(controller.Cards, active));
                }

                if (vitals.IsDead)
                {
                    run.DiedAt = director.Elapsed;
                    break;
                }

                // Точность: раз в четверть секунды жребий — промах уводит прицел на 10–20° в сторону.
                missLeft -= step;
                if (missLeft <= 0f)
                {
                    missLeft = 0.25f;
                    miss = random.NextDouble() > profile.Accuracy
                        ? Quaternion.AngleAxis(Mathf.Lerp(profile.MissMin, profile.MissMax, (float)random.NextDouble()), UnityEngine.Random.onUnitSphere)
                        : Quaternion.identity;
                }

                var commands = PlayerCommands.Empty;
                var eye = player.transform.position;

                visible.Clear();

                if (crowd.FindNearest(eye, CatacombRunCheck.EngageRange(active), 24, near) > 0)
                {
                    foreach (var index in near)
                    {
                        var position = crowd.PositionAt(index);
                        if (!Physics.Linecast(eye, position, ~0, QueryTriggerInteraction.Ignore)) visible.Add(position);
                        if (visible.Count >= 12) break;
                    }
                }

                if (visible.Count > 0)
                {
                    var target = CatacombRunCheck.BestAim(active, eye, visible);
                    player.transform.rotation = miss * Quaternion.LookRotation((target - eye).normalized, Vector3.up);

                    switch (active)
                    {
                        case WeaponId.Rail:
                            holding += step;
                            commands.FireHeld = holding < 1f;
                            commands.FirePressed = holding <= step * 1.5f;
                            if (holding >= 1f + step) holding = 0f;
                            break;

                        case WeaponId.PickHammer:
                            holding += step;
                            commands.FirePressed = ((int)(holding / step) & 1) == 0;
                            commands.FireHeld = commands.FirePressed;
                            break;

                        default:
                            commands.FireHeld = true;
                            commands.FirePressed = !wasFiring;
                            break;
                    }
                }
                else
                {
                    holding = 0f;
                    commands.Reload = true;
                }

                wasFiring = commands.FireHeld;

                arsenal.Tick(commands, step, true);
                arsenal.Context.Projectiles.Tick(step);

                while (minute < run.LevelAtMinute.Length && director.Elapsed >= (minute + 1) * 60f)
                {
                    run.LevelAtMinute[minute] = controller.Session.Level;
                    minute++;
                }
            }

            crowd.FlushKills();
            for (; minute < run.LevelAtMinute.Length; minute++) run.LevelAtMinute[minute] = controller.Session.Level;

            run.Level = controller.Session.Level;
            run.Kills = arsenal.TotalKills - killsBefore;
            run.Mercy = controller.MercyTriggers;
            return run;
        }
    }
}
