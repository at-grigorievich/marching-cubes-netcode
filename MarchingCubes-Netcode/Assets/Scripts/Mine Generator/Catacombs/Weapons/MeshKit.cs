using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Сборщик простых мешей из коробок, цилиндров и дисков — по подмешу на материал.
    ///
    /// Гранатомёт, граната и обломки породы собираются кодом, а не берутся ассетом: готовой
    /// модели оружия в проекте нет, а у кодовой нет ни импорта, ни .fbx в репозитории,
    /// и её пропорции правятся числами рядом с объяснением, почему они такие.
    /// </summary>
    public sealed class MeshKit
    {
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<Vector2> _uvs = new List<Vector2>();
        private readonly List<List<int>> _submeshes = new List<List<int>>();

        public MeshKit(int submeshCount)
        {
            for (var i = 0; i < submeshCount; i++) _submeshes.Add(new List<int>());
        }

        /// <summary>Коробка с гранями под своими нормалями — острые рёбра, как у металла.</summary>
        public void Box(int submesh, Vector3 centre, Vector3 size, Quaternion rotation)
        {
            var half = size * 0.5f;

            for (var face = 0; face < 6; face++)
            {
                var axis = face / 2;
                var sign = face % 2 == 0 ? 1f : -1f;

                var normal = Vector3.zero;
                normal[axis] = sign;

                var u = Vector3.zero;
                u[(axis + 1) % 3] = 1f;

                var v = Vector3.Cross(normal, u);

                var faceCentre = Vector3.Scale(normal, half);
                var du = Vector3.Scale(u, half);
                var dv = Vector3.Scale(v, half);

                Quad(submesh,
                    centre + rotation * (faceCentre - du - dv),
                    centre + rotation * (faceCentre + du - dv),
                    centre + rotation * (faceCentre + du + dv),
                    centre + rotation * (faceCentre - du + dv),
                    rotation * normal);
            }
        }

        /// <summary>
        /// Цилиндр вдоль оси. Бока — гладкие, торцы — плоские. Конус, если радиусы разные.
        /// </summary>
        public void Cylinder(int submesh, Vector3 start, Vector3 end, float startRadius, float endRadius,
            int segments, bool capStart = true, bool capEnd = true)
        {
            var axis = end - start;
            var length = axis.magnitude;
            if (length < 1e-5f) return;

            axis /= length;

            var side = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up;
            var u = Vector3.Cross(axis, side).normalized;
            var v = Vector3.Cross(axis, u);

            var slope = (startRadius - endRadius) / length;

            for (var i = 0; i < segments; i++)
            {
                var a0 = i * Mathf.PI * 2f / segments;
                var a1 = (i + 1) * Mathf.PI * 2f / segments;

                var d0 = u * Mathf.Cos(a0) + v * Mathf.Sin(a0);
                var d1 = u * Mathf.Cos(a1) + v * Mathf.Sin(a1);

                var n0 = (d0 + axis * slope).normalized;
                var n1 = (d1 + axis * slope).normalized;

                var b = _vertices.Count;

                Add(start + d0 * startRadius, n0, new Vector2(i / (float)segments, 0f));
                Add(start + d1 * startRadius, n1, new Vector2((i + 1) / (float)segments, 0f));
                Add(end + d1 * endRadius, n1, new Vector2((i + 1) / (float)segments, 1f));
                Add(end + d0 * endRadius, n0, new Vector2(i / (float)segments, 1f));

                Triangle(submesh, b, b + 1, b + 2);
                Triangle(submesh, b, b + 2, b + 3);
            }

            if (capStart) Disc(submesh, start, -axis, startRadius, segments);
            if (capEnd) Disc(submesh, end, axis, endRadius, segments);
        }

        /// <summary>Плоский круг, смотрящий в сторону нормали.</summary>
        public void Disc(int submesh, Vector3 centre, Vector3 normal, float radius, int segments)
        {
            normal.Normalize();

            var side = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up;
            var u = Vector3.Cross(normal, side).normalized;
            var v = Vector3.Cross(normal, u);

            var c = _vertices.Count;
            Add(centre, normal, new Vector2(0.5f, 0.5f));

            for (var i = 0; i <= segments; i++)
            {
                var a = i * Mathf.PI * 2f / segments;
                var d = u * Mathf.Cos(a) + v * Mathf.Sin(a);

                Add(centre + d * radius, normal, new Vector2(0.5f + d.x * 0.5f, 0.5f + d.y * 0.5f));
            }

            for (var i = 0; i < segments; i++) Triangle(submesh, c, c + 1 + i, c + 2 + i);
        }

        /// <summary>Неровный камешек: икосаэдр с раздутыми вершинами. Обломки породы от взрыва.</summary>
        public void Rock(int submesh, Vector3 centre, float radius, int seed)
        {
            var t = (1f + Mathf.Sqrt(5f)) * 0.5f;

            var points = new[]
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1)
            };

            var random = new System.Random(seed);

            for (var i = 0; i < points.Length; i++)
            {
                var stretch = new Vector3(1f, 0.6f + (float)random.NextDouble() * 0.5f, 1f);
                points[i] = Vector3.Scale(points[i].normalized, stretch) * (radius * (0.7f + (float)random.NextDouble() * 0.5f));
            }

            int[] faces =
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1
            };

            // Плоские грани: у камня рёбра, а не обтекаемость.
            for (var f = 0; f < faces.Length; f += 3)
            {
                var a = points[faces[f]];
                var b = points[faces[f + 1]];
                var c = points[faces[f + 2]];

                var normal = Vector3.Cross(b - a, c - a).normalized;
                var i0 = _vertices.Count;

                Add(centre + a, normal, Vector2.zero);
                Add(centre + b, normal, Vector2.right);
                Add(centre + c, normal, Vector2.up);

                Triangle(submesh, i0, i0 + 1, i0 + 2);
            }
        }

        public Mesh Build(string name)
        {
            var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };

            mesh.SetVertices(_vertices);
            mesh.SetNormals(_normals);
            mesh.SetUVs(0, _uvs);
            mesh.subMeshCount = _submeshes.Count;

            for (var i = 0; i < _submeshes.Count; i++) mesh.SetTriangles(_submeshes[i], i);

            mesh.RecalculateBounds();
            return mesh;
        }

        private void Quad(int submesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            var i = _vertices.Count;

            Add(a, normal, new Vector2(0f, 0f));
            Add(b, normal, new Vector2(1f, 0f));
            Add(c, normal, new Vector2(1f, 1f));
            Add(d, normal, new Vector2(0f, 1f));

            // Обход по часовой, если смотреть снаружи: так Unity считает лицевой стороной.
            Triangle(submesh, i, i + 1, i + 2);
            Triangle(submesh, i, i + 2, i + 3);
        }

        private void Add(Vector3 position, Vector3 normal, Vector2 uv)
        {
            _vertices.Add(position);
            _normals.Add(normal);
            _uvs.Add(uv);
        }

        private void Triangle(int submesh, int a, int b, int c)
        {
            var list = _submeshes[submesh];

            list.Add(a);
            list.Add(b);
            list.Add(c);
        }
    }
}
