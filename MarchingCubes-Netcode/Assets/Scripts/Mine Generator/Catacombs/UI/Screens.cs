using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MineGenerator.Catacombs
{
    /// <summary>Затемнение и рамка экрана — общее у всех модальных экранов.</summary>
    public abstract class ModalScreen : IUiScreen
    {
        protected readonly GameUi Ui;
        protected RectTransform Root;

        protected ModalScreen(GameUi ui) => Ui = ui;

        public bool IsOpen => Root != null && Root.gameObject.activeSelf;

        public virtual void SetOpen(bool open)
        {
            if (Root != null) Root.gameObject.SetActive(open);
        }

        protected RectTransform Shade(Transform parent, string name, float alpha)
        {
            Root = UiKit.Node(parent, name).Stretch();

            var shade = UiKit.Panel(Root, "Shade", new Color(0f, 0f, 0f, alpha), null, true);
            shade.rectTransform.Stretch(-200f, -200f, -200f, -200f);

            return Root;
        }

        protected static TextMeshProUGUI Title(Transform parent, float y, float size = 44f)
        {
            var title = UiKit.Text(parent, "Title", size, UiKit.Amber, TextAlignmentOptions.Center, true);
            title.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(1100f, 64f));
            return title;
        }
    }

    // ====================================================================== карточки

    /// <summary>
    /// Выбор улучшения на уровне: три карточки (четыре при удаче), клавиши 1–4, клик, касание.
    /// Время стоит (GameFlow.LevelUp). Несколько уровней подряд — очередь экранов: контроллер
    /// открывает следующий сам. Переброс, пропуск, изгнание — счётчики меты (M4), переброс за
    /// рекламу — раз за уровень (M5).
    /// </summary>
    public sealed class CardScreen : ModalScreen
    {
        private const int MaxCards = 4;

        private TextMeshProUGUI _title;
        private readonly CardView[] _cards = new CardView[MaxCards];
        private Button _reroll;
        private Button _adReroll;
        private Button _skip;
        private Button _banish;

        private CaveRunController _controller;
        private bool _banishing;
        private bool _adRerollUsed;
        private int _shownLevel = -1;

        internal sealed class CardView
        {
            public Button Button;
            public Image Stripe;
            public TextMeshProUGUI Kind;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI Body;
            public TextMeshProUGUI Key;
        }

        public CardScreen(GameUi ui) : base(ui) { }

        public void Build(Transform parent)
        {
            Shade(parent, "Cards", 0.72f);

            _title = Title(Root, -90f);

            for (var i = 0; i < MaxCards; i++)
            {
                var index = i;
                _cards[i] = BuildCard(Root, i, () => Pick(index));
            }

            _reroll = UiKit.Button(Root, "Reroll", "", Reroll, 220f, 48f);
            _reroll.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-360f, 70f), new Vector2(220f, 48f));

            _adReroll = UiKit.Button(Root, "Ad Reroll", "", AdReroll, 220f, 48f, new Color(0.12f, 0.18f, 0.1f, 0.95f));
            _adReroll.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-120f, 70f), new Vector2(220f, 48f));

            _skip = UiKit.Button(Root, "Skip", "", Skip, 220f, 48f);
            _skip.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(120f, 70f), new Vector2(220f, 48f));

            _banish = UiKit.Button(Root, "Banish", "", ToggleBanish, 220f, 48f);
            _banish.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(360f, 70f), new Vector2(220f, 48f));
        }

        public void Show(CaveRunController controller)
        {
            _controller = controller;
            _banishing = false;

            if (controller.Session.Level != _shownLevel)
            {
                _shownLevel = controller.Session.Level;
                _adRerollUsed = false;
            }

            Refresh();
            SetOpen(true);
        }

        /// <summary>Карточка — кнопка с полосой, видом, именем и описанием. Общая с привалом (M4).</summary>
        internal static CardView BuildCard(Transform root, int i, System.Action onClick)
        {
            var view = new CardView();

            view.Button = UiKit.Button(root, "Card " + i, "", onClick, 250f, 340f, new Color(0.1f, 0.075f, 0.06f, 0.97f));

            var card = view.Button.transform;
            card.GetComponentInChildren<TextMeshProUGUI>().text = "";

            view.Stripe = UiKit.Panel(card, "Stripe", UiKit.Amber);
            view.Stripe.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(230f, 6f));

            view.Kind = UiKit.Text(card, "Kind", 16f, UiKit.Dim, TextAlignmentOptions.Center);
            view.Kind.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(230f, 24f));

            view.Name = UiKit.Text(card, "Name", 26f, UiKit.Paper, TextAlignmentOptions.Center, true);
            view.Name.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -50f), new Vector2(230f, 64f));

            view.Body = UiKit.Text(card, "Body", 18f, UiKit.Paper, TextAlignmentOptions.Top);
            view.Body.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -120f), new Vector2(222f, 180f));

            view.Key = UiKit.Text(card, "Key", 18f, UiKit.Amber, TextAlignmentOptions.Center, true);
            view.Key.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 10f), new Vector2(60f, 26f));
            view.Key.text = $"[{i + 1}]";

            return view;
        }

        private void Refresh()
        {
            var cards = _controller.Cards;
            var session = _controller.Session;

            _title.text = _controller.CocoonOffer
                ? Loc.T("cards.cocoon")
                : Loc.F("cards.title", session.Level) + (session.PendingLevels > 1 ? $"  <size=60%>(+{session.PendingLevels - 1})</size>" : "");

            var width = cards.Count * 270f;

            for (var i = 0; i < MaxCards; i++)
            {
                var view = _cards[i];
                var visible = i < cards.Count;

                view.Button.gameObject.SetActive(visible);
                if (!visible) continue;

                var rect = view.Button.GetComponent<RectTransform>();
                rect.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-width * 0.5f + 135f + i * 270f, 10f),
                    new Vector2(250f, 340f));

                Describe(cards[i], session, view);
            }

            var cocoon = _controller.CocoonOffer;

            _reroll.gameObject.SetActive(!cocoon && session.Rerolls > 0);
            _reroll.SetLabel(Loc.F("cards.reroll", session.Rerolls));

            _adReroll.gameObject.SetActive(!cocoon && !_adRerollUsed && Platform.Current.RewardedAvailable);
            _adReroll.SetLabel(Loc.T("cards.reroll_ad"));

            _skip.gameObject.SetActive(!cocoon && session.Skips > 0);
            _skip.SetLabel(Loc.F("cards.skip", session.Skips));

            _banish.gameObject.SetActive(!cocoon && session.Banishes > 0);
            _banish.SetLabel(_banishing ? Loc.T("cards.banish_pick") : Loc.F("cards.banish", session.Banishes));
        }

        internal static void Describe(Card card, RunSession session, CardView view)
        {
            switch (card.Type)
            {
                case CardType.NewWeapon:
                case CardType.WeaponLevel:
                case CardType.Evolution:
                {
                    var def = WeaponCatalog.Get(card.Weapon);

                    view.Stripe.color = card.Type == CardType.Evolution ? new Color(1f, 0.85f, 0.3f) : def.Tint;
                    view.Name.text = Loc.T(def.NameKey);

                    if (card.Type == CardType.NewWeapon)
                    {
                        view.Kind.text = Loc.T("cards.new_weapon");
                        view.Body.text = Loc.T(def.DescKey);
                    }
                    else if (card.Type == CardType.WeaponLevel)
                    {
                        view.Kind.text = Loc.F("cards.weapon_level", card.ToLevel);
                        view.Body.text = Loc.T(def.LevelTextKey(card.ToLevel)) +
                                         (card.ToLevel == WeaponDef.AltLevel ? "\n\n<color=#8fb8ff>" + Loc.T(def.AltKey) + "</color>" : "");
                    }
                    else
                    {
                        view.Kind.text = Loc.T("cards.evolution");
                        view.Name.text = Loc.T(def.EvolutionKey);
                        view.Body.text = Loc.T(def.EvolutionKey + ".desc");
                    }

                    break;
                }

                case CardType.NewTome:
                case CardType.TomeLevel:
                {
                    var tome = Tomes.Get(card.Tome);

                    view.Stripe.color = new Color(0.55f, 0.75f, 0.95f);
                    view.Kind.text = card.Type == CardType.NewTome ? Loc.T("cards.new_tome") : Loc.F("cards.tome_level", card.ToLevel);
                    view.Name.text = Loc.T(tome.NameKey);
                    view.Body.text = Loc.T(tome.DescKey);
                    break;
                }

                case CardType.Heal:
                    view.Stripe.color = UiKit.Blood;
                    view.Kind.text = Loc.T("cards.spare");
                    view.Name.text = Loc.T("cards.heal");
                    view.Body.text = Loc.F("cards.heal_desc", Mathf.RoundToInt(card.Amount));
                    break;

                case CardType.Chitin:
                    view.Stripe.color = UiKit.Teal;
                    view.Kind.text = Loc.T("cards.spare");
                    view.Name.text = Loc.T("cards.chitin");
                    view.Body.text = Loc.F("cards.chitin_desc", Mathf.RoundToInt(card.Amount));
                    break;
            }
        }

        /// <summary>Клавиши 1–4 — команда кадра (стенд держит её и при заблокированном вводе).</summary>
        public void Tick()
        {
            var rig = Ui.Rig;
            if (rig == null || _controller == null) return;

            var key = rig.Commands.CardKey;
            if (key >= 0 && key < _controller.Cards.Count) Pick(key);
        }

        private void Pick(int index)
        {
            if (_controller == null) return;

            Ui.Audio?.Play(SoundId.UiClick, 0.6f);

            if (_banishing)
            {
                _banishing = false;

                if (_controller.Banish(index)) Refresh();
                return;
            }

            _controller.Choose(index);
        }

        private void Reroll()
        {
            if (_controller != null && _controller.Reroll()) Refresh();
        }

        private void AdReroll()
        {
            if (_controller == null || _adRerollUsed) return;

            Platform.Rewarded("reroll", ok =>
            {
                if (!ok) return;

                _adRerollUsed = true;
                _controller.Reroll(true);
                Refresh();
            });
        }

        private void Skip() => _controller?.Skip();

        private void ToggleBanish()
        {
            _banishing = !_banishing;
            Refresh();
        }
    }

    // ====================================================================== смерть

    /// <summary>
    /// Экран смерти: возродиться зарядом (мета «Возрождение»), за рекламу (раз за забег) или итоги.
    /// Кнопка рекламы показывается, только если реклама есть: CrazyGames отклоняет игры с кнопками
    /// рекламы «без эффекта».
    /// </summary>
    public sealed class DeathScreen : ModalScreen
    {
        private TextMeshProUGUI _title;
        private Button _free;
        private Button _ad;
        private Button _results;
        private CaveRunController _controller;

        public DeathScreen(GameUi ui) : base(ui) { }

        public void Build(Transform parent)
        {
            Shade(parent, "Death", 0.6f);

            _title = Title(Root, -200f, 58f);
            _title.color = UiKit.Blood;

            _free = UiKit.Button(Root, "Revive", "", () => _controller?.ReviveFree(), 360f, 60f);
            _free.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 20f), new Vector2(360f, 60f));

            _ad = UiKit.Button(Root, "Revive Ad", "", () => _controller?.ReviveForAd(), 360f, 60f, new Color(0.12f, 0.18f, 0.1f, 0.95f));
            _ad.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -56f), new Vector2(360f, 60f));

            _results = UiKit.Button(Root, "Results", "", () => _controller?.Finish(false), 360f, 60f);
            _results.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -132f), new Vector2(360f, 60f));
        }

        public void Show(CaveRunController controller)
        {
            _controller = controller;

            _title.text = Loc.T("death.title");

            var revives = Ui.Rig != null && Ui.Rig.Vitals != null ? Ui.Rig.Vitals.Revives : 0;

            _free.gameObject.SetActive(controller.CanReviveFree);
            _free.SetLabel(Loc.F("death.revive", revives));

            _ad.gameObject.SetActive(controller.CanReviveAd);
            _ad.SetLabel(Loc.T("death.revive_ad"));

            _results.SetLabel(Loc.T("death.results"));

            SetOpen(true);
        }

        public void Tick()
        {
            // Без кнопок возрождения — итоги по любой клавише выбора.
            if (_controller == null || Ui.Rig == null) return;
            if (!_controller.CanReviveFree && !_controller.CanReviveAd && Ui.Rig.Commands.CardKey == 0) _controller.Finish(false);
        }
    }

    // ====================================================================== итоги

    /// <summary>
    /// Итоги забега: время, ярус, уровень, убито (всего и по пушкам), элита, Матки, хитин,
    /// «до следующего открытия» (M4). «Ещё раз» — сразу в забег, «В лагерь» — мета (M4).
    /// Межстраничная реклама — только на «Ещё раз», не на навигации (требование CrazyGames).
    /// </summary>
    public sealed class ResultsScreen : ModalScreen
    {
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _body;
        private TextMeshProUGUI _extra;
        private Button _again;
        private Button _camp;
        private Button _doubleChitin;

        /// <summary>«Ещё раз» и «В лагерь» — мета подменяет (M4: реклама, сохранение профиля).</summary>
        public System.Action OnAgain;
        public System.Action OnCamp;
        public System.Action OnDoubleChitin;

        /// <summary>Строка «до следующего открытия», задания — пишет мета (M4).</summary>
        public System.Func<RunResults, string> ExtraText;

        public ResultsScreen(GameUi ui) : base(ui) { }

        public void Build(Transform parent)
        {
            Shade(parent, "Results", 0.82f);

            _title = Title(Root, -70f, 52f);

            _body = UiKit.Text(Root, "Body", 22f, UiKit.Paper, TextAlignmentOptions.TopLeft);
            _body.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-140f, -150f), new Vector2(520f, 380f));

            _extra = UiKit.Text(Root, "Extra", 20f, UiKit.Teal, TextAlignmentOptions.TopLeft);
            _extra.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(300f, -150f), new Vector2(380f, 380f));

            _again = UiKit.Button(Root, "Again", "", () => (OnAgain ?? DefaultAgain)(), 280f, 60f, new Color(0.3f, 0.16f, 0.06f, 0.97f));
            _again.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-160f, 60f), new Vector2(280f, 60f));

            _camp = UiKit.Button(Root, "Camp", "", () => (OnCamp ?? DefaultAgain)(), 280f, 60f);
            _camp.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(160f, 60f), new Vector2(280f, 60f));

            _doubleChitin = UiKit.Button(Root, "Double", "", () => OnDoubleChitin?.Invoke(), 280f, 50f, new Color(0.12f, 0.18f, 0.1f, 0.95f));
            _doubleChitin.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 136f), new Vector2(280f, 50f));
        }

        private void DefaultAgain() => Ui.Controller?.Restart();

        public void Show(RunResults results)
        {
            _title.text = Loc.T(results.Victory ? "results.victory" : "results.defeat");
            _title.color = results.Victory ? UiKit.Amber : UiKit.Blood;

            var text = new StringBuilder();

            text.AppendLine(Loc.F("results.time", UiKit.Clock(results.Seconds)));
            text.AppendLine(Loc.F("results.tier", results.Tier, results.Depth));
            text.AppendLine(Loc.F("results.level", results.Level));
            text.AppendLine(Loc.F("results.kills", results.Kills));
            text.AppendLine(Loc.F("results.elites", results.EliteKills));
            text.AppendLine(Loc.F("results.queens", results.QueensKilled));
            text.AppendLine(Loc.F("results.chitin", Mathf.FloorToInt(results.Chitin)));

            if (results.KillsByWeapon != null)
            {
                text.AppendLine();

                for (var i = 0; i < results.KillsByWeapon.Length; i++)
                {
                    if (results.KillsByWeapon[i] <= 0) continue;

                    var def = WeaponCatalog.Get((WeaponId)i);
                    text.AppendLine($"  {Loc.T(def.NameKey)}: {results.KillsByWeapon[i]}");
                }
            }

            _body.text = text.ToString();
            _extra.text = ExtraText != null ? ExtraText(results) : "";

            _again.SetLabel(Loc.T("results.again"));
            _camp.SetLabel(Loc.T("results.camp"));
            _camp.gameObject.SetActive(OnCamp != null);

            _doubleChitin.gameObject.SetActive(OnDoubleChitin != null && results.Chitin >= 1f && Platform.Current.RewardedAvailable);
            _doubleChitin.SetLabel(Loc.T("results.double"));

            SetOpen(true);
        }

        public void HideDouble() => _doubleChitin.gameObject.SetActive(false);
    }

    // ====================================================================== пауза

    public sealed class PauseScreen : ModalScreen
    {
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _controls;

        /// <summary>«В лагерь» и «Настройки» — мета (M4) подставляет свои.</summary>
        public System.Action OnCamp;
        public System.Action OnSettings;

        private Button _camp;
        private Button _settings;

        public PauseScreen(GameUi ui) : base(ui) { }

        public void Build(Transform parent)
        {
            Shade(parent, "Pause", 0.65f);

            _title = Title(Root, -160f);

            var resume = UiKit.Button(Root, "Resume", "", () => Ui.ResumeFromPause(), 320f, 60f);
            resume.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 40f), new Vector2(320f, 60f));
            resume.SetLabel(Loc.T("pause.resume"));

            _settings = UiKit.Button(Root, "Settings", "", () => OnSettings?.Invoke(), 320f, 60f);
            _settings.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -32f), new Vector2(320f, 60f));

            _camp = UiKit.Button(Root, "Camp", "", () => OnCamp?.Invoke(), 320f, 60f);
            _camp.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -104f), new Vector2(320f, 60f));

            _controls = UiKit.Text(Root, "Controls", 18f, UiKit.Dim, TextAlignmentOptions.Bottom);
            _controls.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(1100f, 120f));
        }

        public override void SetOpen(bool open)
        {
            base.SetOpen(open);
            if (!open || _title == null) return;

            _title.text = Loc.T("pause.title");
            _controls.text = Loc.T("pause.controls");
            _settings.SetLabel(Loc.T("pause.settings"));
            _camp.SetLabel(Loc.T("pause.camp"));

            _settings.gameObject.SetActive(OnSettings != null);
            _camp.gameObject.SetActive(OnCamp != null);
        }
    }

    // ====================================================================== подсказки

    /// <summary>
    /// Первые минуты без экранов обучения (CrazyGames: новый игрок — сразу в игре): подсказки
    /// по месту, по одной, гаснут после выполнения. «WASD — идти», «ЛКМ — огонь», «Shift — рывок»
    /// (при первом укусе), «Выбери улучшение» (на первом уровне), «Найди логово Матки» (на 1:30).
    /// Флаги «показано» — в профиле (M4): там же их и отмечают.
    /// </summary>
    public sealed class HintView
    {
        private readonly GameUi _ui;
        private TextMeshProUGUI _text;
        private Image _back;

        private string _current;
        private float _shown;
        private bool _bitten;

        /// <summary>Показана ли подсказка раньше — профиль (M4). Без него — в рамках сессии.</summary>
        public System.Func<string, bool> WasShown;
        public System.Action<string> MarkShown;

        private readonly HashSet<string> _session = new HashSet<string>();

        public HintView(GameUi ui) => _ui = ui;

        public void Build(Transform parent)
        {
            _back = UiKit.Panel(parent, "Hint", new Color(0f, 0f, 0f, 0.55f), UiKit.Rounded);
            _back.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 120f), new Vector2(560f, 44f));

            _text = UiKit.Text(_back.transform, "Text", 22f, UiKit.Paper, TextAlignmentOptions.Center, true);
            _text.rectTransform.Stretch(10f, 10f, 4f, 4f);

            _back.gameObject.SetActive(false);
        }

        public void OnBitten() => _bitten = true;

        private bool Seen(string key) => _session.Contains(key) || (WasShown != null && WasShown(key));

        private void Done(string key)
        {
            _session.Add(key);
            MarkShown?.Invoke(key);

            if (_current == key)
            {
                _current = null;
                _back.gameObject.SetActive(false);
            }
        }

        public void Tick(float deltaTime, GameState state)
        {
            var rig = _ui.Rig;
            var director = _ui.Director;
            var controller = _ui.Controller;

            if (rig == null || (state != GameState.Playing && state != GameState.LevelUp))
            {
                _back.gameObject.SetActive(false);
                return;
            }

            var commands = rig.Commands;

            // Выполнено — гаснет.
            if (_current == "hint.move" && commands.Move.sqrMagnitude > 0.1f) Done("hint.move");
            if (_current == "hint.fire" && commands.FirePressed && rig.IsLooking) Done("hint.fire");
            if (_current == "hint.dash" && commands.Dash) Done("hint.dash");
            if (_current == "hint.cards" && state == GameState.Playing && controller != null && !controller.Offering) Done("hint.cards");
            if (_current == "hint.lair" && _shown > 8f) Done("hint.lair");

            if (_current == null)
            {
                if (!Seen("hint.move")) Show("hint.move");
                else if (!Seen("hint.fire")) Show("hint.fire");
                else if (_bitten && !Seen("hint.dash")) Show("hint.dash");
                else if (controller != null && controller.Offering && !Seen("hint.cards")) Show("hint.cards");
                else if (director != null && director.Elapsed >= 90f && !Seen("hint.lair") &&
                         director.Phase == CaveRunDirector.RunPhase.Stage) Show("hint.lair");
            }

            if (_current != null) _shown += deltaTime;
        }

        private void Show(string key)
        {
            _current = key;
            _shown = 0f;

            // На таче — своя подпись: «ЛКМ — огонь» под пальцем ничего не значит (M5.5).
            _text.text = _ui.TouchMode && Loc.Has(key + ".touch") ? Loc.T(key + ".touch") : Loc.T(key);
            _back.gameObject.SetActive(true);
        }
    }
}
