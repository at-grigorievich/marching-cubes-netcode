using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Паутина и коконы в узких местах.
    ///
    /// Отдельный компонент от CaveFixtures намеренно: тот про свет, этот про то, чей
    /// это дом. Общего у них только событие генерации.
    ///
    /// Паутина вешается ровно на щели, и это не украшение: щель — единственное место
    /// уровня, которое игрок обязан пройти вплотную, и именно там нужно сказать «здесь
    /// живут пауки» до того, как они выбегут. Заодно паутина работает ориентиром —
    /// щель с паутиной отличается от щели без неё, и уровень перестаёт быть одинаковым
    /// коричневым коридором.
    /// </summary>
    [ExecuteAlways]
    public sealed class CaveWebs : MonoBehaviour
    {
        [SerializeField] private CatacombWorld world;

        [Tooltip("Доля щелей, затянутых паутиной.")]
        [SerializeField, Range(0f, 1f)] private float webShare = 0.7f;

        [Tooltip("Сколько коконов вешать рядом с затянутой щелью.")]
        [SerializeField] private Vector2Int cocoonsPerWeb = new Vector2Int(0, 3);

        // Приглушённая и грязноватая: на полной яркости паутина читается как свежая
        // краска, а не как то, что висит тут годами.
        [SerializeField] private Color webColor = new Color(0.62f, 0.62f, 0.56f, 0.3f);
        [SerializeField] private Color cocoonColor = new Color(0.62f, 0.60f, 0.54f);

        private static Mesh _quad;
        private static Mesh _sphere;
        // Кэш по цвету, а не одна статическая ссылка. Ссылка без ключа — это ошибка,
        // на которой я уже попадался с ореолами: правишь цвет, а на экране остаётся
        // старый, потому что материал построен один раз и аргумент не смотрит.
        private static readonly Dictionary<Color, Material> WebMaterials = new Dictionary<Color, Material>();
        private static readonly Dictionary<Color, Material> CocoonMaterials = new Dictionary<Color, Material>();
        private static readonly Dictionary<Color, Texture2D> WebTextures = new Dictionary<Color, Texture2D>();

        private Transform _container;

        private void OnEnable()
        {
            if (world == null) world = GetComponent<CatacombWorld>();
            if (world == null) world = FindFirstObjectByType<CatacombWorld>();

            if (world != null) world.Generated += Rebuild;
        }

        private void OnDisable()
        {
            if (world != null) world.Generated -= Rebuild;
        }

        public void Rebuild()
        {
            Clear();

            if (world == null || world.Layout == null) return;

            var pinches = world.Layout.Pinches;
            if (pinches.Count == 0) return;

            var container = new GameObject("Cave Webs");
            container.transform.SetParent(transform, false);

            if (!Application.isPlaying) container.hideFlags = HideFlags.DontSave;
            _container = container.transform;

            var random = new System.Random(world.CurrentSeed ^ 0x7EB5);

            foreach (var pinch in pinches)
            {
                if (random.NextDouble() >= webShare) continue;

                SpawnWeb(pinch, random);
            }
        }

        public void Clear()
        {
            if (_container != null)
            {
                DestroyNow(_container.gameObject);
                _container = null;
            }

            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child.name == "Cave Webs") DestroyNow(child.gameObject);
            }
        }

        private void SpawnWeb(CatacombLayout.Pinch pinch, System.Random random)
        {
            var web = new GameObject("Web");
            web.transform.SetParent(_container, false);
            web.transform.position = pinch.Center + Vector3.up * 1.4f;

            // Плоскостью поперёк хода: паутина перегораживает проход, а не лежит вдоль него.
            web.transform.rotation = Quaternion.LookRotation(pinch.Direction);

            // Чуть крупнее самой щели, чтобы края уходили в породу, а не висели в воздухе
            // ровным кругом.
            var span = 4.2f + (float)random.NextDouble() * 1.2f;
            web.transform.localScale = new Vector3(span, span, 1f);

            web.AddComponent<MeshFilter>().sharedMesh = QuadMesh();

            var view = web.AddComponent<MeshRenderer>();
            view.sharedMaterial = WebMaterial(webColor);
            view.shadowCastingMode = ShadowCastingMode.Off;

            // Паутина тонкая и полупрозрачная: тени от неё выглядят как грязь, а приём
            // теней делает её чёрной тряпкой.
            view.receiveShadows = false;
            view.lightProbeUsage = LightProbeUsage.Off;

            var cocoons = random.Next(cocoonsPerWeb.x, cocoonsPerWeb.y + 1);

            for (var i = 0; i < cocoons; i++) SpawnCocoon(web.transform, random);
        }

        private void SpawnCocoon(Transform parent, System.Random random)
        {
            var cocoon = new GameObject("Cocoon");
            cocoon.transform.SetParent(parent, false);

            // В плоскости паутины, но ближе к краю: кокон висит на нитях, а не парит
            // в середине прохода, где игрок в него упрётся.
            var angle = (float)random.NextDouble() * Mathf.PI * 2f;
            var offset = 0.2f + (float)random.NextDouble() * 0.22f;

            cocoon.transform.localPosition = new Vector3(
                Mathf.Cos(angle) * offset, Mathf.Sin(angle) * offset, (float)(random.NextDouble() - 0.5) * 0.06f);

            // Масштаб родителя неравномерный и крупный — компенсируем, иначе кокон
            // растянет вместе с паутиной.
            var scale = 0.5f + (float)random.NextDouble() * 0.35f;
            var parentScale = parent.localScale;

            cocoon.transform.localScale = new Vector3(
                scale * 0.12f / Mathf.Max(0.001f, parentScale.x),
                scale * 0.2f / Mathf.Max(0.001f, parentScale.y),
                scale * 0.12f);

            cocoon.transform.localRotation = Quaternion.Euler(0f, 0f, (float)random.NextDouble() * 40f - 20f);

            cocoon.AddComponent<MeshFilter>().sharedMesh = SphereMesh();

            var view = cocoon.AddComponent<MeshRenderer>();

            // Кокон освещаемый, в отличие от паутины: у него есть объём, и именно светотень
            // отличает его от наклейки. Неосвещаемые мелкие предметы в этой сцене уже
            // пробовались в виде кристаллов и читались как пластик.
            view.sharedMaterial = CocoonMaterial(cocoonColor);
            view.shadowCastingMode = ShadowCastingMode.Off;
        }

        private static Mesh QuadMesh() => _quad != null ? _quad : _quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
        private static Mesh SphereMesh() => _sphere != null ? _sphere : _sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");

        private static Material WebMaterial(Color color)
        {
            Material cached;
            if (WebMaterials.TryGetValue(color, out cached) && cached != null) return cached;

            // Прозрачный неосвещаемый: паутина — это нити на просвет, а не поверхность.
            var material = CaveMaterials.AlphaBlended("Cave Web", WebTexture(color), Color.white);

            WebMaterials[color] = material;
            return material;
        }

        private static Material CocoonMaterial(Color color)
        {
            Material cached;
            if (CocoonMaterials.TryGetValue(color, out cached) && cached != null) return cached;

            // Кокон — единственное здесь, что должно быть освещаемым: ему нужен объём,
            // иначе он читается тем же пластиком, что и выброшенные кристаллы-осколки.
            // В URP это Lit, а гладкость называется _Smoothness, а не _Glossiness.
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"))
            {
                name = "Cave Cocoon",
                color = color,
                hideFlags = HideFlags.HideAndDontSave
            };

            material.SetFloat("_Smoothness", 0.15f);

            CocoonMaterials[color] = material;
            return material;
        }

        /// <summary>
        /// Паутина рисуется в текстуру: радиальные нити плюс кольца, с дырами.
        ///
        /// Геометрией это делать нельзя — мелкие примитивы у стены в этой сцене уже
        /// пробовались и читались как пластиковые палки. Нить толщиной в пиксель
        /// текстуры выглядит нитью, а не трубой.
        /// </summary>
        private static Texture2D WebTexture(Color color)
        {
            Texture2D cachedTexture;
            if (WebTextures.TryGetValue(color, out cachedTexture) && cachedTexture != null) return cachedTexture;

            const int size = 256;
            const int strands = 14;
            const int rings = 7;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "Cave Web",
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color[size * size];
            var random = new System.Random(4242);

            // Радиус каждой нити слегка гуляет, иначе сетка выглядит чертёжной.
            var ringRadius = new float[rings];
            for (var i = 0; i < rings; i++)
                ringRadius[i] = (i + 1) / (float)rings * (0.82f + (float)random.NextDouble() * 0.12f);

            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = (x + 0.5f) / size * 2f - 1f;
                var dy = (y + 0.5f) / size * 2f - 1f;

                var r = Mathf.Sqrt(dx * dx + dy * dy);
                var a = Mathf.Atan2(dy, dx);

                var value = 0f;

                if (r <= 1f)
                {
                    // Радиальные нити: расстояние до ближайшего луча по углу.
                    var step = Mathf.PI * 2f / strands;
                    var toRay = Mathf.Abs(Mathf.Repeat(a + Mathf.PI, step) - step * 0.5f);
                    var rayWidth = 0.004f / Mathf.Max(0.05f, r);

                    if (toRay < rayWidth) value = 1f;

                    // Кольца: провисающие дуги между лучами, поэтому радиус кольца слегка
                    // колеблется с угловой частотой числа нитей.
                    foreach (var radius in ringRadius)
                    {
                        var sag = radius * (1f - 0.035f * Mathf.Cos((a + Mathf.PI) * strands));
                        if (Mathf.Abs(r - sag) < 0.006f) value = 1f;
                    }

                    // Края расходятся, центр гуще.
                    value *= Mathf.SmoothStep(1f, 0.25f, r);
                }

                // Прорехи: целая сеть выглядит нарисованной.
                if (value > 0f && random.NextDouble() < 0.12) value = 0f;

                pixels[y * size + x] = new Color(color.r, color.g, color.b, value * color.a);
            }

            texture.SetPixels(pixels);
            texture.Apply();

            WebTextures[color] = texture;
            return texture;
        }

        private static void DestroyNow(Object target)
        {
            if (target == null) return;

            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
    }
}
