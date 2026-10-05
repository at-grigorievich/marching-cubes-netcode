using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Прогон интерфейса забега (M1.9–M1.10).
    ///
    /// Вопросы, на которые глазами не ответить или ответить дорого:
    /// 1. Есть ли перевод у каждого ключа на каждом языке и совпадают ли подстановки {0}, {1}.
    ///    Строка без «{1}» на турецком молча съест число, и заметят это только турки.
    /// 2. Есть ли в таблице каждый ключ, который зовёт код: и литералы, и собранные из пушек
    ///    и томов. Отсутствующий ключ рисуется самим ключом — «weapon.rail.evo» на карточке.
    /// 3. Есть ли в шрифте каждая буква таблицы: квадратик вместо «ğ» глазами ловится только
    ///    на турецком, а его никто из нас не читает.
    /// 4. Собираются ли все экраны, и не остаётся ли на открытом экране ключа или «{0}».
    /// 5. Ноль аллокаций в кадре HUD: на WebGL сборщик мусора идёт на главном потоке, и HUD,
    ///    сорящий строками каждый кадр, даёт рывки раз в несколько секунд.
    /// </summary>
    public static class UiCheck
    {
        private const float Step = 1f / 30f;
        private const string CodeRoot = "Assets/Scripts/Mine Generator";

        private static readonly Regex Placeholder = new Regex(@"\{(\d+)[^}]*\}", RegexOptions.Compiled);
        private static readonly Regex RawKey = new Regex(@"^[a-z]+(\.[a-z0-9_]+)+$", RegexOptions.Compiled);

        public static void Run()
        {
            var code = 0;

            try
            {
                code = Report();
            }
            catch (Exception error)
            {
                Debug.LogError("ПРОГОН ИНТЕРФЕЙСА УПАЛ: " + error);
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

            Loc.Reload();

            failed += CheckTable(text);
            failed += CheckCodeKeys(text);
            failed += CheckGlyphs(text);

            var scene = CheckScene.Open(text, ref failed);
            if (scene == null) return Fail(text, failed + 1);

            var ui = BuildUi(scene, text, ref failed);

            if (ui != null)
            {
                try
                {
                    failed += CheckScreens(ui, text);
                    failed += CheckAllocations(ui, scene, text);
                    failed += CheckTouch(ui, scene, text);
                }
                finally
                {
                    Object.DestroyImmediate(ui.gameObject);

                    var events = Object.FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>();
                    if (events != null && (events.gameObject.hideFlags & HideFlags.DontSave) != 0) Object.DestroyImmediate(events.gameObject);
                    Loc.SetLanguage("ru");
                }
            }

            text.AppendLine();
            text.AppendLine($"прогон занял {clock.Elapsed.TotalSeconds:0.0} с");

            if (failed > 0) return Fail(text, failed);

            Debug.Log($"Проверка интерфейса пройдена:{Environment.NewLine}{text}");
            return 0;
        }

        // ------------------------------------------------------------------ таблица

        private static int CheckTable(StringBuilder text)
        {
            var failed = 0;

            text.AppendLine("=== Таблица локализации ===");

            var keys = Loc.Keys.ToList();
            text.AppendLine($"  ключей {keys.Count}, языков {Loc.Languages.Length} ({string.Join(", ", Loc.Languages)})");

            Expect(keys.Count > 0, "таблица загружена из Resources/" + Loc.TablePath, text, ref failed);

            var incomplete = new List<string>();
            var mismatched = new List<string>();

            foreach (var key in keys)
            {
                if (!Loc.IsComplete(key, out var missing)) incomplete.Add(key + " (нет: " + missing.Trim() + ")");

                var sets = Loc.Languages.Select(language => PlaceholdersOf(Loc.Raw(key, language))).ToList();
                if (sets.Any(set => !set.SetEquals(sets[0]))) mismatched.Add(key);
            }

            Report(incomplete, "без перевода", text);
            Report(mismatched, "с разными подстановками {n} по языкам", text);

            Expect(incomplete.Count == 0, "у каждого ключа перевод на каждом языке", text, ref failed);
            Expect(mismatched.Count == 0, "подстановки {n} совпадают во всех языках", text, ref failed);

            return failed;
        }

        private static HashSet<int> PlaceholdersOf(string value)
        {
            var set = new HashSet<int>();
            if (string.IsNullOrEmpty(value)) return set;

            foreach (Match match in Placeholder.Matches(value)) set.Add(int.Parse(match.Groups[1].Value));

            return set;
        }

        // ------------------------------------------------------------------ ключи из кода

        /// <summary>
        /// Литералы ключей в коде — по префиксам, которые есть в таблице (комментарии пропускаются:
        /// в них ключи бывают примером). Плюс собранные: имена, описания, уровни, альт-огонь
        /// и эволюции пушек, имена и описания томов.
        /// </summary>
        private static int CheckCodeKeys(StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Ключи, которые зовёт код ===");

            var prefixes = new HashSet<string>(Loc.Keys.Select(key => key.Split('.')[0]));
            // Ключ кончается кавычкой или двоеточием: объявления директора едут «ключ:подстановка».
            var literal = new Regex("\"((" + string.Join("|", prefixes.Select(Regex.Escape)) + @")(\.[a-z0-9_]+)+)["":]");

            var used = new Dictionary<string, string>();

            foreach (var path in Directory.GetFiles(CodeRoot, "*.cs", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(path);

                for (var i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*")) continue;

                    foreach (Match match in literal.Matches(lines[i]))
                    {
                        var key = match.Groups[1].Value;

                        // «loading.tip" + номер» — начало собираемого ключа, а не ключ: семейство
                        // заводится ниже целиком.
                        if (lines[i].Substring(match.Index + match.Length).TrimStart().StartsWith("+")) continue;

                        // Номера заданий (M4.5) похожи на ключи и делят с ними префиксы: «class.miner» —
                        // задание, открывающее класс, а не подпись.
                        if (Quests.Find(key) != null) continue;

                        if (!used.ContainsKey(key)) used[key] = Path.GetFileName(path) + ":" + (i + 1);
                    }
                }
            }

            var literals = used.Count;

            for (var id = 0; id < (int)WeaponId.Count; id++)
            {
                var def = WeaponCatalog.Get((WeaponId)id);
                if (def == null) continue;

                var where = "пушка " + (WeaponId)id;

                used[def.NameKey] = where;
                used[def.DescKey] = where;
                used[def.AltKey] = where;
                used[def.EvolutionKey] = where;
                used[def.EvolutionKey + ".desc"] = where;

                for (var level = 2; level <= 5; level++) used[def.LevelTextKey(level)] = where + ", уровень " + level;
            }

            for (var id = 0; id < (int)TomeId.Count; id++)
            {
                var tome = Tomes.Get((TomeId)id);
                used[tome.NameKey] = "том " + (TomeId)id;
                used[tome.DescKey] = "том " + (TomeId)id;
            }

            // Объявления аффиксов элиты собираются из имени аффикса (M3.3).
            for (var id = 0; id < (int)EliteAffix.Count; id++)
            {
                used["banner.elite." + ((EliteAffix)id).ToString().ToLowerInvariant()] = "аффикс " + (EliteAffix)id;
            }

            // Мета-игра (M4): улучшения, классы, тексты заданий по виду, вкладки лагеря, советы загрузки.
            for (var id = 0; id < (int)MetaUpgrade.Count; id++)
            {
                var def = MetaUpgrades.Get((MetaUpgrade)id);
                used[def.NameKey] = "улучшение " + (MetaUpgrade)id;
                used[def.DescKey] = "улучшение " + (MetaUpgrade)id;
            }

            for (var id = 0; id < (int)PlayerClass.Count; id++)
            {
                used[PlayerClasses.NameKey((PlayerClass)id)] = "класс " + (PlayerClass)id;
                used[PlayerClasses.DescKey((PlayerClass)id)] = "класс " + (PlayerClass)id;
            }

            foreach (var quest in Quests.Permanent) used[quest.TextKey] = "задание " + quest.Id;
            foreach (var quest in Quests.DailyFor(0)) used[quest.TextKey] = "ежедневное " + quest.Kind;

            var tabs = typeof(HubScreen).GetNestedType("Tab", BindingFlags.NonPublic);
            foreach (var tab in Enum.GetNames(tabs).Where(name => name != "Count")) used["hub.tab." + tab.ToLowerInvariant()] = "вкладка лагеря";

            var tips = (int)typeof(LoadingView).GetField("Tips", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            for (var i = 0; i < tips; i++) used["loading.tip" + i] = "совет загрузки " + i;

            var missing = used.Where(pair => !Loc.Has(pair.Key)).Select(pair => pair.Key + " (" + pair.Value + ")").ToList();
            var unused = Loc.Keys.Where(key => !used.ContainsKey(key)).ToList();

            text.AppendLine($"  литералов в коде {literals}, всего с пушками и томами {used.Count}");
            Report(missing, "нет в таблице", text);

            // Неиспользуемые — не ошибка: ключи для M4 и подписи, собираемые иначе, заводятся заранее.
            text.AppendLine($"  в таблице, но не найдены в коде: {unused.Count}" +
                            (unused.Count > 0 ? " — " + string.Join(", ", unused.Take(12)) + (unused.Count > 12 ? "…" : "") : ""));

            Expect(missing.Count == 0, "каждый ключ из кода есть в таблице", text, ref failed);

            return failed;
        }

        // ------------------------------------------------------------------ шрифт

        /// <summary>
        /// Буквы таблицы против исходного файла шрифта, а не против атласа: атлас динамический
        /// и до первого показа пуст, а спрашивать его с добавлением — значит менять ассет прогоном.
        /// </summary>
        private static int CheckGlyphs(StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Буквы в шрифте ===");

            foreach (var font in new[] { UiKit.Regular, UiKit.Bold })
            {
                if (font == null || font.sourceFontFile == null)
                {
                    text.AppendLine("  ПЛОХО: нет шрифта или его исходного файла (Resources/Fonts)");
                    failed++;
                    continue;
                }

                FontEngine.InitializeFontEngine();

                if (FontEngine.LoadFontFace(font.sourceFontFile, 90) != FontEngineError.Success)
                {
                    text.AppendLine($"  ПЛОХО: не открылся файл шрифта {font.sourceFontFile.name}");
                    failed++;
                    continue;
                }

                foreach (var language in Loc.Languages)
                {
                    var letters = new SortedSet<char>();

                    foreach (var key in Loc.Keys)
                    {
                        var value = Loc.Raw(key, language);
                        if (value == null) continue;

                        foreach (var letter in value)
                        {
                            if (!char.IsWhiteSpace(letter) && !char.IsControl(letter)) letters.Add(letter);
                        }
                    }

                    var absent = letters.Where(letter => !FontEngine.TryGetGlyphIndex(letter, out var index) || index == 0).ToList();

                    text.AppendLine($"  {font.name}, {language}: букв {letters.Count}, нет в шрифте {absent.Count}" +
                                    (absent.Count > 0 ? " — " + string.Join(" ", absent.Select(c => $"«{c}» U+{(int)c:X4}")) : ""));

                    if (absent.Count > 0) failed++;
                }
            }

            Expect(failed == 0, "в шрифте есть каждая буква таблицы на каждом языке", text, ref failed);

            return failed;
        }

        // ------------------------------------------------------------------ экраны

        // ------------------------------------------------------------------ тач

        /// <summary>
        /// Помощь прицеливания (M5.5): паук в 3° от перекрестия — взгляд тянется к нему, а не прочь
        /// (ошибку знака глазами на кадре не увидеть), «липкость» снижает чувствительность, автоогонь —
        /// у револьвера да, у гранатомёта нет. И джойстик синтетическими касаниями — ход вперёд.
        /// </summary>
        private static int CheckTouch(GameUi ui, CheckScene scene, StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Тач ===");

            var rig = scene.Player;
            var crowd = scene.Crowd;
            var arsenal = rig.GetComponent<PlayerArsenal>();
            var touch = ui.Touch;

            ui.EnableTouch();
            touch.Injected = new List<TouchPoint>();

            // Экраны прошлого раздела (карточки, смерть) оставляют ввод заблокированным.
            rig.InputBlocked = false;

            try
            {
                arsenal.Prepare();
                arsenal.ClearLoadout();
                arsenal.Give(WeaponId.Launcher);

                crowd.ClearAll();

                // Паука — в 5–20 юнитах на открытой линии; направление перебором, у входа стены близко.
                var eye = rig.transform.position;
                var slot = -1;
                var direction = Vector3.forward;

                for (var k = 0; k < 16 && slot < 0; k++)
                {
                    var dir = Quaternion.Euler(0f, k * 22.5f, 0f) * Vector3.forward;
                    var candidate = crowd.SpawnAt(eye + dir * 10f, 0, 1000f);
                    if (candidate < 0) continue;

                    crowd.StepStatic(0f);
                    var delta = crowd.PositionAt(candidate) - eye;

                    if (delta.magnitude > 5f && delta.magnitude < 20f &&
                        !Physics.Raycast(eye, delta.normalized, delta.magnitude - 1f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        slot = candidate;
                        direction = delta.normalized;
                    }
                    else
                    {
                        crowd.ClearAll();
                    }
                }

                if (slot < 0)
                {
                    text.AppendLine("  ПЛОХО: у входа не нашлось открытой линии на паука");
                    return failed + 1;
                }

                // Перекрестие — на 3° правее паука: тянуть должно влево.
                rig.transform.rotation = Quaternion.LookRotation(Quaternion.Euler(0f, 3f, 0f) * direction, Vector3.up);

                var commands = PlayerCommands.Empty;
                touch.Step(ref commands, 1f / 30f, 0f);

                text.AppendLine($"  цель {(touch.HasTarget ? "найдена" : "НЕ найдена")} в {touch.TargetAngle:0.0}°, подтяжка взгляда {commands.Look.x:+0.000;-0.000} (влево — минус), " +
                                $"чувствительность {touch.Router.Sensitivity:0.0}, автоогонь у гранатомёта {(touch.AutoFiring ? "ДА" : "нет")}");

                Expect(touch.HasTarget && Mathf.Abs(touch.TargetAngle - 3f) < 1.5f, "паук в конусе 6° найден", text, ref failed);
                Expect(commands.Look.x < 0f, "взгляд тянется к пауку, а не прочь", text, ref failed);
                Expect(Mathf.Approximately(touch.Router.Sensitivity, 1f), "в 3° — ещё не «липко» (порог 2.5°)", text, ref failed);
                Expect(!touch.AutoFiring, "гранатомёт сам не стреляет", text, ref failed);

                arsenal.ClearLoadout();
                arsenal.Give(WeaponId.Revolver);
                rig.transform.rotation = Quaternion.LookRotation(Quaternion.Euler(0f, 1f, 0f) * direction, Vector3.up);

                commands = PlayerCommands.Empty;
                touch.Step(ref commands, 1f / 30f, 0.1f);

                text.AppendLine($"  револьвер в 1° от паука: автоогонь {(touch.AutoFiring ? "да" : "НЕТ")}, огонь {(commands.FireHeld ? "держится" : "НЕТ")}, " +
                                $"чувствительность {touch.Router.Sensitivity:0.0}");

                Expect(touch.AutoFiring && commands.FireHeld && commands.FirePressed, "револьвер стреляет сам, пока перекрестие на цели", text, ref failed);
                Expect(Mathf.Approximately(touch.Router.Sensitivity, TouchControls.StickyScale), "над целью — «липкость» ×0.6", text, ref failed);

                // Взгляд в сторону — цели нет, огня нет.
                rig.transform.rotation = Quaternion.LookRotation(Quaternion.Euler(0f, 40f, 0f) * direction, Vector3.up);
                commands = PlayerCommands.Empty;
                touch.Step(ref commands, 1f / 30f, 0.2f);
                Expect(!touch.HasTarget && !commands.FireHeld, "паук вне конуса — ни подтяжки, ни огня", text, ref failed);

                // Джойстик: касание слева и сдвиг вверх — ход вперёд.
                var at = new Vector2(Screen.width * 0.2f, Screen.height * 0.3f);
                touch.Injected.Clear();
                touch.Injected.Add(new TouchPoint(1, TouchPhase.Began, at));
                commands = PlayerCommands.Empty;
                touch.Step(ref commands, 1f / 30f, 0.3f);

                touch.Injected.Clear();
                touch.Injected.Add(new TouchPoint(1, TouchPhase.Moved, at + new Vector2(0f, touch.Router.JoystickRadius)));
                commands = PlayerCommands.Empty;
                touch.Step(ref commands, 1f / 30f, 0.4f);

                text.AppendLine($"  джойстик вверх на радиус: ход {commands.Move}");
                Expect(commands.Move.y > 0.95f, "джойстик вверх — ход вперёд", text, ref failed);
            }
            finally
            {
                touch.Injected = null;
                touch.Router.Reset();
                rig.SetInputSource(new DesktopInputSource());
                Input.simulateMouseWithTouches = true;
                crowd.ClearAll();
                arsenal.ClearLoadout();
            }

            return failed;
        }

        private static GameUi BuildUi(CheckScene scene, StringBuilder text, ref int failed)
        {
            text.AppendLine();
            text.AppendLine("=== Экраны ===");

            var controller = Object.FindAnyObjectByType<CaveRunController>();

            if (controller == null)
            {
                text.AppendLine("  ПЛОХО: нет CaveRunController — «Починить свет» его не создал");
                failed++;
                return null;
            }

            controller.EnsureLinks();
            controller.StartRun();

            // В режиме редактирования Awake у добавленного компонента не зовётся — собираем руками.
            var ui = new GameObject("Game UI (UiCheck)") { hideFlags = HideFlags.DontSave }.AddComponent<GameUi>();
            ui.Build();
            ui.EnsureLinks();

            var canvases = ui.GetComponentsInChildren<Canvas>(true);
            var scalers = ui.GetComponentsInChildren<CanvasScaler>(true);
            var safe = ui.GetComponentsInChildren<SafeArea>(true);

            text.AppendLine($"  канвасов {canvases.Length}, подогнанных под 1280×720 и 0.5: " +
                            $"{scalers.Count(s => s.referenceResolution == UiKit.Reference && Mathf.Approximately(s.matchWidthOrHeight, 0.5f))}, " +
                            $"с безопасной зоной {safe.Length}");

            Expect(canvases.Length == 3, "три канваса: статичный HUD, динамичный HUD, экраны", text, ref failed);
            Expect(scalers.Length == 3 && scalers.All(s => s.referenceResolution == UiKit.Reference), "масштаб от 1280×720", text, ref failed);
            Expect(safe.Length == 3, "у каждого канваса отступ по Screen.safeArea", text, ref failed);
            Expect(ui.Hud != null && ui.Cards != null && ui.Death != null && ui.Results != null && ui.Pause != null && ui.Hints != null,
                "все экраны собраны: HUD, карточки, смерть, итоги, пауза, подсказки", text, ref failed);
            Expect(ui.Controller == controller && ui.Rig == scene.Player && ui.Director == scene.Director,
                "интерфейс нашёл контроллер, игрока и директора", text, ref failed);

            return ui;
        }

        private static int CheckScreens(GameUi ui, StringBuilder text)
        {
            var failed = 0;
            var controller = ui.Controller;

            foreach (var language in Loc.Languages)
            {
                Loc.SetLanguage(language);

                // Карточки: уровень через опыт — тем же путём, что в игре, событием контроллера.
                controller.Session.AddXp(XpCurve.Need(controller.Session.Level));
                controller.Tick(Step, Step);

                var cardsOpen = ui.Cards.IsOpen;
                var cardsShown = controller.Cards.Count;
                var leftovers = Leftovers(ui);

                if (controller.Offering) controller.Choose(0);

                ui.Hud.Tick(Step);

                ui.Death.Show(controller);
                var deathOpen = ui.Death.IsOpen;
                leftovers.AddRange(Leftovers(ui));
                ui.Death.SetOpen(false);

                var results = new RunResults
                {
                    Victory = language == "en",
                    Seconds = 333f,
                    Tier = 1,
                    Depth = 1,
                    Level = controller.Session.Level,
                    Kills = 1234,
                    EliteKills = 2,
                    QueensKilled = 1,
                    Chitin = 56f,
                    KillsByWeapon = Enumerable.Range(0, (int)WeaponId.Count).ToArray(),
                    DeathKind = DamageKind.Bite
                };

                ui.Results.Show(results);
                var resultsOpen = ui.Results.IsOpen;
                leftovers.AddRange(Leftovers(ui));
                ui.Results.SetOpen(false);

                ui.Pause.SetOpen(true);
                var pauseOpen = ui.Pause.IsOpen;
                leftovers.AddRange(Leftovers(ui));
                ui.Pause.SetOpen(false);

                leftovers = leftovers.Distinct().ToList();

                text.AppendLine($"  {language}: карточки {(cardsOpen ? "открылись" : "НЕ ОТКРЫЛИСЬ")} ({cardsShown} шт.), " +
                                $"смерть {(deathOpen ? "да" : "НЕТ")}, итоги {(resultsOpen ? "да" : "НЕТ")}, пауза {(pauseOpen ? "да" : "НЕТ")}, " +
                                $"сырых ключей и {{n}} на экранах: {leftovers.Count}" +
                                (leftovers.Count > 0 ? " — " + string.Join(" | ", leftovers.Take(6)) : ""));

                Expect(cardsOpen && cardsShown >= 3, $"{language}: уровень открывает экран из трёх карточек и больше", text, ref failed);
                Expect(deathOpen && resultsOpen && pauseOpen, $"{language}: смерть, итоги и пауза открываются", text, ref failed);
                Expect(leftovers.Count == 0, $"{language}: на открытых экранах ни ключей, ни неподставленных {{n}}", text, ref failed);
            }

            return failed;
        }

        /// <summary>Видимые тексты, оставшиеся ключом или с неподставленной {n}.</summary>
        private static List<string> Leftovers(GameUi ui)
        {
            var found = new List<string>();

            foreach (var label in ui.GetComponentsInChildren<TextMeshProUGUI>(false))
            {
                var value = label.text;
                if (string.IsNullOrEmpty(value)) continue;

                if (RawKey.IsMatch(value.Trim()) || Placeholder.IsMatch(value)) found.Add(label.name + ": " + value);
            }

            return found;
        }

        // ------------------------------------------------------------------ аллокации

        /// <summary>
        /// Кадр HUD при идущей стадии: таймер, уровень, хитин, полосы, указатель.
        ///
        /// Счётчик — <c>ProfilerRecorder</c> «GC Allocated In Frame» и «GC Allocation In Frame Count»:
        /// их текущее значение живое и внутри одного вызова. <c>GC.GetAllocatedBytesForCurrentThread</c>,
        /// <c>GC.GetTotalMemory</c> и <c>Profiler.GetMonoUsedSizeLong</c> в редакторе на тех же
        /// трёх тысячах аллокаций показывают ноль — их поймал контроль ниже. Меряется приращение
        /// ВНУТРИ вызова интерфейса: мир между кадрами (директор, здоровье) сорит своим и в замер
        /// не попадает. Контроль контроля: тот же замер обязан увидеть заведомые 64 байта на кадр.
        /// </summary>
        private static int CheckAllocations(GameUi ui, CheckScene scene, StringBuilder text)
        {
            var failed = 0;

            text.AppendLine();
            text.AppendLine("=== Аллокации кадра HUD ===");

            const int frames = 3000;

            var sink = new object[1];
            var control = Measure(frames, () => sink[0] = new byte[64], () => { });
            GC.KeepAlive(sink);

            text.AppendLine($"  контроль: 64 байта на кадр за {frames} кадров — выделено {control.Bytes} байт, " +
                            $"{control.Count} раз, в {control.Frames} кадрах");
            Expect(control.Count >= frames, "замер видит заведомую аллокацию", text, ref failed);

            Loc.SetLanguage("ru");
            scene.Director.Restart();

            var controller = ui.Controller;

            // Прогрев: первые кадры строят ленивые кэши TMP и раскладку — это не кадр игры.
            for (var i = 0; i < 90; i++)
            {
                ui.Tick(Step);
                Frame(scene, controller, i);
            }

            // В редакторе TMP на каждый SetText пересобирает строку для инспектора (`#if UNITY_EDITOR`
            // в TMP_Text.SetText) — две аллокации на смену числа, которых в сборке нет. Поэтому кадры
            // делятся: без смены текста обязан быть ноль, смены — отдельной строкой отчёта.
            var labels = ui.GetComponentsInChildren<TMP_Text>(true);
            var shown = new string[labels.Length];

            long steadyAllocs = 0, changeAllocs = 0;
            int steadyFrames = 0, steadyDirty = 0, changeFrames = 0, changes = 0;
            double totalMs = 0, maxMs = 0;

            using (var count = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocation In Frame Count"))
            {
                for (var i = 0; i < frames; i++)
                {
                    for (var j = 0; j < labels.Length; j++) shown[j] = labels[j].text;

                    var before = count.CurrentValue;
                    var started = Stopwatch.GetTimestamp();

                    ui.Tick(Step);

                    var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                    var allocated = Math.Max(0, count.CurrentValue - before);

                    totalMs += ms;
                    maxMs = Math.Max(maxMs, ms);

                    var changed = 0;
                    for (var j = 0; j < labels.Length; j++)
                    {
                        if (!ReferenceEquals(labels[j].text, shown[j])) changed++;
                    }

                    if (changed == 0)
                    {
                        steadyFrames++;
                        steadyAllocs += allocated;
                        if (allocated > 0) steadyDirty++;
                    }
                    else
                    {
                        changeFrames++;
                        changes += changed;
                        changeAllocs += allocated;
                    }

                    Frame(scene, controller, i);
                }
            }

            text.AppendLine($"  {frames} кадров ({frames * Step:0} с стадии), кадр интерфейса в среднем {totalMs / frames:0.000} мс, " +
                            $"наибольший {maxMs:0.000} мс");
            text.AppendLine($"  без смены текста: {steadyFrames} кадров, выделено {steadyAllocs} раз в {steadyDirty} кадрах");
            // Цена одного SetText в этом редакторе — тем же TMP на пробной подписи.
            var tmpCost = TmpSetTextCost(ui);
            var allowed = (long)Math.Ceiling(tmpCost * changes * 1.02) + 2;

            text.AppendLine($"  со сменой текста: {changeFrames} кадров, смен {changes}, выделено {changeAllocs} раз " +
                            $"({(changes > 0 ? (double)changeAllocs / changes : 0):0.00} на смену; сам TMP в редакторе тратит " +
                            $"{tmpCost:0.00} на SetText, в сборке 0)");

            Expect(steadyFrames > frames / 2, "большинство кадров — без смены текста (иначе замер ни о чём)", text, ref failed);
            Expect(steadyAllocs == 0, "кадр HUD без смены чисел — без аллокаций", text, ref failed);
            Expect(changeAllocs <= allowed, "смена числа — не дороже строки, которую TMP в редакторе собирает для инспектора", text, ref failed);

            if (steadyAllocs > 0 || changeAllocs > allowed)
                text.AppendLine("  по частям кадра (вместе со строками TMP): " + Breakdown(ui, scene, controller, 600));

            return failed;
        }

        /// <summary>Аллокаций на один SetText с числом — в редакторе TMP собирает из него строку для инспектора.</summary>
        private static double TmpSetTextCost(GameUi ui)
        {
            var label = UiKit.Text(ui.ScreenRoot, "UiCheck probe", 20f, Color.white);

            try
            {
                using var count = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocation In Frame Count");

                const int calls = 400;
                var before = count.CurrentValue;

                for (var i = 0; i < calls; i++)
                {
                    if ((i & 1) == 0) label.SetText("{0}", 90 + i);
                    else label.SetText("{0:00}:{1:00}", i / 60, i % 60);
                }

                return Math.Max(0, count.CurrentValue - before) / (double)calls;
            }
            finally
            {
                Object.DestroyImmediate(label.gameObject);
            }
        }

        /// <summary>
        /// Кто сорит: части кадра HUD по отдельности. Зовутся типизированными делегатами,
        /// собранными заранее, — вызов через MethodInfo.Invoke сам выделял бы массив аргументов.
        /// </summary>
        private static string Breakdown(GameUi ui, CheckScene scene, CaveRunController controller, int frames)
        {
            var hud = ui.Hud;
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

            T Bind<T>(string name) where T : Delegate =>
                (T)Delegate.CreateDelegate(typeof(T), hud, typeof(HudView).GetMethod(name, flags));

            var run = Bind<Action<CaveRunController>>("TickRun");
            var stage = Bind<Action<CaveRunDirector, float>>("TickStage");
            var body = Bind<Action<CatacombTestRig, float>>("TickBody");
            var weapons = Bind<Action<PlayerArsenal>>("TickWeapons");
            var boss = Bind<Action<CaveQueen>>("TickBoss");
            var pointer = Bind<Action<CaveQueen, CatacombTestRig, CaveRunDirector, CaveRunController>>("TickPointer");
            var markers = Bind<Action<float>>("TickMarkers");

            var parts = new (string Name, Action Call)[]
            {
                ("уровень и хитин", () => run(ui.Controller)),
                ("таймер и объявления", () => stage(ui.Director, Step)),
                ("здоровье и рывок", () => body(ui.Rig, Step)),
                ("пушки", () => weapons(ui.Arsenal)),
                ("Матка", () => boss(ui.Queen)),
                ("указатель", () => pointer(ui.Queen, ui.Rig, ui.Director, ui.Controller)),
                ("маркеры", () => markers(Step)),
                ("подсказки", () => ui.Hints.Tick(Step, GameState.Playing))
            };

            var counts = new long[parts.Length];

            using var count = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocation In Frame Count");

            for (var i = 0; i < frames; i++)
            {
                for (var p = 0; p < parts.Length; p++)
                {
                    var before = count.CurrentValue;
                    parts[p].Call();
                    counts[p] += Math.Max(0, count.CurrentValue - before);
                }

                Frame(scene, controller, i);
            }

            return string.Join(", ", parts.Select((part, p) => $"{part.Name} {counts[p]}"));
        }

        private struct HeapGrowth
        {
            public long Bytes;
            public long Count;
            public int Frames;
        }

        private static HeapGrowth Measure(int frames, Action measured, Action between)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var growth = new HeapGrowth();

            using var bytes = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            using var count = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocation In Frame Count");

            for (var i = 0; i < frames; i++)
            {
                var bytesBefore = bytes.CurrentValue;
                var countBefore = count.CurrentValue;

                measured();

                var allocated = count.CurrentValue - countBefore;

                // Отрицательное — счётчик кадра обнулился посреди вызова: такой кадр не в счёт.
                if (allocated > 0)
                {
                    growth.Bytes += Math.Max(0, bytes.CurrentValue - bytesBefore);
                    growth.Count += allocated;
                    growth.Frames++;
                }

                between();
            }

            return growth;
        }

        /// <summary>Мир между кадрами интерфейса: идёт стадия, капает опыт и хитин, кусают.</summary>
        private static void Frame(CheckScene scene, CaveRunController controller, int i)
        {
            scene.Director.Tick(Step);

            if (i % 7 == 0)
            {
                controller.Session.Kills++;
                controller.Session.Chitin += 0.4f;
                controller.Session.Xp = Mathf.Repeat(controller.Session.Xp + 0.5f, 5f);
            }

            if (i % 45 == 0) scene.Vitals.TakeDamage(DamageHit.Simple(2f, scene.Entry + Vector3.forward * 3f, DamageKind.Bite));
            scene.Vitals.Tick(Step);
        }

        // ------------------------------------------------------------------ помощники

        private static void Report(List<string> items, string what, StringBuilder text)
        {
            if (items.Count == 0) return;

            text.AppendLine($"  {what}: {items.Count} — {string.Join(", ", items.Take(12))}{(items.Count > 12 ? "…" : "")}");
        }

        private static void Expect(bool condition, string what, StringBuilder text, ref int failed)
        {
            text.AppendLine((condition ? "  ок: " : "  ПЛОХО: ") + what);
            if (!condition) failed++;
        }

        private static int Fail(StringBuilder text, int failed)
        {
            Debug.LogError($"ПРОВЕРКА ИНТЕРФЕЙСА НЕ ПРОШЛА, проблемных пунктов {failed}:{Environment.NewLine}{text}");
            return 1;
        }
    }
}
