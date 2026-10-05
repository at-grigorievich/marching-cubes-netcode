using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Линии оружия: трассеры, луч рельсы, молнии, росчерки гвоздей и гранат. Один динамический
    /// меш на всё и один вызов отрисовки: росчерков в кадре десятки, и объект с LineRenderer
    /// на каждый стоил бы вызова на каждый.
    ///
    /// Ленты развёрнуты к камере на процессоре — сегментов сотни, это дешевле, чем заводить
    /// под них свой шейдер. Цвет — в вершинах, смешение аддитивное (свет), туман гасит в чёрное.
    /// </summary>
    public sealed class WeaponFx : MonoBehaviour
    {
        private const int MaxSegments = 1024;

        private struct Segment
        {
            public Vector3 A;
            public Vector3 B;
            public float Width;
            public Color ColorA;
            public Color ColorB;
            public float Life;
            public float Age;

            /// <summary>Молния: перестраивать изломы каждый кадр.</summary>
            public int Bolt;
            public float Jitter;
            public int Seed;
        }

        private readonly List<Segment> _segments = new List<Segment>(256);
        private readonly List<Segment> _frame = new List<Segment>(256);

        private Mesh _mesh;
        private Material _material;

        private readonly List<Vector3> _vertices = new List<Vector3>(MaxSegments * 4);
        private readonly List<Color> _colors = new List<Color>(MaxSegments * 4);
        private readonly List<Vector2> _uvs = new List<Vector2>(MaxSegments * 4);
        private readonly List<int> _indices = new List<int>(MaxSegments * 6);

        public Camera Camera;

        public void Build(Material additive)
        {
            _material = additive;

            _mesh = new Mesh { name = "Weapon Fx Lines", hideFlags = HideFlags.DontSave };
            _mesh.MarkDynamic();
        }

        /// <summary>Линия, гаснущая за <paramref name="life"/> секунд (трассер, луч рельсы).</summary>
        public void Line(Vector3 a, Vector3 b, float width, Color colorA, Color colorB, float life)
        {
            if (_segments.Count >= MaxSegments) return;

            _segments.Add(new Segment { A = a, B = b, Width = width, ColorA = colorA, ColorB = colorB, Life = life });
        }

        /// <summary>Молния: ломаная из <paramref name="joints"/> изломов, перестраивается каждый кадр.</summary>
        public void Bolt(Vector3 a, Vector3 b, float width, Color color, float life, int joints, float jitter)
        {
            if (_segments.Count >= MaxSegments) return;

            _segments.Add(new Segment
            {
                A = a, B = b, Width = width, ColorA = color, ColorB = color, Life = life,
                Bolt = Mathf.Max(1, joints), Jitter = jitter, Seed = Random.Range(0, 100000)
            });
        }

        /// <summary>Росчерк на один кадр — хвост снаряда.</summary>
        public void Streak(Vector3 tail, Vector3 head, float width, Color tailColor, Color headColor)
        {
            if (_frame.Count >= MaxSegments) return;

            _frame.Add(new Segment { A = tail, B = head, Width = width, ColorA = tailColor, ColorB = headColor, Life = -1f });
        }

        public void Clear()
        {
            _segments.Clear();
            _frame.Clear();
        }

        private void LateUpdate()
        {
            if (_mesh == null || _material == null) return;

            var dt = Time.deltaTime;
            var eye = Camera != null ? Camera.transform.position : transform.position;

            _vertices.Clear();
            _colors.Clear();
            _uvs.Clear();
            _indices.Clear();

            for (var i = _segments.Count - 1; i >= 0; i--)
            {
                var s = _segments[i];
                s.Age += dt;

                if (s.Age >= s.Life)
                {
                    _segments.RemoveAt(i);
                    continue;
                }

                _segments[i] = s;

                // Гаснет к концу жизни, ширина чуть тает — луч «остывает», а не обрывается.
                var fade = 1f - s.Age / s.Life;
                var width = s.Width * (0.6f + 0.4f * fade);

                if (s.Bolt > 0) EmitBolt(s, eye, width, fade);
                else Emit(s.A, s.B, width, s.ColorA * fade, s.ColorB * fade, eye);
            }

            foreach (var s in _frame) Emit(s.A, s.B, s.Width, s.ColorA, s.ColorB, eye);

            _frame.Clear();

            _mesh.Clear();

            if (_vertices.Count == 0) return;

            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetUVs(0, _uvs);
            _mesh.SetTriangles(_indices, 0, false);
            _mesh.bounds = new Bounds(eye, Vector3.one * 1000f);

            var parameters = new RenderParams(_material)
            {
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                layer = gameObject.layer,
                worldBounds = new Bounds(eye, Vector3.one * 1000f)
            };

            Graphics.RenderMesh(parameters, _mesh, 0, Matrix4x4.identity);
        }

        private void EmitBolt(in Segment s, Vector3 eye, float width, float fade)
        {
            // Изломы — каждый кадр новые: молния «живёт», а не висит застывшей ломаной.
            var state = (uint)(s.Seed * 9781 + Time.frameCount * 6271) | 1u;
            var axis = s.B - s.A;
            var length = axis.magnitude;
            if (length < 1e-4f) return;

            var direction = axis / length;
            var side = Vector3.Cross(direction, Mathf.Abs(direction.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
            var up = Vector3.Cross(direction, side);

            var previous = s.A;

            for (var k = 1; k <= s.Bolt; k++)
            {
                var t = k / (float)s.Bolt;
                var point = Vector3.Lerp(s.A, s.B, t);

                if (k < s.Bolt)
                {
                    var amplitude = s.Jitter * length * Mathf.Sin(Mathf.PI * t);
                    point += side * (Next(ref state) * 2f - 1f) * amplitude +
                             up * (Next(ref state) * 2f - 1f) * amplitude;
                }

                Emit(previous, point, width, s.ColorA * fade, s.ColorB * fade, eye);
                previous = point;
            }
        }

        /// <summary>Xorshift без выделений: молнии перестраиваются каждый кадр.</summary>
        private static float Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;

            return (state & 0xFFFFFF) / (float)0x1000000;
        }

        private void Emit(Vector3 a, Vector3 b, float width, Color colorA, Color colorB, Vector3 eye)
        {
            if (_vertices.Count >= MaxSegments * 4) return;

            var along = b - a;
            if (along.sqrMagnitude < 1e-8f) return;

            // Лента развёрнута к камере: поперёк — перпендикуляр к линии и к взгляду.
            var toEye = eye - (a + b) * 0.5f;
            var across = Vector3.Cross(along, toEye);

            if (across.sqrMagnitude < 1e-10f) across = Vector3.Cross(along, Vector3.up);

            across = across.normalized * (width * 0.5f);

            var start = _vertices.Count;

            _vertices.Add(a - across);
            _vertices.Add(a + across);
            _vertices.Add(b + across);
            _vertices.Add(b - across);

            _colors.Add(colorA);
            _colors.Add(colorA);
            _colors.Add(colorB);
            _colors.Add(colorB);

            _uvs.Add(new Vector2(0f, 0f));
            _uvs.Add(new Vector2(1f, 0f));
            _uvs.Add(new Vector2(1f, 1f));
            _uvs.Add(new Vector2(0f, 1f));

            _indices.Add(start);
            _indices.Add(start + 1);
            _indices.Add(start + 2);
            _indices.Add(start);
            _indices.Add(start + 2);
            _indices.Add(start + 3);
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
