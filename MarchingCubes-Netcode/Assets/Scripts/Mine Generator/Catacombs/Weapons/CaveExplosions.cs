using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Взрывы гранат и брызги разорванных пауков: вспышка, огненный шар, дым, искры, угольки,
    /// раскалённые обломки породы, кольцо ударной волны и свет.
    ///
    /// Одна система частиц на каждый слой эффекта на весь мир, а не объект на взрыв: взрыв
    /// досыпает в неё частицы штучно (Emit), и шесть гранат подряд стоят столько же вызовов
    /// отрисовки, сколько одна. На WebGL, где всё доезжает на главном потоке, это важнее
    /// числа частиц. Свет — пул из нескольких источников: при Forward+ каждый источник
    /// в кадре стоит места в кластерах, а на WebGL2 их не больше 32 на камеру.
    ///
    /// Текстуры рисуются кодом, материалы — на своём шейдере Cave Particles, который
    /// приезжает ссылкой от гранатомёта: <c>Shader.Find</c> в сборке находит только шейдеры,
    /// на которые кто-то ссылается.
    /// </summary>
    public sealed class CaveExplosions : MonoBehaviour
    {
        private const int LightPool = 3;

        private ParticleSystem _flash;
        private ParticleSystem _fire;
        private ParticleSystem _smoke;
        private ParticleSystem _sparks;
        private ParticleSystem _embers;
        private ParticleSystem _debris;
        private ParticleSystem _ring;
        private ParticleSystem _ichor;
        private ParticleSystem _mist;

        private readonly List<FlashLight> _lights = new List<FlashLight>();
        private readonly List<Object> _owned = new List<Object>();

        private sealed class FlashLight
        {
            public Light Light;
            public float Age = 99f;
            public float Peak;
        }

        /// <summary>Создаёт все слои. Зовётся гранатомётом один раз, в play-режиме.</summary>
        public void Build(Shader particles)
        {
            var glow = MakeMaterial("Explosion Glow", particles, Paint("soft", SoftDot), BlendMode.SrcAlpha, BlendMode.One, 1f);
            var fire = MakeMaterial("Explosion Fire", particles, Paint("fire", FirePuff), BlendMode.SrcAlpha, BlendMode.One, 1f);
            var smoke = MakeMaterial("Explosion Smoke", particles, Paint("smoke", SmokePuff), BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, 0f);
            var ring = MakeMaterial("Explosion Ring", particles, Paint("ring", Ring), BlendMode.SrcAlpha, BlendMode.One, 1f);
            var rock = MakeMaterial("Explosion Debris", particles, null, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, 0f);
            var goo = MakeMaterial("Spider Ichor", particles, Paint("soft", SoftDot), BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, 0f);

            // Вспышка ядра: белая, огромная и мгновенная. Без неё взрыв начинается с огня,
            // а глаз читает удар по первому кадру.
            _flash = Layer("Flash", glow, 16, ParticleSystemRenderMode.Billboard);
            Fade(_flash, new Color(1f, 0.95f, 0.8f), new Color(1f, 0.7f, 0.35f));
            Grow(_flash, 0.6f, 1.3f);

            // Огненный шар: клубы, которые раздуваются, краснеют и гаснут.
            _fire = Layer("Fire", fire, 256, ParticleSystemRenderMode.Billboard);
            Gradient(_fire,
                (0f, new Color(1f, 0.92f, 0.65f), 1f),
                (0.2f, new Color(1f, 0.55f, 0.15f), 0.95f),
                (0.55f, new Color(0.65f, 0.16f, 0.04f), 0.55f),
                (1f, new Color(0.15f, 0.05f, 0.02f), 0f));
            Grow(_fire, 0.55f, 1.6f);
            Drag(_fire, 3.5f);
            Spin(_fire, 1.5f);

            // Дым: медленный, тяжёлый, заполняет коридор и тает. Туман гасит его в свой
            // цвет, а не в чёрное — дым и есть часть воздуха.
            _smoke = Layer("Smoke", smoke, 256, ParticleSystemRenderMode.Billboard);
            Gradient(_smoke,
                (0f, new Color(0.22f, 0.19f, 0.16f), 0f),
                (0.12f, new Color(0.17f, 0.15f, 0.13f), 0.7f),
                (0.6f, new Color(0.12f, 0.11f, 0.1f), 0.45f),
                (1f, new Color(0.09f, 0.09f, 0.09f), 0f));
            Grow(_smoke, 0.6f, 2.2f);
            Drag(_smoke, 1.6f);
            Spin(_smoke, 0.6f);

            // Искры: растянутые по скорости, отскакивают от стен.
            _sparks = Layer("Sparks", glow, 512, ParticleSystemRenderMode.Stretch);
            Gradient(_sparks,
                (0f, new Color(1f, 0.95f, 0.75f), 1f),
                (0.4f, new Color(1f, 0.6f, 0.2f), 1f),
                (1f, new Color(0.8f, 0.2f, 0.05f), 0f));
            Gravity(_sparks, 1.1f);
            Bounce(_sparks, 0.35f, 0.3f);
            var sparkRenderer = _sparks.GetComponent<ParticleSystemRenderer>();
            sparkRenderer.velocityScale = 0.045f;
            sparkRenderer.lengthScale = 1.5f;

            // Угольки: тлеющие точки, которые медленно оседают и мерцают яркостью.
            _embers = Layer("Embers", glow, 256, ParticleSystemRenderMode.Billboard);
            Gradient(_embers,
                (0f, new Color(1f, 0.75f, 0.3f), 1f),
                (0.5f, new Color(1f, 0.4f, 0.1f), 0.9f),
                (1f, new Color(0.6f, 0.12f, 0.03f), 0f));
            Gravity(_embers, 0.25f);
            Drag(_embers, 1.2f);

            // Обломки породы: раскалённые у взрыва, остывают до цвета камня, скачут по полу.
            _debris = Layer("Debris", rock, 160, ParticleSystemRenderMode.Mesh);
            _debris.GetComponent<ParticleSystemRenderer>().mesh = RockMesh();
            Gradient(_debris,
                (0f, new Color(1f, 0.55f, 0.2f), 1f),
                (0.18f, new Color(0.35f, 0.16f, 0.08f), 1f),
                (0.35f, new Color(0.14f, 0.12f, 0.1f), 1f),
                (0.85f, new Color(0.12f, 0.1f, 0.09f), 1f),
                (1f, new Color(0.12f, 0.1f, 0.09f), 0f));
            Gravity(_debris, 1.2f);
            Bounce(_debris, 0.3f, 0.45f);
            var debrisMain = _debris.main;
            debrisMain.startRotation3D = true;
            var debrisSpin = _debris.rotationOverLifetime;
            debrisSpin.enabled = true;
            debrisSpin.separateAxes = true;
            debrisSpin.x = new ParticleSystem.MinMaxCurve(-8f, 8f);
            debrisSpin.y = new ParticleSystem.MinMaxCurve(-8f, 8f);
            debrisSpin.z = new ParticleSystem.MinMaxCurve(-8f, 8f);

            // Кольцо ударной волны: четверть секунды, но именно оно даёт «удар», а не «пожар».
            _ring = Layer("Shockwave", ring, 16, ParticleSystemRenderMode.Billboard);
            Fade(_ring, new Color(1f, 0.85f, 0.6f), new Color(1f, 0.5f, 0.2f));
            Grow(_ring, 0.15f, 1f);

            // Брызги гемолимфы разорванных пауков: тёмно-оливковые капли по дуге и облачко.
            _ichor = Layer("Ichor", goo, 1024, ParticleSystemRenderMode.Stretch);
            Gradient(_ichor,
                (0f, new Color(0.42f, 0.5f, 0.1f), 1f),
                (0.7f, new Color(0.26f, 0.32f, 0.06f), 0.9f),
                (1f, new Color(0.18f, 0.22f, 0.04f), 0f));
            Gravity(_ichor, 1.4f);
            var ichorRenderer = _ichor.GetComponent<ParticleSystemRenderer>();
            ichorRenderer.velocityScale = 0.03f;
            ichorRenderer.lengthScale = 1.2f;

            _mist = Layer("Ichor Mist", goo, 256, ParticleSystemRenderMode.Billboard);
            Gradient(_mist,
                (0f, new Color(0.35f, 0.42f, 0.1f), 0.55f),
                (1f, new Color(0.2f, 0.25f, 0.06f), 0f));
            Grow(_mist, 0.5f, 1.6f);
            Drag(_mist, 3f);

            for (var i = 0; i < LightPool; i++)
            {
                var go = new GameObject("Blast Light") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(transform, false);

                var light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.shadows = LightShadows.None;
                light.intensity = 0f;
                light.enabled = false;

                _lights.Add(new FlashLight { Light = light });
            }
        }

        /// <summary>
        /// Взрыв в точке. Нормаль — поверхности, о которую ударилась граната: огонь и обломки
        /// летят от неё, а не в камень. Для взрыва в воздухе — любое направление (вверх).
        /// </summary>
        public void Explode(Vector3 point, Vector3 normal, float radius)
        {
            if (_fire == null) return;

            normal = normal.sqrMagnitude > 1e-4f ? normal.normalized : Vector3.up;

            var scale = radius / 5f;

            Emit(_flash, point + normal * 0.3f, Vector3.zero, 7f * scale, 0.14f, Color.white, 0f);
            Emit(_ring, point + normal * 0.2f, Vector3.zero, 11f * scale, 0.28f, Color.white, Random.Range(0f, 360f));

            for (var i = 0; i < 22; i++)
            {
                var direction = Hemisphere(normal, 0.35f);

                Emit(_fire, point + direction * Random.Range(0.1f, 1f) * scale,
                    direction * Random.Range(2.5f, 8f) * scale,
                    Random.Range(1.8f, 3.2f) * scale, Random.Range(0.45f, 0.8f), Color.white, Random.Range(0f, 360f));
            }

            // Язык огня вверх: в давке взрыв рвётся среди спин, и шар на уровне пола закрыт
            // пауками перед игроком — над кучей его видно.
            for (var i = 0; i < 7; i++)
            {
                var direction = (Vector3.up * 1.5f + normal + Random.insideUnitSphere * 0.5f).normalized;

                Emit(_fire, point + direction * Random.Range(0.3f, 1.2f) * scale,
                    direction * Random.Range(5f, 10f) * scale,
                    Random.Range(1.4f, 2.4f) * scale, Random.Range(0.5f, 0.85f), Color.white, Random.Range(0f, 360f));
            }

            for (var i = 0; i < 14; i++)
            {
                var direction = Hemisphere(normal, 0.2f);

                Emit(_smoke, point + direction * Random.Range(0.3f, 1.2f) * scale,
                    direction * Random.Range(1f, 3.2f) * scale + Vector3.up * 0.4f,
                    Random.Range(2f, 3.6f) * scale, Random.Range(2.2f, 3.4f), Color.white, Random.Range(0f, 360f));
            }

            for (var i = 0; i < 44; i++)
            {
                var direction = Hemisphere(normal, 0.05f);

                Emit(_sparks, point + normal * 0.1f, direction * Random.Range(8f, 22f),
                    Random.Range(0.05f, 0.11f), Random.Range(0.3f, 0.9f), Color.white, 0f);
            }

            for (var i = 0; i < 14; i++)
            {
                var direction = Hemisphere(normal, 0.2f);

                Emit(_embers, point + direction * Random.Range(0.2f, 1f), direction * Random.Range(1.5f, 5f),
                    Random.Range(0.06f, 0.13f), Random.Range(1.2f, 2.4f), Color.white, 0f);
            }

            for (var i = 0; i < 12; i++)
            {
                var direction = Hemisphere(normal, 0.1f);

                EmitRock(point + normal * 0.25f, direction * Random.Range(4f, 10f), Random.Range(0.07f, 0.2f),
                    Random.Range(1.6f, 2.6f));
            }

            Flash(point + normal * 0.8f, 70f, radius * 3.4f);
        }

        /// <summary>Брызги одного разорванного паука.</summary>
        public void Gore(Vector3 point, Vector3 away)
        {
            if (_ichor == null) return;

            var up = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.up;

            for (var i = 0; i < 7; i++)
            {
                var direction = Hemisphere(up, 0.25f);

                Emit(_ichor, point + direction * 0.2f, direction * Random.Range(3f, 7.5f),
                    Random.Range(0.12f, 0.24f), Random.Range(0.5f, 1f), Color.white, 0f);
            }

            Emit(_mist, point + up * 0.3f, up * 0.6f, Random.Range(0.9f, 1.4f), Random.Range(0.45f, 0.7f),
                Color.white, Random.Range(0f, 360f));
        }

        /// <summary>Вспышка у дула: короткий свет без частиц — частицы вспышки рисует сам гранатомёт.</summary>
        public void MuzzleLight(Vector3 point) => Flash(point, 18f, 7f);

        private void Update()
        {
            var dt = Time.deltaTime;

            foreach (var flash in _lights)
            {
                if (!flash.Light.enabled) continue;

                flash.Age += dt;

                // Быстро гаснет и краснеет: белая вспышка удара, потом отсвет огня.
                var k = Mathf.Exp(-flash.Age * 7f);

                flash.Light.intensity = flash.Peak * k;
                flash.Light.color = Color.Lerp(new Color(1f, 0.45f, 0.15f), new Color(1f, 0.88f, 0.65f), k);

                if (k < 0.01f) flash.Light.enabled = false;
            }
        }

        private void Flash(Vector3 point, float peak, float range)
        {
            if (_lights.Count == 0) return;

            // Самый старый источник из пула: при частой стрельбе гаснущая вспышка уступает
            // свежей, и источников в кадре никогда не больше трёх.
            var chosen = _lights[0];

            foreach (var flash in _lights)
            {
                if (flash.Age > chosen.Age) chosen = flash;
            }

            chosen.Age = 0f;
            chosen.Peak = peak;
            chosen.Light.transform.position = point;
            chosen.Light.range = range;
            chosen.Light.intensity = peak;
            chosen.Light.enabled = true;
        }

        private void OnDestroy()
        {
            foreach (var owned in _owned)
            {
                if (owned != null) Destroy(owned);
            }
        }

        // ----------------------------------------------------------------- частицы

        private static void Emit(ParticleSystem system, Vector3 position, Vector3 velocity, float size, float lifetime,
            Color color, float rotation)
        {
            var emit = new ParticleSystem.EmitParams
            {
                position = position,
                velocity = velocity,
                startSize = size,
                startLifetime = lifetime,
                startColor = color,
                rotation = rotation,
                applyShapeToPosition = false
            };

            system.Emit(emit, 1);
        }

        private void EmitRock(Vector3 position, Vector3 velocity, float size, float lifetime)
        {
            var emit = new ParticleSystem.EmitParams
            {
                position = position,
                velocity = velocity,
                startSize = size,
                startLifetime = lifetime,
                startColor = Color.white,
                rotation3D = new Vector3(Random.Range(0f, 360f), Random.Range(0f, 360f), Random.Range(0f, 360f)),
                applyShapeToPosition = false
            };

            _debris.Emit(emit, 1);
        }

        /// <summary>Случайное направление в полусфере вокруг оси; spread — насколько можно уйти вбок.</summary>
        private static Vector3 Hemisphere(Vector3 axis, float spread)
        {
            var random = Random.onUnitSphere;

            if (Vector3.Dot(random, axis) < 0f) random = -random;

            return (random + axis * (1f - spread)).normalized;
        }

        private ParticleSystem Layer(string name, Material material, int max, ParticleSystemRenderMode mode)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);

            var system = go.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = system.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = max;
            main.startSpeed = 0f;
            main.scalingMode = ParticleSystemScalingMode.Shape;

            var emission = system.emission;
            emission.enabled = false;

            var shape = system.shape;
            shape.enabled = false;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.material = material;
            renderer.renderMode = mode;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortMode = ParticleSystemSortMode.Distance;

            // Играет всегда: излучение выключено, частицы досыпает Emit, а симуляция идёт.
            system.Play();

            return system;
        }

        private static void Gradient(ParticleSystem system, params (float time, Color color, float alpha)[] keys)
        {
            var colors = new GradientColorKey[keys.Length];
            var alphas = new GradientAlphaKey[keys.Length];

            for (var i = 0; i < keys.Length; i++)
            {
                colors[i] = new GradientColorKey(keys[i].color, keys[i].time);
                alphas[i] = new GradientAlphaKey(keys[i].alpha, keys[i].time);
            }

            var gradient = new Gradient();
            gradient.SetKeys(colors, alphas);

            var module = system.colorOverLifetime;
            module.enabled = true;
            module.color = gradient;
        }

        private static void Fade(ParticleSystem system, Color from, Color to) =>
            Gradient(system, (0f, from, 1f), (1f, to, 0f));

        private static void Grow(ParticleSystem system, float from, float to)
        {
            var module = system.sizeOverLifetime;
            module.enabled = true;
            module.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, from, 1f, to));
        }

        private static void Drag(ParticleSystem system, float drag)
        {
            var module = system.limitVelocityOverLifetime;
            module.enabled = true;
            module.drag = drag;
            module.limit = 1000f;
        }

        private static void Spin(ParticleSystem system, float radians)
        {
            var module = system.rotationOverLifetime;
            module.enabled = true;
            module.z = new ParticleSystem.MinMaxCurve(-radians, radians);
        }

        private static void Gravity(ParticleSystem system, float modifier)
        {
            var main = system.main;
            main.gravityModifier = modifier;
        }

        /// <summary>
        /// Отскок от стен. Физикой частиц, а не своей: коллайдеры есть на всех поверхностях
        /// ходов, а частица начинает путь в пустоте — грабли №1 про лучи из толщи камня
        /// здесь не про неё.
        /// </summary>
        private static void Bounce(ParticleSystem system, float bounce, float dampen)
        {
            var module = system.collision;
            module.enabled = true;
            module.type = ParticleSystemCollisionType.World;
            module.mode = ParticleSystemCollisionMode.Collision3D;
            module.quality = ParticleSystemCollisionQuality.Medium;
            module.bounce = bounce;
            module.dampen = dampen;
            module.lifetimeLoss = 0f;
            module.radiusScale = 0.5f;
            module.enableDynamicColliders = false;
        }

        // ----------------------------------------------------------------- материалы и текстуры

        private Material MakeMaterial(string name, Shader shader, Texture2D texture, BlendMode src, BlendMode dst, float fogToBlack)
        {
            var material = new Material(shader) { name = name, hideFlags = HideFlags.DontSave };

            if (texture != null) material.mainTexture = texture;

            material.SetFloat("_SrcBlend", (float)src);
            material.SetFloat("_DstBlend", (float)dst);
            material.SetFloat("_FogToBlack", fogToBlack);
            material.renderQueue = (int)RenderQueue.Transparent;

            _owned.Add(material);
            return material;
        }

        private Texture2D Paint(string name, System.Func<float, float, float, Color> paint)
        {
            const int size = 64;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "Explosion " + name,
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
            texture.Apply(true, true);

            _owned.Add(texture);
            return texture;
        }

        private Mesh RockMesh()
        {
            var kit = new MeshKit(1);
            kit.Rock(0, Vector3.zero, 0.5f, 7);

            var mesh = kit.Build("Debris Rock");
            _owned.Add(mesh);

            return mesh;
        }

        private static Color SoftDot(float u, float v, float r)
        {
            var a = Mathf.Clamp01(1f - r);
            a *= a;

            return new Color(1f, 1f, 1f, a);
        }

        /// <summary>Клуб огня: мягкий край, рваная кромка, светлая середина.</summary>
        private static Color FirePuff(float u, float v, float r)
        {
            var noise = Mathf.PerlinNoise(u * 3.1f + 11.3f, v * 3.1f + 4.7f) * 0.6f +
                        Mathf.PerlinNoise(u * 7.3f + 2.1f, v * 7.3f + 9.9f) * 0.4f;

            var a = Mathf.Clamp01((1f - r) * (0.55f + noise * 0.9f));
            a = Mathf.SmoothStep(0f, 1f, a);

            var core = Mathf.Clamp01(1f - r * 1.6f);

            return new Color(1f, 0.85f + core * 0.15f, 0.7f + core * 0.3f, a);
        }

        /// <summary>Клуб дыма: мягкий, с прожилками, без чёткого края.</summary>
        private static Color SmokePuff(float u, float v, float r)
        {
            var noise = Mathf.PerlinNoise(u * 2.3f + 31.7f, v * 2.3f + 12.9f) * 0.55f +
                        Mathf.PerlinNoise(u * 5.9f + 8.3f, v * 5.9f + 27.1f) * 0.3f +
                        Mathf.PerlinNoise(u * 11.7f + 3.3f, v * 11.7f + 1.9f) * 0.15f;

            var a = Mathf.Clamp01(1f - r) * Mathf.Clamp01(noise * 1.6f - 0.15f);

            return new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, a));
        }

        private static Color Ring(float u, float v, float r)
        {
            var band = Mathf.Exp(-Mathf.Pow((r - 0.78f) / 0.09f, 2f));
            var inner = Mathf.Clamp01(1f - r) * 0.15f;

            return new Color(1f, 1f, 1f, Mathf.Clamp01(band + inner) * (r < 1f ? 1f : 0f));
        }
    }
}
