using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Сигнальная шашка — единственный источник в сцене, который стоит в мире, а не на камере.
    ///
    /// Зачем она вообще. Фонарь висит у глаза, поэтому N*L почти совпадает с N*V: что бы он
    /// ни освещал, это по определению обращено к зрителю, светотень получается почти постоянной,
    /// и форму породы такой свет не лепит в принципе. Замер это подтверждает — фонарь даёт 62%
    /// яркости кадра, а среднеквадратичный разброс яркости при этом всего 0.06–0.09, то есть
    /// картинка плоская. Шашка лежит на полу сбоку: свет идёт поперёк взгляда, тени ложатся
    /// вверх по стенам, и стена наконец получает градиент вместо ровной заливки.
    ///
    /// Живых шашек не больше <see cref="MaxLive"/>. В Built-in RP число попиксельных источников
    /// ограничено настройкой качества (на Ultra это 4), и одно место занято фонарём. Лишняя
    /// шашка не была бы ошибкой — она молча уехала бы в вершинные источники, где нет теней,
    /// и разница читалась бы как «иногда тени есть, иногда нет» без видимой причины.
    /// </summary>
    public sealed class CaveFlare : MonoBehaviour
    {
        public const int MaxLive = 3;

        private static readonly List<CaveFlare> LiveFlares = new List<CaveFlare>();

        // Аддитивный ореол складывается с уже освещённой породой, поэтому яркий цвет
        // упирается в единицу по всем трём каналам и белеет. Насыщенный и приглушённый
        // остаётся оранжевым даже поверх светлого пола.
        private static readonly Color HaloColor = new Color(1f, 0.45f, 0.16f);

        private static Mesh _sphere;
        private static Material _glowMaterial;
        private static PhysicsMaterial _bounceMaterial;

        [Tooltip("Сколько секунд шашка горит на полной яркости.")]
        [SerializeField] private float burnSeconds = 75f;

        [Tooltip("За сколько секунд до конца шашка начинает гаснуть.")]
        [SerializeField] private float fadeSeconds = 8f;

        private Light _light;
        private Renderer _glow;
        private Renderer _halo;
        private float _haloScale;
        private float _glowScale;
        private float _baseIntensity;
        private Color _baseColor;
        private float _age;
        private float _flickerSeed;

        public static IReadOnlyList<CaveFlare> Live => LiveFlares;

        /// <summary>
        /// Бросает шашку. Возвращает её, чтобы вызывающий мог, например, доложить о броске.
        /// </summary>
        public static CaveFlare Throw(Vector3 origin, Vector3 velocity)
        {
            // Самую старую гасим до создания новой, а не после: иначе на один кадр живых
            // становится MaxLive + 1, и попиксельный бюджет успевает выкинуть чужую шашку.
            while (LiveFlares.Count >= MaxLive)
            {
                var oldest = LiveFlares[0];
                LiveFlares.RemoveAt(0);

                if (oldest != null) Destroy(oldest.gameObject);
            }

            var root = new GameObject("Cave Flare");
            root.transform.position = origin;

            // Корень не масштабируем: у него коллайдер и физика, а вложенный шарик рисуется
            // своим масштабом. Масштабировать корень значило бы масштабировать и коллайдер.
            var body = root.AddComponent<Rigidbody>();
            body.mass = 1.2f;
            body.linearDamping = 0.1f;
            body.angularDamping = 1.4f;

            // Шашка летит быстро и мелкая; на дискретной проверке она проскакивает сквозь
            // стену тонкого хода и улетает в породу, где её свет уже никому не виден.
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.linearVelocity = velocity;

            var shape = root.AddComponent<SphereCollider>();
            shape.radius = 0.16f;
            // sharedMaterial, а не material: второе создаёт копию физматериала на каждую шашку.
            shape.sharedMaterial = BounceMaterial();

            var lightObject = new GameObject("Light");
            lightObject.transform.SetParent(root.transform, false);

            // Приподнят над центром: лежащая шашка наполовину утоплена в неровности пола,
            // и источник ровно в её центре наполовину светил бы в породу.
            lightObject.transform.localPosition = new Vector3(0f, 0.25f, 0f);

            var glowObject = new GameObject("Glow");

            // Ядро висит ровно там же, где источник: разнесённые на четверть метра шарик
            // и ореол читались как две разные штуки — огонёк и висящее над ним пятно.
            glowObject.transform.SetParent(lightObject.transform, false);

            // Ядро мелкое намеренно: видимый размер шашке даёт ореол, а не сам шарик.
            // Шарик покрупнее читался как белая таблетка с резкой кромкой — предмет,
            // а не огонь.
            glowObject.transform.localScale = Vector3.one * 0.12f;

            // Меш встроенный, а не CreatePrimitive: у примитива есть коллайдер, форму держит
            // сфера на корне, а второй коллайдер под тем же Rigidbody только мешает.
            glowObject.AddComponent<MeshFilter>().sharedMesh = SphereMesh();

            var glow = glowObject.AddComponent<MeshRenderer>();
            glow.sharedMaterial = GlowMaterial();

            // Шашка сидит внутри собственного источника, и её тень падала бы сама на себя:
            // яркий шар с чёрным ободком. Приёма теней ей тоже не нужно — она неосвещаемая.
            glow.shadowCastingMode = ShadowCastingMode.Off;
            glow.receiveShadows = false;

            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Point;

            // Дальность короткая намеренно: шашка должна освещать карман вокруг себя,
            // а не весь ход. При 20 юнитах в коридоре шириной четыре две шашки заливали
            // кадр целиком, и вместо островов света получался ровный оранжевый суп —
            // ровно тот дефект, от которого всё это и лечится.
            light.range = 9f;
            light.intensity = 2.6f;

            // Тёплый, почти красный — против холодного ambient и холодных жил. Тени и форму
            // в кадре делает именно контраст двух температур, а не абсолютная яркость.
            light.color = new Color(1f, 0.52f, 0.22f);

            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.8f;

            // Те же смещения, что у фонаря: нормали породы берутся из градиента поля плотности
            // и на гранях вокселя гуляют, поэтому обычного bias не хватает — остаются полосы
            // самозатенения. normalBias двигает выборку вдоль нормали и убирает их.
            light.shadowBias = 0.05f;
            light.shadowNormalBias = 0.5f;
            light.shadowNearPlane = 0.1f;

            // Важнее фонаря: если попиксельных мест не хватит, выкинуть должно фонарь,
            // который всё равно не даёт светотени, а не шашку, ради которой всё затевалось.
            light.renderMode = LightRenderMode.ForcePixel;

            // Ореол вдвое шире, чем кажется нужным: у настоящего источника свечение
            // заметно больше самого огня, и именно по нему глаз опознаёт свет.
            var halo = CaveGlowBillboard.Attach(lightObject.transform, 2.4f, HaloColor);

            var flare = root.AddComponent<CaveFlare>();
            flare._halo = halo.GetComponent<Renderer>();
            flare._haloScale = halo.transform.localScale.x;
            flare._glowScale = glowObject.transform.localScale.x;
            flare._light = light;
            flare._glow = glow;
            flare._baseIntensity = light.intensity;
            flare._baseColor = glow.sharedMaterial.color;
            flare._flickerSeed = Random.value * 100f;

            LiveFlares.Add(flare);
            return flare;
        }

        /// <summary>
        /// Гасит все шашки. Нужно при перегенерации уровня: старые остались бы висеть
        /// в воздухе там, где породы больше нет, или оказались бы замурованы в новой.
        /// </summary>
        public static void ClearAll()
        {
            foreach (var flare in LiveFlares)
            {
                if (flare != null) Destroy(flare.gameObject);
            }

            LiveFlares.Clear();
        }

        private void OnDestroy()
        {
            LiveFlares.Remove(this);
        }

        private void Update()
        {
            _age += Time.deltaTime;

            if (_age >= burnSeconds)
            {
                Destroy(gameObject);
                return;
            }

            // Мерцание. Не синус: ровное дыхание читается как баг освещения, а не как огонь.
            // Перлин на двух частотах даёт неровный, но непрерывный сигнал.
            var flicker = 0.86f
                          + 0.10f * Mathf.PerlinNoise(_flickerSeed, Time.time * 6.5f)
                          + 0.04f * Mathf.PerlinNoise(_flickerSeed + 17f, Time.time * 19f);

            // Затухание к концу горения — и у света, и у самого шарика, иначе яркая точка
            // осталась бы висеть в темноте после того, как светить перестала.
            var left = burnSeconds - _age;
            var fade = fadeSeconds > 0f ? Mathf.Clamp01(left / fadeSeconds) : 1f;

            _light.intensity = _baseIntensity * flicker * fade;

            var visible = Mathf.Lerp(0.35f, 1f, flicker * fade);

            // Ядро гаснет размером, а не цветом. Материал ядра общий на все шашки, и
            // renderer.material создал бы его копию на каждый вызов — а вызов тут каждый
            // кадр. Ровно на этом Unity ругается «leak materials into the scene».
            if (_glow != null) _glow.transform.localScale = Vector3.one * (_glowScale * visible);

            // Ореол гаснет вместе со светом размером, а не цветом: материал ореола общий,
            // renderer.material создал бы его копию на каждый вызов, а цвет у шейдера
            // Mobile/Particles/Additive менять всё равно нечем — он запечён в текстуру.
            if (_halo != null) _halo.transform.localScale = Vector3.one * (_haloScale * visible);
        }

        private static Mesh SphereMesh()
        {
            return _sphere != null ? _sphere : _sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
        }

        private static Material GlowMaterial()
        {
            if (_glowMaterial != null) return _glowMaterial;

            // Unlit: шарик должен светиться сам, а не быть освещённым собственным источником.
            _glowMaterial = new Material(Shader.Find("Unlit/Color"))
            {
                name = "Cave Flare Glow",
                color = new Color(1f, 0.78f, 0.45f),
                hideFlags = HideFlags.HideAndDontSave
            };

            return _glowMaterial;
        }

        private static PhysicsMaterial BounceMaterial()
        {
            if (_bounceMaterial != null) return _bounceMaterial;

            // Шашка должна улечься там, куда её бросили, а не укатиться за поворот: один
            // короткий отскок и высокое трение. Свет, уехавший из виду, бесполезен.
            _bounceMaterial = new PhysicsMaterial("Cave Flare Bounce")
            {
                bounciness = 0.18f,
                dynamicFriction = 0.85f,
                staticFriction = 0.9f,
                hideFlags = HideFlags.HideAndDontSave
            };

            return _bounceMaterial;
        }
    }
}
