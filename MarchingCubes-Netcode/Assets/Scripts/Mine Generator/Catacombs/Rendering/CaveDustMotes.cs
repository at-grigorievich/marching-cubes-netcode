using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Пылинки в воздухе вокруг игрока.
    ///
    /// Нужны потому, что до них в кадре не двигалось вообще ничего: статичный свет на
    /// статичном камне читается как декорация, а не как место, где ты находишься. Пыль
    /// даёт воздуху объём — особенно в конусе лампы, где она вспыхивает и гаснет.
    ///
    /// Система привязана к камере и эмитит в объёме вокруг неё, а не по всему уровню:
    /// частиц нужно несколько сотен вместо десятков тысяч, и это единственный вариант,
    /// который имеет смысл при цели WebGL. Симуляция в мировых координатах, иначе пыль
    /// едет вместе с игроком и выглядит приклеенной к экрану.
    /// </summary>
    [ExecuteAlways]
    public sealed class CaveDustMotes : MonoBehaviour
    {
        [Tooltip("Сколько пылинок держать в воздухе.")]
        [SerializeField, Range(0, 800)] private int motes = 420;

        [Tooltip("Радиус объёма вокруг камеры, в котором живёт пыль.")]
        [SerializeField, Min(2f)] private float radius = 9f;

        [Tooltip("Размер пылинки в юнитах.")]
        // Крупнее этого пылинки перестают быть пылью и читаются как летающие шарики.
        [SerializeField, Min(0.005f)] private float size = 0.018f;

        [SerializeField] private Color color = new Color(0.55f, 0.51f, 0.45f);

        private ParticleSystem _system;

        private void OnEnable()
        {
            Build();
        }

        private void OnDisable()
        {
            if (_system == null) return;

            if (Application.isPlaying) Destroy(_system.gameObject);
            else DestroyImmediate(_system.gameObject);

            _system = null;
        }

        private void Build()
        {
            if (_system != null) return;

            var host = new GameObject("Dust Motes");
            host.transform.SetParent(transform, false);

            // Пыль восстанавливается из настроек при каждом включении, в сцену её писать
            // незачем.
            if (!Application.isPlaying) host.hideFlags = HideFlags.DontSave;

            _system = host.AddComponent<ParticleSystem>();

            var main = _system.main;
            main.loop = true;
            main.playOnAwake = true;
            main.maxParticles = motes;
            main.startLifetime = 14f;
            main.startSpeed = 0.06f;
            main.startSize = size;
            main.startColor = color;
            main.gravityModifier = 0.004f;

            // Мировые координаты: иначе облако едет за игроком и читается как грязь
            // на объективе.
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = _system.emission;
            emission.rateOverTime = motes / main.startLifetime.constant;

            var shape = _system.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = radius;

            // Пылинка должна чуть заметно ползти, а не висеть намертво.
            var noise = _system.noise;
            noise.enabled = true;
            noise.strength = 0.12f;
            noise.frequency = 0.15f;
            noise.scrollSpeed = 0.05f;

            // Появляется и гаснет плавно: иначе на границе объёма пылинки мигают.
            var alpha = _system.colorOverLifetime;
            alpha.enabled = true;

            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f),
                    new GradientAlphaKey(1f, 0.8f), new GradientAlphaKey(0f, 1f)
                });

            alpha.color = new ParticleSystem.MinMaxGradient(gradient);

            var view = host.GetComponent<ParticleSystemRenderer>();
            view.renderMode = ParticleSystemRenderMode.Billboard;
            view.sharedMaterial = Material();
            view.shadowCastingMode = ShadowCastingMode.Off;
            view.receiveShadows = false;

            // Пылинка светится сама и не должна считаться источником для чего-либо.
            view.lightProbeUsage = LightProbeUsage.Off;
            view.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        private static Material _material;
        private static Texture2D _dot;

        private static Material Material()
        {
            if (_material != null) return _material;

            var shader = Shader.Find("Mobile/Particles/Additive")
                         ?? Shader.Find("Legacy Shaders/Particles/Additive")
                         ?? Shader.Find("Sprites/Default");

            _material = new Material(shader)
            {
                name = "Cave Dust",
                mainTexture = Dot(),
                hideFlags = HideFlags.HideAndDontSave
            };

            return _material;
        }

        /// <summary>
        /// Мягкая точка. Цвет запечён в текстуру: у Mobile/Particles/Additive из свойств
        /// есть только _MainTex, тинта он не знает и любой заданный цвет игнорирует.
        /// </summary>
        private static Texture2D Dot()
        {
            if (_dot != null) return _dot;

            const int size = 16;

            _dot = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Cave Dust Dot",
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color[size * size];

            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = (x + 0.5f) / size * 2f - 1f;
                var dy = (y + 0.5f) / size * 2f - 1f;

                var v = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                v *= v;

                pixels[y * size + x] = new Color(v, v, v, v);
            }

            _dot.SetPixels(pixels);
            _dot.Apply();

            return _dot;
        }
    }
}
