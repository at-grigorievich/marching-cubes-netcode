using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Цвет дали и заливки по этажу, на котором сейчас игрок.
    ///
    /// Зачем. Замер по кадрам пробежки: схожесть цветовых гистограмм между произвольными
    /// восемью точками маршрута — 0.65, холодных пикселей везде 3–7 процентов. То есть
    /// весь уровень выглядит одним и тем же коричневым коридором, и ориентироваться
    /// в нём не по чему. Цвет светильников уже разведён по этажам в CaveFixtures; здесь
    /// то же самое делается с туманом и ambient — они глобальные, поэтому меняются
    /// не по месту, а по тому, куда спустился игрок.
    ///
    /// В редакторе само не работает намеренно: RenderSettings — состояние сцены, и правка
    /// их каждый кадр в режиме редактирования пачкает сцену и рискует сохранить чужие
    /// значения поверх эталонных. Для съёмки кадров есть <see cref="ApplyFor"/>.
    /// </summary>
    [ExecuteAlways]
    public sealed class CaveLevelMood : MonoBehaviour
    {
        [SerializeField] private CatacombWorld world;

        [Tooltip("За кем следим. Пусто — возьмём основную камеру.")]
        [SerializeField] private Transform viewer;

        [Tooltip("Насколько быстро меняется настроение при переходе между этажами.")]
        [SerializeField, Min(0.01f)] private float blendSpeed = 0.6f;

        [Tooltip("Сила подкраски тумана цветом этажа. 1 — туман целиком в цвете жил этажа.")]
        [SerializeField, Range(0f, 1f)] private float fogTint = 0.75f;

        [Tooltip("Сила подкраски ambient.")]
        [SerializeField, Range(0f, 1f)] private float ambientTint = 0.6f;

        private readonly List<float> _levelHeight = new List<float>();

        private Color _baseFog;
        private Color _baseSky;
        private Color _baseEquator;
        private Color _baseGround;
        private bool _captured;

        private float _current = -1f;

        private void OnEnable()
        {
            if (world == null) world = GetComponent<CatacombWorld>();
            if (world == null) world = FindFirstObjectByType<CatacombWorld>();

            Capture();

            if (world != null) world.Generated += Rebuild;
            Rebuild();
        }

        private void OnDisable()
        {
            if (world != null) world.Generated -= Rebuild;

            Restore();
        }

        /// <summary>Запоминает эталонные значения, чтобы было к чему возвращаться.</summary>
        private void Capture()
        {
            if (_captured) return;

            _baseFog = RenderSettings.fogColor;
            _baseSky = RenderSettings.ambientSkyColor;
            _baseEquator = RenderSettings.ambientEquatorColor;
            _baseGround = RenderSettings.ambientGroundColor;

            _captured = true;
        }

        private void Restore()
        {
            if (!_captured) return;

            RenderSettings.fogColor = _baseFog;
            RenderSettings.ambientSkyColor = _baseSky;
            RenderSettings.ambientEquatorColor = _baseEquator;
            RenderSettings.ambientGroundColor = _baseGround;
        }

        /// <summary>
        /// За кем следим. Camera.main одного мало: она ищет камеру с тегом MainCamera,
        /// а камера отладочного стенда создаётся кодом и без тега — из-за этого настроение
        /// молча не работало в игре, хотя компонент на месте и в редакторе всё считалось.
        /// </summary>
        private Transform Viewer()
        {
            if (viewer != null) return viewer;
            if (Camera.main != null) return Camera.main.transform;

            var camera = Camera.current;
            if (camera != null) return camera.transform;

            var all = Camera.allCameras;
            return all.Length > 0 ? all[0].transform : null;
        }

        /// <summary>Средние высоты полов по этажам — по ним определяем, где игрок.</summary>
        public void Rebuild()
        {
            _levelHeight.Clear();

            if (world == null) return;

            for (var level = 0; ; level++)
            {
                var rooms = world.GetRoomCenters(level);
                if (rooms.Count == 0) break;

                var sum = 0f;
                foreach (var room in rooms) sum += room.y;

                _levelHeight.Add(sum / rooms.Count);
            }
        }

        private void Update()
        {
            if (!Application.isPlaying) return;

            var target = Viewer();
            if (target == null) return;

            ApplyFor(target.position, Time.deltaTime * blendSpeed);
        }

        /// <summary>
        /// Ставит настроение для точки. Отдельный публичный метод нужен съёмке кадров:
        /// в редакторе компонент намеренно молчит, но кадр должен показывать то же,
        /// что увидит игрок.
        /// </summary>
        public void ApplyFor(Vector3 position, float step = 1f)
        {
            if (_levelHeight.Count == 0) Rebuild();
            if (_levelHeight.Count == 0) return;

            Capture();

            // Номер этажа как дробное число: на пандусе между этажами цвет обязан ползти
            // плавно, иначе на середине спуска картинка переключается рывком.
            var level = NearestLevel(position.y);

            _current = _current < 0f ? level : Mathf.Lerp(_current, level, Mathf.Clamp01(step));

            var low = Mathf.FloorToInt(_current);
            var high = Mathf.Min(low + 1, _levelHeight.Count - 1);
            var t = _current - low;

            var tint = Color.Lerp(CaveFixtures.VeinColorFor(low), CaveFixtures.VeinColorFor(high), t);

            // Туман красим оттенком, но НЕ поднимаем его яркость: туман задаёт цвет дали,
            // и осветлив его, мы просто зальём кадр молоком.
            RenderSettings.fogColor = Tint(_baseFog, tint, fogTint);

            RenderSettings.ambientSkyColor = Tint(_baseSky, tint, ambientTint);
            RenderSettings.ambientEquatorColor = Tint(_baseEquator, tint, ambientTint);
            RenderSettings.ambientGroundColor = Tint(_baseGround, tint, ambientTint * 0.6f);
        }

        /// <summary>Подкраска с сохранением яркости исходного цвета.</summary>
        private static Color Tint(Color source, Color tint, float amount)
        {
            var luminance = source.r * 0.2126f + source.g * 0.7152f + source.b * 0.0722f;
            var tintLuminance = Mathf.Max(0.001f, tint.r * 0.2126f + tint.g * 0.7152f + tint.b * 0.0722f);

            var scaled = tint * (luminance / tintLuminance);

            return Color.Lerp(source, scaled, amount);
        }

        private float NearestLevel(float y)
        {
            var best = 0;
            var bestDistance = float.MaxValue;

            for (var i = 0; i < _levelHeight.Count; i++)
            {
                var d = Mathf.Abs(_levelHeight[i] - y);
                if (d >= bestDistance) continue;

                bestDistance = d;
                best = i;
            }

            return best;
        }
    }
}
