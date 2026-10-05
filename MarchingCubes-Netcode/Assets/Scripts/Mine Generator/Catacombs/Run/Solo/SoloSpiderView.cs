using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Отрисовка одиночки — Матки или особого паука — той же запечённой анимацией, что толпа,
    /// одним экземпляром (<c>RenderMeshInstanced</c> на один инстанс). Вынесена из
    /// <see cref="CaveQueen"/> (план M3.1): особых пауков рисует то же самое.
    ///
    /// Своего меша у одиночек нет — вид пака (<see cref="SpiderKind"/>), растянутый и окрашенный.
    /// Четвёртый канал оттенка — возраст разлёта на куски в шейдере толпы (грабли №60): у одиночки
    /// он всегда ноль, иначе она разлетелась бы в первом же кадре.
    /// </summary>
    public sealed class SoloSpiderView
    {
        private static readonly int AnimStateId = Shader.PropertyToID("_AnimState");
        private static readonly int InstanceTintId = Shader.PropertyToID("_InstanceTint");

        public SpiderKind Kind { get; private set; }

        /// <summary>Итоговый масштаб меша: множитель вида × множитель одиночки.</summary>
        public float Scale { get; private set; } = 1f;

        /// <summary>Центр тела над точкой опоры.</summary>
        public float BodyLift { get; private set; } = 0.5f;

        /// <summary>Радиус попадания — щедрый, по габаритам всех клипов.</summary>
        public float HitRadius { get; private set; } = 1f;

        /// <summary>Модель смотрит в минус Z (пак Spiders, грабли №19).</summary>
        public float Facing => Kind != null && Kind.FacesMinusZ ? -1f : 1f;

        public SpiderClip Clip => _clip;
        public float Phase => _phase;

        /// <summary>Неповторяемый клип доигран до конца.</summary>
        public bool Finished { get; private set; }

        private SpiderClip _clip = SpiderClip.Idle;
        private float _phase;

        private readonly Matrix4x4[] _matrix = new Matrix4x4[1];
        private readonly Vector4[] _anim = new Vector4[1];
        private readonly Vector4[] _tint = new Vector4[1];
        private MaterialPropertyBlock _block;

        /// <summary>Вид, множитель размера, оттенок. False — вида нет (толпа ещё не заселена).</summary>
        public bool Bind(SpiderKind kind, float sizeScale, Color hue)
        {
            Kind = kind;
            if (kind == null) return false;

            Scale = kind.Scale * sizeScale;
            BodyLift = math.max(0.3f, kind.RestBounds.center.y * Scale + kind.Hover * Scale);
            HitRadius = math.max(0.5f, kind.RestBounds.extents.magnitude * Scale * 0.7f);

            _block ??= new MaterialPropertyBlock();
            SetHue(hue);

            return true;
        }

        public void SetHue(Color hue) => _tint[0] = new Vector4(hue.r, hue.g, hue.b, 0f);

        public void Play(SpiderClip clip, bool restart = false)
        {
            if (_clip == clip && !restart) return;

            _clip = clip;
            _phase = 0f;
            Finished = false;
        }

        /// <summary>Шаг клипа. Удар доигран — обратно в стояние; смерть застывает на последнем кадре.</summary>
        public void Advance(float deltaTime, float speed = 1f)
        {
            if (Kind == null) return;

            var range = Kind.GetClip(_clip);
            _phase += deltaTime * speed / math.max(0.05f, range.Length);

            if (range.Loop)
            {
                _phase = math.frac(_phase);
                return;
            }

            if (_phase < 1f) return;

            Finished = true;

            if (_clip == SpiderClip.Attack) Play(SpiderClip.Idle);
            else _phase = 1f;
        }

        /// <summary>Нарисовать в точке опоры (лапы на камне) с поворотом.</summary>
        public void Draw(Vector3 foot, Quaternion rotation, int layer, bool shadows)
        {
            if (Kind == null || Kind.Material == null || Kind.Mesh == null) return;

            var range = Kind.GetClip(_clip);
            var up = rotation * Vector3.up;

            _matrix[0] = Matrix4x4.TRS(foot + up * (Kind.Hover * Scale), rotation, Vector3.one * Scale);
            _anim[0] = new Vector4(range.StartRow, range.FrameCount, _phase, range.Loop ? 1f : 0f);

            _block.SetVectorArray(AnimStateId, _anim);
            _block.SetVectorArray(InstanceTintId, _tint);

            var parameters = new RenderParams(Kind.Material)
            {
                worldBounds = new Bounds(foot + up * BodyLift, Vector3.one * (HitRadius * 4f)),
                shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                receiveShadows = true,
                layer = layer,
                renderingLayerMask = 1,
                lightProbeUsage = LightProbeUsage.Off,
                reflectionProbeUsage = ReflectionProbeUsage.Off,
                matProps = _block
            };

            Graphics.RenderMeshInstanced(parameters, Kind.Mesh, 0, _matrix, 1);
        }
    }
}
