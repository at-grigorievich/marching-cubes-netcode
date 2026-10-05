using UnityEngine;
using UnityEngine.UI;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Значки особых пауков на краю экрана (M3.2): пока особый жив и не виден, у края в его
    /// сторону висит значок своего цвета. Vermintide 2 учит слушать; значок — для тех, кто
    /// играет без звука (а на вебе таких много), и для тача.
    ///
    /// Без аллокаций в кадре: значков четыре, они создаются при сборке и только двигаются.
    /// </summary>
    public sealed class SpecialMarkers
    {
        private const int Slots = 4;

        private static readonly Color[] Colors =
        {
            new Color(0.5f, 1f, 0.3f, 0.95f),   // плевун — кислотный
            new Color(0.75f, 0.6f, 1f, 0.95f),  // ловчий — сиреневый
            new Color(0.92f, 0.95f, 1f, 0.95f), // ткач — белёсый
            new Color(1f, 0.85f, 0.25f, 0.95f)  // бомбардир — жёлтый
        };

        private readonly GameUi _ui;
        private readonly RectTransform[] _icons = new RectTransform[Slots];
        private readonly Image[] _images = new Image[Slots];
        private readonly float[] _flash = new float[Slots];

        public SpecialMarkers(GameUi ui) => _ui = ui;

        /// <summary>Сколько значков сейчас видно — для UiCheck и прогона особых.</summary>
        public int Shown { get; private set; }

        public void Build(Transform parent)
        {
            for (var i = 0; i < Slots; i++)
            {
                _icons[i] = UiKit.Node(parent, "Special Marker " + i);
                _icons[i].sizeDelta = new Vector2(30f, 30f);

                _images[i] = _icons[i].gameObject.AddComponent<Image>();
                _images[i].sprite = UiKit.Arrow;
                _images[i].raycastTarget = false;

                _icons[i].gameObject.SetActive(false);
            }
        }

        /// <summary>Метка особого — значок вспыхивает.</summary>
        public void OnCue(SoloSpider special, SpecialCue cue)
        {
            var specials = _ui.Specials;
            if (specials == null) return;

            var slot = 0;

            for (var i = 0; i < specials.All.Count && slot < Slots; i++)
            {
                var other = specials.All[i];
                if (other.Dead) continue;

                if (other == special) _flash[slot] = 1f;
                slot++;
            }
        }

        public void Tick(float deltaTime)
        {
            var specials = _ui.Specials;
            var camera = _ui.Camera;
            Shown = 0;

            var slot = 0;

            if (specials != null && camera != null)
            {
                var all = specials.All;
                var canvas = (RectTransform)_icons[0].parent;
                var size = canvas.rect.size;

                for (var i = 0; i < all.Count && slot < Slots; i++)
                {
                    var special = all[i];
                    if (special.Dead) continue;

                    var icon = _icons[slot];
                    var image = _images[slot];
                    _flash[slot] = Mathf.Max(0f, _flash[slot] - deltaTime * 1.5f);

                    var centre = special.Centre;

                    if (specials.IsVisibleToPlayer(centre))
                    {
                        icon.gameObject.SetActive(false);
                        slot++;
                        continue;
                    }

                    var viewport = camera.WorldToViewportPoint(centre);
                    var direction = new Vector2(viewport.x - 0.5f, viewport.y - 0.5f);
                    if (viewport.z < 0f) direction = -direction;
                    if (direction.sqrMagnitude < 1e-4f) direction = Vector2.down;

                    var scale = Mathf.Max(Mathf.Max(Mathf.Abs(direction.x) / 0.46f, Mathf.Abs(direction.y) / 0.44f), 1e-3f);
                    var edge = direction / scale;

                    icon.anchoredPosition = new Vector2(edge.x * size.x, edge.y * size.y);
                    icon.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(-direction.x, direction.y) * Mathf.Rad2Deg);
                    icon.localScale = Vector3.one * (1f + _flash[slot] * 0.6f);

                    var color = Colors[(int)special.Type];
                    color.a = 0.65f + 0.35f * _flash[slot];
                    image.color = color;

                    icon.gameObject.SetActive(true);
                    Shown++;
                    slot++;
                }
            }

            for (; slot < Slots; slot++) _icons[slot].gameObject.SetActive(false);
        }
    }
}
