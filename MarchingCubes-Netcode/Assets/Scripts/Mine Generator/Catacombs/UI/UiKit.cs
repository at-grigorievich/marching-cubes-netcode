using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Интерфейс из кода: канвасы, панели, тексты, кнопки, процедурные спрайты.
    ///
    /// Без префабов, как и всё в проекте: у кодового интерфейса нет ни импорта, ни .prefab
    /// в репозитории, а его числа правятся рядом с объяснением. UGUI + TextMeshPro, а не UI
    /// Toolkit (решение плана №7): предсказуемее на WebGL и мобильных браузерах.
    ///
    /// Масштаб — от 1280×720 с половинным весом ширины и высоты: на телефоне в альбомной
    /// ориентации и на широком мониторе пропорции элементов держатся одинаково.
    /// </summary>
    public static class UiKit
    {
        public static readonly Vector2 Reference = new Vector2(1280f, 720f);

        public static readonly Color Ink = new Color(0.04f, 0.035f, 0.03f, 0.82f);
        public static readonly Color Paper = new Color(0.93f, 0.86f, 0.72f, 1f);
        public static readonly Color Dim = new Color(0.62f, 0.56f, 0.47f, 1f);
        public static readonly Color Amber = new Color(1f, 0.66f, 0.2f, 1f);
        public static readonly Color Blood = new Color(0.85f, 0.12f, 0.08f, 1f);
        public static readonly Color Teal = new Color(0.45f, 0.85f, 0.75f, 1f);

        private static TMP_FontAsset _regular;
        private static TMP_FontAsset _bold;
        private static Sprite _white;
        private static Sprite _circle;
        private static Sprite _ring;
        private static Sprite _rounded;
        private static Sprite _arrow;
        private static Sprite _arc;
        private static Sprite _vignette;

        public static TMP_FontAsset Regular => _regular ??= Resources.Load<TMP_FontAsset>("Fonts/Roboto SDF") ?? TMP_Settings.defaultFontAsset;
        public static TMP_FontAsset Bold => _bold ??= Resources.Load<TMP_FontAsset>("Fonts/Roboto Bold SDF") ?? Regular;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            _regular = null;
            _bold = null;
            _white = _circle = _ring = _rounded = _arrow = _arc = _vignette = null;
        }

        // ------------------------------------------------------------------ канвас

        public static Canvas Canvas(string name, int order, Transform parent = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            if (parent != null) go.transform.SetParent(parent, false);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = order;
            canvas.pixelPerfect = false;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = Reference;
            scaler.matchWidthOrHeight = 0.5f;

            go.AddComponent<GraphicRaycaster>();

            return canvas;
        }

        /// <summary>EventSystem со старым модулем ввода — кнопки работают и мышью, и касанием.</summary>
        public static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindAnyObjectByType<EventSystem>() != null) return;

            var go = new GameObject("Event System", typeof(EventSystem), typeof(StandaloneInputModule));

            // В режиме редактирования (UiCheck) DontDestroyOnLoad бросает исключение; там
            // достаточно не писать объект в сцену.
            if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(go);
            else go.hideFlags = HideFlags.DontSave;
        }

        // ------------------------------------------------------------------ узлы

        public static RectTransform Node(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            return (RectTransform)go.transform;
        }

        /// <summary>Привязка: якоря, опорная точка, позиция и размер в единицах 1280×720.</summary>
        public static RectTransform Place(this RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            return rect;
        }

        /// <summary>Растянуть на родителя с отступами.</summary>
        public static RectTransform Stretch(this RectTransform rect, float left = 0f, float right = 0f, float top = 0f, float bottom = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);

            return rect;
        }

        public static Image Panel(Transform parent, string name, Color color, Sprite sprite = null, bool raycast = false)
        {
            var rect = Node(parent, name);
            var image = rect.gameObject.AddComponent<Image>();

            image.sprite = sprite ?? White;
            image.color = color;
            image.raycastTarget = raycast;

            if (sprite == Rounded) image.type = Image.Type.Sliced;

            return image;
        }

        /// <summary>Полоса: фон и заливка слева направо (Image.Filled).</summary>
        public static Image Bar(Transform parent, string name, Color back, Color fill, out Image background)
        {
            background = Panel(parent, name, back);

            var fillImage = Panel(background.transform, "Fill", fill);
            fillImage.rectTransform.Stretch(2f, 2f, 2f, 2f);
            fillImage.type = Image.Type.Filled;
            fillImage.fillMethod = Image.FillMethod.Horizontal;
            fillImage.fillOrigin = 0;
            fillImage.fillAmount = 1f;

            return fillImage;
        }

        public static TextMeshProUGUI Text(Transform parent, string name, float size, Color color,
            TextAlignmentOptions alignment = TextAlignmentOptions.Center, bool bold = false)
        {
            var rect = Node(parent, name);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();

            text.font = bold ? Bold : Regular;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.richText = true;
            text.overflowMode = TextOverflowModes.Overflow;

            return text;
        }

        public static Button Button(Transform parent, string name, string label, Action onClick, float width = 260f,
            float height = 56f, Color? color = null)
        {
            var image = Panel(parent, name, color ?? new Color(0.16f, 0.12f, 0.09f, 0.95f), Rounded, true);
            image.rectTransform.sizeDelta = new Vector2(width, height);

            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;

            var colors = button.colors;
            colors.highlightedColor = new Color(1.25f, 1.15f, 1f, 1f);
            colors.pressedColor = new Color(0.8f, 0.75f, 0.7f, 1f);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.6f);
            button.colors = colors;

            var text = Text(image.transform, "Label", 22f, Paper, TextAlignmentOptions.Center, true);
            text.rectTransform.Stretch(8f, 8f, 4f, 4f);
            text.text = label;

            button.onClick.AddListener(() => onClick?.Invoke());

            return button;
        }

        public static void SetLabel(this Button button, string label)
        {
            var text = button.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) text.text = label;
        }

        // ------------------------------------------------------------------ спрайты

        public static Sprite White => _white ??= Make("White", 4, (u, v, r) => Color.white, Vector4.zero);

        public static Sprite Circle => _circle ??= Make("Circle", 64, (u, v, r) =>
            new Color(1f, 1f, 1f, Mathf.Clamp01((1f - r) * 24f)), Vector4.zero);

        public static Sprite Ring => _ring ??= Make("Ring", 64, (u, v, r) =>
            new Color(1f, 1f, 1f, Mathf.Clamp01((1f - r) * 24f) * Mathf.Clamp01((r - 0.78f) * 24f)), Vector4.zero);

        /// <summary>Скруглённый прямоугольник — фон кнопок и карточек (девять частей).</summary>
        public static Sprite Rounded => _rounded ??= Make("Rounded", 32, (u, v, r) =>
        {
            var x = Mathf.Max(0f, Mathf.Abs(u) - 0.5f) / 0.5f;
            var y = Mathf.Max(0f, Mathf.Abs(v) - 0.5f) / 0.5f;
            var d = Mathf.Sqrt(x * x + y * y);

            return new Color(1f, 1f, 1f, Mathf.Clamp01((1f - d) * 8f));
        }, new Vector4(10f, 10f, 10f, 10f));

        /// <summary>Стрелка-указатель вверх.</summary>
        public static Sprite Arrow => _arrow ??= Make("Arrow", 64, (u, v, r) =>
        {
            var y = (v + 1f) * 0.5f;
            var half = (1f - y) * 0.8f;
            var inside = Mathf.Abs(u) < half && y > 0.05f;

            return new Color(1f, 1f, 1f, inside ? 1f : 0f);
        }, Vector4.zero);

        /// <summary>Дуга направления урона: кусок кольца сверху.</summary>
        public static Sprite Arc => _arc ??= Make("Arc", 128, (u, v, r) =>
        {
            var band = Mathf.Clamp01(1f - Mathf.Abs(r - 0.85f) / 0.1f);
            var angle = Mathf.Abs(Mathf.Atan2(u, v));
            var span = Mathf.Clamp01(1f - angle / 0.55f);

            return new Color(1f, 1f, 1f, band * span * span);
        }, Vector4.zero);

        /// <summary>Виньетка: прозрачный центр, плотные края.</summary>
        public static Sprite Vignette => _vignette ??= Make("Vignette", 128, (u, v, r) =>
        {
            var edge = Mathf.Clamp01((Mathf.Max(Mathf.Abs(u), Mathf.Abs(v)) * 0.6f + r * 0.5f - 0.55f) / 0.5f);
            return new Color(1f, 1f, 1f, edge * edge);
        }, Vector4.zero);

        private static Sprite Make(string name, int size, Func<float, float, float, Color> paint, Vector4 border)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };

            var pixels = new Color[size * size];

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var u = (x + 0.5f) / size * 2f - 1f;
                    var v = (y + 0.5f) / size * 2f - 1f;

                    pixels[y * size + x] = paint(u, v, Mathf.Sqrt(u * u + v * v));
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, true);

            var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, border);

            sprite.name = name;
            sprite.hideFlags = HideFlags.DontSave;

            return sprite;
        }

        public static string Clock(float seconds)
        {
            seconds = Mathf.Max(0f, seconds);
            return $"{(int)(seconds / 60f):00}:{(int)(seconds % 60f):00}";
        }
    }

    /// <summary>
    /// Держит детей внутри безопасной зоны экрана (вырезы телефонов, приложение CrazyGames):
    /// пересчёт только когда зона поменялась.
    /// </summary>
    public sealed class SafeArea : MonoBehaviour
    {
        private Rect _applied;
        private Vector2Int _screen;

        private void LateUpdate()
        {
            var area = Screen.safeArea;
            var screen = new Vector2Int(Screen.width, Screen.height);

            if (area == _applied && screen == _screen) return;

            _applied = area;
            _screen = screen;

            if (screen.x <= 0 || screen.y <= 0) return;

            var rect = (RectTransform)transform;
            rect.anchorMin = new Vector2(area.xMin / screen.x, area.yMin / screen.y);
            rect.anchorMax = new Vector2(area.xMax / screen.x, area.yMax / screen.y);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
