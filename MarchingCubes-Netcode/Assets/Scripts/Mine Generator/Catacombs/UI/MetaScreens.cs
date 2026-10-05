using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MineGenerator.Catacombs
{
    // ====================================================================== лагерь

    /// <summary>
    /// Лагерь (M4.3): поверх маленьких катакомб с костром, камера облетает зал. Вкладки — «В бой»,
    /// «Арсенал», «Улучшения», «Классы», «Задания», «Настройки».
    ///
    /// Содержимое вкладки перестраивается целиком по её выбору и после покупки — не в кадре: лагерь
    /// не забег, и аллокации здесь стоят раз на клик, а не раз на кадр.
    /// </summary>
    public sealed class HubScreen : ModalScreen
    {
        private enum Tab { Play, Arsenal, Upgrades, Classes, Quests, Settings, Count }

        private readonly MetaGame _meta;

        private TextMeshProUGUI _chitin;
        private TextMeshProUGUI _notice;
        private RectTransform _content;
        private readonly Button[] _tabs = new Button[(int)Tab.Count];
        private Tab _tab = Tab.Play;

        public HubScreen(GameUi ui, MetaGame meta) : base(ui) => _meta = meta;

        public void Build(Transform parent)
        {
            Shade(parent, "Hub", 0.35f);

            var title = Title(Root, -30f, 48f);
            title.text = Loc.T("hub.title");

            _chitin = UiKit.Text(Root, "Chitin", 26f, UiKit.Teal, TextAlignmentOptions.Right, true);
            _chitin.rectTransform.Place(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-40f, -40f), new Vector2(360f, 40f));

            _notice = UiKit.Text(Root, "Notice", 18f, UiKit.Amber, TextAlignmentOptions.Left);
            _notice.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -40f), new Vector2(420f, 60f));

            for (var i = 0; i < (int)Tab.Count; i++)
            {
                var tab = (Tab)i;
                var button = UiKit.Button(Root, "Tab " + tab, "", () => Select(tab), 230f, 52f);
                button.GetComponent<RectTransform>().Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -120f - i * 62f), new Vector2(230f, 52f));
                _tabs[i] = button;
            }

            var panel = UiKit.Panel(Root, "Content", new Color(0.05f, 0.04f, 0.035f, 0.85f), UiKit.Rounded, true);
            panel.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(300f, -110f), new Vector2(920f, 570f));
            _content = panel.rectTransform;
        }

        public override void SetOpen(bool open)
        {
            base.SetOpen(open);
            if (open && _content != null) Refresh();
        }

        private void Select(Tab tab)
        {
            _tab = tab;
            Refresh();
        }

        public void Refresh()
        {
            var profile = _meta.Profile;
            if (profile == null || _content == null) return;

            _chitin.text = Loc.F("hub.chitin", Mathf.FloorToInt(profile.Chitin));

            // После первой смерти — подсветка улучшений (M4.3): «у тебя N хитина».
            var firstVisit = profile.Stats.Runs >= 1 && profile.TotalUpgradeLevels() == 0 && profile.Chitin >= 100f;
            _notice.text = (_meta.StreakReward > 0f ? Loc.F("hub.streak", profile.StreakDays, Mathf.RoundToInt(_meta.StreakReward)) + "\n" : "") +
                           (firstVisit ? Loc.F("hub.upgrade_hint", Mathf.FloorToInt(profile.Chitin)) : "");

            for (var i = 0; i < _tabs.Length; i++)
            {
                _tabs[i].SetLabel(Loc.T("hub.tab." + ((Tab)i).ToString().ToLowerInvariant()));
                _tabs[i].GetComponent<Image>().color = (Tab)i == _tab
                    ? new Color(0.35f, 0.2f, 0.08f, 0.97f)
                    : new Color(0.16f, 0.12f, 0.09f, 0.95f);
            }

            for (var i = _content.childCount - 1; i >= 0; i--) Object.Destroy(_content.GetChild(i).gameObject);

            _rowY = -20f;

            switch (_tab)
            {
                case Tab.Play: BuildPlay(profile); break;
                case Tab.Arsenal: BuildArsenal(profile); break;
                case Tab.Upgrades: BuildUpgrades(profile); break;
                case Tab.Classes: BuildClasses(profile); break;
                case Tab.Quests: BuildQuests(profile); break;
                case Tab.Settings: BuildSettings(profile); break;
            }
        }

        // ------------------------------------------------------------------ строки

        private float _rowY;

        private TextMeshProUGUI Line(string text, float size = 20f, Color? color = null, float height = 32f)
        {
            var label = UiKit.Text(_content, "Line", size, color ?? UiKit.Paper, TextAlignmentOptions.Left);
            label.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, _rowY), new Vector2(620f, height));
            label.text = text;

            _rowY -= height + 4f;
            return label;
        }

        private Button RowButton(string label, System.Action onClick, bool enabled = true, float width = 240f)
        {
            var button = UiKit.Button(_content, "Button", label, onClick, width, 34f);
            button.GetComponent<RectTransform>().Place(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-24f, _rowY + 36f), new Vector2(width, 34f));
            button.interactable = enabled;
            return button;
        }

        private Button BigButton(string label, System.Action onClick, Color color)
        {
            var button = UiKit.Button(_content, "Big", label, onClick, 420f, 64f, color);
            button.GetComponent<RectTransform>().Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, _rowY), new Vector2(420f, 64f));
            _rowY -= 76f;
            return button;
        }

        private void Stepper(string label, System.Action minus, System.Action plus)
        {
            Line(label, 22f);

            var less = UiKit.Button(_content, "Minus", "<", minus, 48f, 34f);
            less.GetComponent<RectTransform>().Place(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-84f, _rowY + 36f), new Vector2(48f, 34f));

            var more = UiKit.Button(_content, "Plus", ">", plus, 48f, 34f);
            more.GetComponent<RectTransform>().Place(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-24f, _rowY + 36f), new Vector2(48f, 34f));
        }

        // ------------------------------------------------------------------ вкладки

        private void BuildPlay(PlayerProfile profile)
        {
            var cls = (PlayerClass)profile.SelectedClass;
            var weapon = WeaponCatalog.Get(PlayerClasses.StartWeapon(cls));

            Stepper(Loc.F("hub.class", Loc.T(PlayerClasses.NameKey(cls))), () => CycleClass(-1), () => CycleClass(1));
            Line(Loc.T(PlayerClasses.DescKey(cls)), 17f, UiKit.Dim, 44f);
            Line(Loc.F("hub.start_weapon", Loc.T(weapon.NameKey)), 20f);

            Stepper(Loc.F("hub.depth", profile.SelectedDepth, profile.UnlockedDepth), () => CycleDepth(-1), () => CycleDepth(1));
            Line(Loc.F("hub.recommended", SortieDifficulty.Recommended(profile)), 17f, UiKit.Dim);

            _rowY -= 16f;

            if (profile.HasSnapshot)
            {
                BigButton(Loc.F("hub.continue", profile.Snapshot.Tier, profile.Snapshot.Depth),
                    () => { SetOpen(false); _meta.ContinueSortie(); }, new Color(0.12f, 0.22f, 0.12f, 0.97f));
            }

            BigButton(Loc.T("hub.fight"), () =>
            {
                SetOpen(false);
                _meta.StartSortie((PlayerClass)profile.SelectedClass, profile.SelectedDepth);
            }, new Color(0.38f, 0.16f, 0.05f, 0.97f));

            // Ящик дня за рекламу (M5.3): кнопка только если реклама есть и ящик сегодня не брали —
            // кнопка «за награду» не врёт.
            if (_meta.DailyBoxAvailable)
            {
                BigButton(Loc.F("hub.daily_box", AdPolicy.DailyBoxChitin), () => _meta.ClaimDailyBox(Refresh),
                    new Color(0.12f, 0.18f, 0.1f, 0.95f));
            }
        }

        private void CycleClass(int step)
        {
            var profile = _meta.Profile;
            var count = (int)PlayerClass.Count;
            var next = profile.SelectedClass;

            for (var i = 0; i < count; i++)
            {
                next = ((next + step) % count + count) % count;
                if (profile.UnlockedClasses.Contains(next)) break;
            }

            profile.SelectedClass = next;
            _meta.Saves.MarkDirty();
            Refresh();
        }

        private void CycleDepth(int step)
        {
            var profile = _meta.Profile;
            profile.SelectedDepth = Mathf.Clamp(profile.SelectedDepth + step, 1, profile.UnlockedDepth);
            _meta.Saves.MarkDirty();
            Refresh();
        }

        private void BuildArsenal(PlayerProfile profile)
        {
            for (var i = 0; i < (int)WeaponId.Count; i++)
            {
                var id = (WeaponId)i;
                var def = WeaponCatalog.Get(id);
                var unlocked = profile.IsWeaponUnlocked(id);
                var kills = profile.Stats.KillsByWeapon != null && i < profile.Stats.KillsByWeapon.Length ? profile.Stats.KillsByWeapon[i] : 0;

                Line($"{Loc.T(def.NameKey)}  <size=70%><color=#a89c86>{Loc.F("hub.weapon_kills", kills)}</color></size>", 21f);

                if (unlocked) continue;

                RowButton(Loc.F("hub.unlock_for", def.Price), () =>
                {
                    if (_meta.BuyWeapon(id)) Refresh();
                }, profile.Chitin >= def.Price);
            }
        }

        private void BuildUpgrades(PlayerProfile profile)
        {
            // Семнадцать улучшений в столбец не влезают: строки наезжали друг на друга, цена
            // переносилась. Сетка 2×9 карточек, последняя клетка второй колонки — сброс.
            const int rows = 9;
            const float width = 428f, height = 50f, gap = 5f;

            for (var i = 0; i <= (int)MetaUpgrade.Count; i++)
            {
                var x = 20f + i / rows * (width + 24f);
                var y = -16f - i % rows * (height + gap);

                if (i == (int)MetaUpgrade.Count)
                {
                    var refund = UiKit.Button(_content, "Refund", Loc.T("hub.refund"), () =>
                    {
                        _meta.RefundUpgrades();
                        Refresh();
                    }, width, height);
                    refund.GetComponent<RectTransform>().Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, y), new Vector2(width, height));
                    refund.GetComponentInChildren<TextMeshProUGUI>().fontSize = 18f;
                    refund.interactable = profile.ChitinSpentOnUpgrades > 0f;
                    break;
                }

                var id = (MetaUpgrade)i;
                var def = MetaUpgrades.Get(id);
                var level = profile.UpgradeLevel(id);
                var price = MetaUpgrades.PriceOf(profile, id);

                var card = UiKit.Panel(_content, "Upgrade", new Color(1f, 1f, 1f, 0.04f), UiKit.Rounded);
                card.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, y), new Vector2(width, height));

                var name = UiKit.Text(card.transform, "Name", 18f, UiKit.Paper, TextAlignmentOptions.Left, true);
                name.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(12f, -4f), new Vector2(width - 170f, 24f));
                name.text = $"{Loc.T(def.NameKey)}  <color=#a89c86>{level}/{def.MaxLevel}</color>";

                var desc = UiKit.Text(card.transform, "Desc", 14f, UiKit.Dim, TextAlignmentOptions.Left);
                desc.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(12f, -27f), new Vector2(width - 170f, 20f));
                desc.text = Loc.T(def.DescKey);

                if (level >= def.MaxLevel) continue;

                var buy = UiKit.Button(card.transform, "Buy", Loc.F("hub.buy_for", Mathf.RoundToInt(price)), () =>
                {
                    if (_meta.BuyUpgrade(id)) Refresh();
                }, 150f, 36f);
                buy.GetComponent<RectTransform>().Place(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-7f, 0f), new Vector2(150f, 36f));
                buy.GetComponentInChildren<TextMeshProUGUI>().fontSize = 16f;
                buy.interactable = profile.Chitin >= price;
            }
        }

        private void BuildClasses(PlayerProfile profile)
        {
            for (var i = 0; i < (int)PlayerClass.Count; i++)
            {
                var id = (PlayerClass)i;
                var open = profile.IsClassUnlocked(id);
                var quest = PlayerClasses.UnlockQuest(id);
                var condition = quest != null && Quests.Find(quest) != null ? MetaGame.QuestText(Quests.Find(quest)) : "";

                Line($"{Loc.T(PlayerClasses.NameKey(id))}  <size=70%><color=#a89c86>{Loc.T(PlayerClasses.DescKey(id))}</color></size>", 20f);
                Line(open ? Loc.T("hub.class_open") : Loc.F("hub.class_locked", condition), 16f, open ? UiKit.Teal : UiKit.Dim, 24f);
            }
        }

        private void BuildQuests(PlayerProfile profile)
        {
            Line(Loc.F("hub.daily", profile.StreakDays), 22f, UiKit.Amber);

            foreach (var def in Quests.DailyFor(_meta.Today))
            {
                var value = Quests.ProgressOf(profile, def);
                Line($"{MetaGame.QuestText(def)}  <color=#a89c86>{value}/{def.Target}</color>  <color=#7fd6c0>+{def.RewardValue}</color>", 18f);
            }

            _rowY -= 10f;
            Line(Loc.T("hub.permanent"), 22f, UiKit.Amber);

            var shown = 0;
            var done = 0;

            foreach (var def in Quests.Permanent)
            {
                var value = Quests.ProgressOf(profile, def);

                if (value >= def.Target)
                {
                    done++;
                    continue;
                }

                if (shown >= 9) continue;
                shown++;

                Line($"{MetaGame.QuestText(def)}  <color=#a89c86>{value}/{def.Target}</color>", 17f, null, 26f);
            }

            Line(Loc.F("hub.quests_done", done, Quests.Permanent.Count), 16f, UiKit.Dim);
        }

        private void BuildSettings(PlayerProfile profile)
        {
            var settings = profile.Settings;

            Stepper(Loc.F("hub.sensitivity", settings.Sensitivity.ToString("0.0")), () => Change(() => settings.Sensitivity = Mathf.Max(0.2f, settings.Sensitivity - 0.1f)),
                () => Change(() => settings.Sensitivity = Mathf.Min(3f, settings.Sensitivity + 0.1f)));

            Stepper(Loc.F("hub.volume", Mathf.RoundToInt(settings.MasterVolume * 100f)), () => Change(() => settings.MasterVolume = Mathf.Max(0f, settings.MasterVolume - 0.1f)),
                () => Change(() => settings.MasterVolume = Mathf.Min(1f, settings.MasterVolume + 0.1f)));

            Stepper(Loc.F("hub.language", Loc.Language), () => Change(() => CycleLanguage(settings, -1)), () => Change(() => CycleLanguage(settings, 1)));

            Stepper(Loc.F("hub.invert", Loc.T(settings.InvertY ? "hub.on" : "hub.off")), () => Change(() => settings.InvertY = !settings.InvertY),
                () => Change(() => settings.InvertY = !settings.InvertY));

            Stepper(Loc.F("hub.autofire", Loc.T(settings.AutoFire ? "hub.on" : "hub.off")), () => Change(() => settings.AutoFire = !settings.AutoFire),
                () => Change(() => settings.AutoFire = !settings.AutoFire));
        }

        private static void CycleLanguage(ProfileSettings settings, int step)
        {
            var languages = Loc.Languages;
            var index = System.Array.IndexOf(languages, Loc.Language);
            index = ((index + step) % languages.Length + languages.Length) % languages.Length;

            settings.Language = languages[index];
            Loc.SetLanguage(settings.Language);
        }

        private void Change(System.Action change)
        {
            change();
            _meta.ApplySettings();
            _meta.Saves.MarkDirty();
            Refresh();
        }
    }

    // ====================================================================== привал

    /// <summary>
    /// Привал между ярусами (M4.1): +30% здоровья уже вылечено, одна бесплатная карточка (или пропуск)
    /// и полное лечение за рекламу — если реклама есть. Время стоит (GameFlow.StageClear).
    /// </summary>
    public sealed class RestScreen : ModalScreen
    {
        private readonly MetaGame _meta;
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _body;
        private readonly CardScreen.CardView[] _cards = new CardScreen.CardView[4];
        private Button _skip;
        private Button _heal;

        public RestScreen(GameUi ui, MetaGame meta) : base(ui) => _meta = meta;

        public void Build(Transform parent)
        {
            Shade(parent, "Rest", 0.78f);

            _title = Title(Root, -70f, 50f);

            _body = UiKit.Text(Root, "Body", 22f, UiKit.Paper, TextAlignmentOptions.Center);
            _body.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -130f), new Vector2(1000f, 60f));

            for (var i = 0; i < _cards.Length; i++)
            {
                var index = i;
                _cards[i] = CardScreen.BuildCard(Root, i, () => Pick(index));
            }

            _skip = UiKit.Button(Root, "Skip", "", () => Pick(-1), 260f, 52f);
            _skip.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-150f, 50f), new Vector2(260f, 52f));

            _heal = UiKit.Button(Root, "Heal Ad", "", HealForAd, 260f, 52f, new Color(0.12f, 0.18f, 0.1f, 0.95f));
            _heal.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(150f, 50f), new Vector2(260f, 52f));
        }

        public void Show()
        {
            var session = _meta.Controller != null ? _meta.Controller.Session : null;
            var cards = _meta.RestCards;

            _title.text = Loc.T("rest.title");
            _body.text = Loc.F("rest.body", Mathf.RoundToInt(MetaGame.RestHeal * 100f), session != null ? session.Tier + 1 : 2);

            var width = cards.Count * 270f;

            for (var i = 0; i < _cards.Length; i++)
            {
                var view = _cards[i];
                var visible = i < cards.Count && session != null;

                view.Button.gameObject.SetActive(visible);
                if (!visible) continue;

                view.Button.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(-width * 0.5f + 135f + i * 270f, -10f), new Vector2(250f, 340f));

                CardScreen.Describe(cards[i], session, view);
            }

            _skip.SetLabel(Loc.T("rest.skip"));
            _heal.SetLabel(Loc.T("rest.heal_ad"));
            _heal.gameObject.SetActive(Platform.Current.RewardedAvailable);

            SetOpen(true);
        }

        public void Tick()
        {
            var rig = Ui.Rig;
            if (rig == null) return;

            var key = rig.Commands.CardKey;
            if (key >= 0 && key < _meta.RestCards.Count) Pick(key);
        }

        private void Pick(int index)
        {
            Ui.Audio?.Play(SoundId.UiClick, 0.6f);
            SetOpen(false);
            _meta.ChooseRestCard(index);
        }

        private void HealForAd()
        {
            _heal.gameObject.SetActive(false);
            _meta.RestFullHealForAd(null);
        }
    }

    // ====================================================================== загрузка

    /// <summary>
    /// Загрузка яруса (M4.1): «Спуск…», прогресс генерации и совет. Генерация идёт по бюджету
    /// кадров, и экран живёт, пока она идёт.
    /// </summary>
    public sealed class LoadingView
    {
        private const int Tips = 8;

        private RectTransform _root;
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _tip;
        private Image _fill;
        private int _shownTip = -1;

        public void Build(Transform parent)
        {
            _root = UiKit.Node(parent, "Loading").Stretch();

            var shade = UiKit.Panel(_root, "Shade", new Color(0f, 0f, 0f, 0.92f), null, true);
            shade.rectTransform.Stretch(-200f, -200f, -200f, -200f);

            _title = UiKit.Text(_root, "Title", 40f, UiKit.Amber, TextAlignmentOptions.Center, true);
            _title.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 60f), new Vector2(900f, 60f));

            _fill = UiKit.Bar(_root, "Progress", new Color(0.1f, 0.08f, 0.06f, 1f), UiKit.Amber, out var back);
            back.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(520f, 14f));

            _tip = UiKit.Text(_root, "Tip", 20f, UiKit.Dim, TextAlignmentOptions.Center);
            _tip.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -60f), new Vector2(900f, 80f));

            _root.gameObject.SetActive(false);
        }

        public void Tick(bool loading, float progress)
        {
            if (_root.gameObject.activeSelf != loading)
            {
                _root.gameObject.SetActive(loading);

                if (loading)
                {
                    _shownTip = (_shownTip + 1 + Random.Range(0, Tips - 1)) % Tips;
                    _title.text = Loc.T("loading.title");
                    _tip.text = Loc.T("loading.tip" + _shownTip);
                }
            }

            if (loading) _fill.fillAmount = Mathf.Clamp01(progress);
        }
    }
}
