using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Сохранения (M4.2): профиль → JSON → профиль без потерь и снимок забега, восстанавливающий
    /// забег в сцене.
    ///
    /// «Без потерь» проверяется не сравнением двух JSON-строк: строка после круга совпадёт с собой
    /// и тогда, когда JsonUtility молча выбросил поле (словарь, свойство, несериализуемый тип), —
    /// выброшенного нет ни в первой строке, ни во второй. Поэтому сравнивается исходный ОБЪЕКТ
    /// с восстановленным, поле за полем, рекурсивно. Профиль для этого заполнен небуквально: каждое
    /// поле отличается от умолчания, иначе потерянное поле восстановилось бы умолчанием и совпало.
    /// </summary>
    public static class SaveCheck
    {
        public static void Run()
        {
            var code = 0;

            try
            {
                code = Report();
            }
            catch (Exception error)
            {
                Debug.LogError("ПРОГОН СОХРАНЕНИЙ УПАЛ: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        public static int Report()
        {
            var text = new StringBuilder();
            var failed = 0;
            var clock = Stopwatch.StartNew();

            CheckRoundTrip(text, ref failed);
            CheckService(text, ref failed);
            CheckSnapshotInScene(text, ref failed);

            text.AppendLine();
            text.AppendLine($"прогон занял {clock.Elapsed.TotalSeconds:0.0} с");

            if (failed > 0)
            {
                Debug.LogError($"ПРОВЕРКА СОХРАНЕНИЙ НЕ ПРОШЛА, проблемных пунктов {failed}:{Environment.NewLine}{text}");
                return 1;
            }

            Debug.Log($"Проверка сохранений пройдена:{Environment.NewLine}{text}");
            return 0;
        }

        // ------------------------------------------------------------------ JSON

        private static void CheckRoundTrip(StringBuilder text, ref int failed)
        {
            text.AppendLine("=== Профиль → JSON → профиль ===");

            var profile = RichProfile();
            var json = JsonUtility.ToJson(profile);
            var back = JsonUtility.FromJson<PlayerProfile>(json);

            var diffs = new List<string>();
            var leaves = Compare(profile, back, "profile", diffs);

            text.AppendLine($"  JSON {json.Length} символов, сравнено значений {leaves}, расхождений {diffs.Count}");
            foreach (var diff in diffs.GetRange(0, Math.Min(10, diffs.Count))) text.AppendLine("    " + diff);

            CheckScene.Expect(leaves >= 100, "сравнение дошло до вложенных списков и массивов (значений ≥ 100)", text, ref failed);
            CheckScene.Expect(diffs.Count == 0, "профиль восстановился без потерь", text, ref failed);

            // Контроль самого контроля: сравнение обязано видеть изменение в глубине.
            back.Stats.KillsByWeapon[3]++;
            back.Snapshot.Tomes[1] = new IntPair(back.Snapshot.Tomes[1].Key, back.Snapshot.Tomes[1].Value + 1);

            var control = new List<string>();
            Compare(profile, back, "profile", control);

            text.AppendLine($"  контроль: две правки в глубине — найдено {control.Count}");
            CheckScene.Expect(control.Count == 2, "сравнение замечает правку во вложенном массиве и в списке структур", text, ref failed);

            // Второй круг — строка обязана совпасть: иначе формат плывёт от сохранения к сохранению.
            var again = JsonUtility.ToJson(JsonUtility.FromJson<PlayerProfile>(json));
            CheckScene.Expect(again == json, "второй круг даёт ту же строку", text, ref failed);
        }

        // ------------------------------------------------------------------ служба

        private static void CheckService(StringBuilder text, ref int failed)
        {
            text.AppendLine();
            text.AppendLine("=== Служба сохранений: загрузка, слияние, битые данные ===");

            var rich = RichProfile();
            var json = JsonUtility.ToJson(rich);

            // Загрузка через службу — с миграцией и дозаполнением умолчаний — не должна менять
            // ничего в профиле нынешней версии.
            var local = new MemorySaveBackend("local") { Json = json };
            var service = new SaveService(local);
            var loaded = service.Load(0);

            var diffs = new List<string>();
            Compare(rich, loaded, "loaded", diffs);
            text.AppendLine($"  загрузка из локального: откуда {service.LoadedFrom}, расхождений с исходным {diffs.Count}");
            foreach (var diff in diffs.GetRange(0, Math.Min(10, diffs.Count))) text.AppendLine("    " + diff);
            CheckScene.Expect(service.LoadedFrom == "local" && diffs.Count == 0, "загрузка с миграцией ничего не меняет", text, ref failed);

            // Сохранение растит счётчик и пишет и туда, и туда.
            var cloud = new MemorySaveBackend("cloud", true);
            service = new SaveService(local, cloud);
            service.Load(0);
            var counter = service.Profile.SaveCounter;
            service.Flush();

            var written = JsonUtility.FromJson<PlayerProfile>(cloud.Json);
            CheckScene.Expect(written != null && written.SaveCounter == counter + 1 && local.Json == cloud.Json,
                "сохранение растит счётчик и пишет в оба хранилища одно и то же", text, ref failed);

            // Облако новее — побеждает облако; локальное новее — локальное.
            var older = RichProfile();
            older.SaveCounter = 3;
            older.Chitin = 1f;
            var newer = RichProfile();
            newer.SaveCounter = 9;
            newer.Chitin = 999f;

            service = new SaveService(new MemorySaveBackend("local") { Json = JsonUtility.ToJson(older) },
                new MemorySaveBackend("cloud", true) { Json = JsonUtility.ToJson(newer) });
            service.Load(0);
            var cloudWins = service.LoadedFrom == "cloud" && Mathf.Approximately(service.Profile.Chitin, 999f);

            service = new SaveService(new MemorySaveBackend("local") { Json = JsonUtility.ToJson(newer) },
                new MemorySaveBackend("cloud", true) { Json = JsonUtility.ToJson(older) });
            service.Load(0);
            var localWins = service.LoadedFrom == "local" && Mathf.Approximately(service.Profile.Chitin, 999f);

            text.AppendLine($"  слияние по счётчику: облако новее — {(cloudWins ? "облако" : "НЕ облако")}, локальное новее — {(localWins ? "локальное" : "НЕ локальное")}");
            CheckScene.Expect(cloudWins && localWins, "при слиянии побеждает больший счётчик", text, ref failed);

            // Облако приходит позже (асинхронно): старое не перебивает, новое подменяет.
            service = new SaveService(new MemorySaveBackend("local") { Json = JsonUtility.ToJson(newer) });
            service.Load(0);
            var keptLocal = !service.MergeCloud(JsonUtility.ToJson(older));
            var tookCloud = service.MergeCloud(JsonUtility.ToJson(new PlayerProfile { SaveCounter = 50, Chitin = 5f }));
            CheckScene.Expect(keptLocal && tookCloud && Mathf.Approximately(service.Profile.Chitin, 5f),
                "запоздавшее облако: старое не перебивает, новое подменяет", text, ref failed);

            // Битые данные не роняют игру: берётся целое или новый профиль.
            service = new SaveService(new MemorySaveBackend("local") { Json = "{\"Version\":1,\"Chitin\":" },
                new MemorySaveBackend("cloud", true) { Json = JsonUtility.ToJson(newer) });
            service.Load(0);
            var brokenLocal = service.LoadedFrom == "cloud";

            service = new SaveService(new MemorySaveBackend("local") { Json = "мусор" });
            service.Load(0);
            var brokenBoth = service.LoadedFrom == "new" && service.Profile != null && service.Profile.IsWeaponUnlocked(WeaponId.Launcher);

            text.AppendLine($"  битое локальное при целом облаке: {(brokenLocal ? "облако" : "ПЛОХО")}; всё битое: {(brokenBoth ? "новый профиль" : "ПЛОХО")}");
            CheckScene.Expect(brokenLocal && brokenBoth, "битое сохранение не роняет загрузку", text, ref failed);

            // Профиль без версии (до M4) — открытия по умолчанию.
            service = new SaveService(new MemorySaveBackend("local") { Json = "{\"Chitin\":42.0}" });
            service.Load(0);
            var migrated = service.Profile.Version == PlayerProfile.CurrentVersion && Mathf.Approximately(service.Profile.Chitin, 42f) &&
                           service.Profile.IsClassUnlocked(PlayerClass.Ranger) && service.Profile.IsWeaponUnlocked(WeaponId.Revolver);
            CheckScene.Expect(migrated, "профиль без версии мигрирует и сохраняет хитин", text, ref failed);
        }

        // ------------------------------------------------------------------ снимок в сцене

        private static void CheckSnapshotInScene(StringBuilder text, ref int failed)
        {
            text.AppendLine();
            text.AppendLine("=== Снимок забега восстанавливает забег ===");

            var scene = CheckScene.Open(text, ref failed);
            if (scene == null) return;

            var controller = Object.FindAnyObjectByType<CaveRunController>();
            var arsenal = scene.Player.GetComponent<PlayerArsenal>();

            if (controller == null || arsenal == null)
            {
                text.AppendLine("  ПЛОХО: нет контроллера забега или арсенала");
                failed++;
                return;
            }

            controller.EnsureLinks();

            // Забег «на середине»: третий слот, эволюция, тома, остаток перебросов, изгнанное.
            var session = new RunSession { Level = 9, Xp = 17.5f, Chitin = 140f, Kills = 1234, EliteKills = 3, SpecialKills = 7, QueensKilled = 1, Seconds = 410f, Tier = 2, Depth = 3 };
            session.Weapons.Add(new OwnedWeapon { Id = WeaponId.Launcher, Level = 5, Evolved = true });
            session.Weapons.Add(new OwnedWeapon { Id = WeaponId.Shotgun, Level = 3 });
            session.Weapons.Add(new OwnedWeapon { Id = WeaponId.Tesla, Level = 2 });
            session.Tomes.Add(new OwnedTome { Id = TomeId.Might, Level = 3 });
            session.Tomes.Add(new OwnedTome { Id = TomeId.Haste, Level = 2 });
            session.Tomes.Add(new OwnedTome { Id = TomeId.Vitality, Level = 1 });
            session.Rerolls = 1;
            session.Banishes = 1;
            session.BanishedTomes.Add(TomeId.Magnet);
            session.KillsByWeapon[(int)WeaponId.Shotgun] = 400;

            controller.StartRun(Clone(session));
            var expected = StatsOf(controller.Stats);

            // Через профиль и JSON — так, как он переживает перезагрузку страницы.
            var profile = PlayerProfile.CreateNew(0);
            profile.Snapshot = RunSnapshot.From(session, (int)PlayerClass.Stormer, 31337, 0.55f);
            profile.HasSnapshot = true;

            var back = JsonUtility.FromJson<PlayerProfile>(JsonUtility.ToJson(profile));
            controller.StartRun(back.Snapshot.ToSession());
            scene.Vitals.SetHealthShare(back.Snapshot.HealthShare);

            var restored = controller.Session;
            var actual = StatsOf(controller.Stats);

            var weaponsOk = true;
            foreach (var owned in session.Weapons)
            {
                var weapon = arsenal.Get(owned.Id);
                var ok = weapon != null && weapon.Level == owned.Level && weapon.Evolved == owned.Evolved;
                weaponsOk &= ok;
                text.AppendLine($"  {owned.Id}: в руках {(weapon != null ? $"ур. {weapon.Level}{(weapon.Evolved ? ", эволюция" : "")}" : "НЕТ")}, в снимке ур. {owned.Level}{(owned.Evolved ? ", эволюция" : "")}");
            }

            var statDiffs = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                if (!Mathf.Approximately(expected[i], actual[i])) statDiffs++;
            }

            text.AppendLine($"  уровень {restored.Level}, опыт {restored.Xp}, ярус {restored.Tier}, глубина {restored.Depth}, убито {restored.Kills}, " +
                            $"томов {restored.Tomes.Count}, перебросов {restored.Rerolls}, изгнаний {restored.Banishes}, изгнано томов {restored.BanishedTomes.Count}");
            text.AppendLine($"  статы (класс не задан — только тома): расходятся {statDiffs} из {expected.Length}; урон ×{controller.Stats.DamageScale:0.00}");
            text.AppendLine($"  здоровье {scene.Vitals.HealthShare:P0} (в снимке {back.Snapshot.HealthShare:P0}), класс {(PlayerClass)back.Snapshot.ClassId}, сид {back.Snapshot.Seed}");

            CheckScene.Expect(weaponsOk && arsenal.Count == session.Weapons.Count, "пушки восстановлены с уровнями и эволюцией", text, ref failed);
            CheckScene.Expect(restored.Level == 9 && Mathf.Approximately(restored.Xp, 17.5f) && restored.Tier == 2 && restored.Depth == 3 &&
                              restored.Kills == 1234 && restored.KillsByWeapon[(int)WeaponId.Shotgun] == 400,
                "уровень, опыт, ярус, глубина и счёт убийств восстановлены", text, ref failed);
            CheckScene.Expect(restored.Tomes.Count == 3 && statDiffs == 0 && controller.Stats.DamageScale > 1f, "тома восстановлены и действуют на статы", text, ref failed);
            CheckScene.Expect(restored.Rerolls == 1 && restored.Banishes == 1 && restored.BanishedTomes.Contains(TomeId.Magnet),
                "остаток перебросов и изгнанное пережили перезагрузку", text, ref failed);
            CheckScene.Expect(Mathf.Abs(scene.Vitals.HealthShare - 0.55f) < 0.01f && back.Snapshot.ClassId == (int)PlayerClass.Stormer && back.Snapshot.Seed == 31337,
                "здоровье, класс и сид яруса в снимке", text, ref failed);

            // Сцену — к началу забега: прочие прогоны ждут чистый забег.
            controller.StartRun();
        }

        // ------------------------------------------------------------------ профиль

        /// <summary>Профиль, где каждое поле отличается от умолчания: потерянное поле не совпадёт случайно.</summary>
        private static PlayerProfile RichProfile()
        {
            var p = PlayerProfile.CreateNew(1700000000123L);

            p.Version = PlayerProfile.CurrentVersion;
            p.SaveCounter = 77;
            p.LastSeenDay = 640;
            p.Chitin = 1234.5f;
            p.ChitinEarnedTotal = 9876.25f;
            p.ChitinSpentOnUpgrades = 432.1f;
            p.UnlockedWeapons.Add((int)WeaponId.Tesla);
            p.UnlockedClasses.Add((int)PlayerClass.Miner);
            p.UnlockedDepth = 4;
            p.SetUpgradeLevel(MetaUpgrade.Might, 3);
            p.SetUpgradeLevel(MetaUpgrade.Revival, 1);
            p.SelectedClass = (int)PlayerClass.Miner;
            p.SelectedDepth = 3;

            var s = p.Stats;
            s.Runs = 41;
            s.Victories = 6;
            s.Kills = 123456789012L;
            for (var i = 0; i < s.KillsByWeapon.Length; i++) s.KillsByWeapon[i] = 1000 + i * 37;
            s.QueensByTier = new[] { 5, 3, 1 };
            s.EliteKills = 88;
            s.SpecialKills = 230;
            s.BestDepth = 4;
            s.BestTier = 3;
            s.RunsAtDepth.Add(new IntPair(1, 20));
            s.RunsAtDepth.Add(new IntPair(3, 7));
            s.ReachedTier3AtDepth.Add(new IntPair(1, 9));
            s.KillsByFire = 5555;
            s.BestFinalSwarmSeconds = 73.3f;

            p.Quests.Add(new QuestProgress { Id = "kills.launcher.1", Value = 200, Done = true, Claimed = true });
            p.Quests.Add(new QuestProgress { Id = "queens.total.1", Value = 2, Done = false, Claimed = false });
            p.Daily.Day = 640;
            p.Daily.Quests.Add(new QuestProgress { Id = "daily.specials", Value = 4 });
            p.StreakDays = 5;
            p.StreakLastDay = 640;
            p.WeaponSkins.Add(new IntPair((int)WeaponId.Launcher, 2));
            p.UnlockedSkins.Add(2);

            p.Settings.Sensitivity = 1.7f;
            p.Settings.InvertY = true;
            p.Settings.MasterVolume = 0.3f;
            p.Settings.MusicVolume = 0.2f;
            p.Settings.EffectsVolume = 0.4f;
            p.Settings.Quality = 1;
            p.Settings.Language = "tr";
            p.Settings.DamageNumbers = false;
            p.Settings.AutoFire = false;

            p.HintsShown.Add("hint.move");
            p.HintsShown.Add("hint.dash");

            p.Ads.RunsSinceInterstitial = 2;
            p.Ads.LastInterstitialAt = 1700000000999L;
            p.Ads.RewardedToday = 3;
            p.Ads.RewardedDay = 640;

            var session = new RunSession { Level = 8, Xp = 3.25f, Chitin = 77f, Kills = 999, EliteKills = 2, SpecialKills = 4, QueensKilled = 1, Seconds = 321.5f, Tier = 2, Depth = 3, Rerolls = 2, Skips = 1, Banishes = 1, PendingLevels = 1 };
            session.Weapons.Add(new OwnedWeapon { Id = WeaponId.Launcher, Level = 4, Evolved = true });
            session.Weapons.Add(new OwnedWeapon { Id = WeaponId.Flamer, Level = 2 });
            session.Tomes.Add(new OwnedTome { Id = TomeId.Might, Level = 2 });
            session.Tomes.Add(new OwnedTome { Id = TomeId.Haste, Level = 1 });
            session.BanishedWeapons.Add(WeaponId.Rail);
            session.BanishedTomes.Add(TomeId.Magnet);
            session.KillsByWeapon[(int)WeaponId.Flamer] = 321;

            p.HasSnapshot = true;
            p.Snapshot = RunSnapshot.From(session, (int)PlayerClass.Miner, 424242, 0.65f);

            return p;
        }

        private static RunSession Clone(RunSession session) =>
            RunSnapshot.From(session, 0, 0, 1f).ToSession();

        private static float[] StatsOf(PlayerStats stats)
        {
            var values = new float[(int)Stat.Count];
            for (var i = 0; i < values.Length; i++) values[i] = stats.Get((Stat)i);
            return values;
        }

        // ------------------------------------------------------------------ сравнение

        /// <summary>Рекурсивное сравнение по публичным полям. Возвращает число сравненных значений-листьев.</summary>
        private static int Compare(object a, object b, string path, List<string> diffs)
        {
            if (a == null || b == null)
            {
                if (!(a == null && b == null)) diffs.Add($"{path}: {(a == null ? "null" : "есть")} против {(b == null ? "null" : "есть")}");
                return 1;
            }

            var type = a.GetType();

            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal))
            {
                if (!a.Equals(b)) diffs.Add($"{path}: {a} против {b}");
                return 1;
            }

            if (a is IList listA && b is IList listB)
            {
                if (listA.Count != listB.Count)
                {
                    diffs.Add($"{path}: длина {listA.Count} против {listB.Count}");
                    return 1;
                }

                var leaves = 0;
                for (var i = 0; i < listA.Count; i++) leaves += Compare(listA[i], listB[i], $"{path}[{i}]", diffs);
                return leaves;
            }

            var total = 0;
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsNotSerialized) continue;
                total += Compare(field.GetValue(a), field.GetValue(b), path + "." + field.Name, diffs);
            }

            return total;
        }
    }
}
