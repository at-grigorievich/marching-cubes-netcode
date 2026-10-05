using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Материалы и текстуры моделей оружия: создаются кодом в play-режиме и в сцену не пишутся.
    /// Шейдеры приезжают ссылками от арсенала (их проставляет «Починить свет»): <c>Shader.Find</c>
    /// в сборке находит только то, на что кто-то ссылается.
    ///
    /// Всё созданное копится здесь и уничтожается разом — иначе каждая перестройка
    /// оставляла бы в памяти материалы прошлой.
    /// </summary>
    public sealed class WeaponMaterials : IDisposable
    {
        private readonly Shader _lit;
        private readonly Shader _particles;
        private readonly List<Object> _owned = new List<Object>();

        private readonly Dictionary<string, Material> _cache = new Dictionary<string, Material>();

        public WeaponMaterials(Shader lit, Shader particles)
        {
            _lit = lit;
            _particles = particles;
        }

        public bool IsValid => _lit != null && _particles != null;

        /// <summary>
        /// Металл и полимер оружия. Светлее и менее металлический, чем в жизни: отражать в пещере
        /// нечего (ни неба, ни проб), и сильно металлический материал читался чёрным пятном.
        /// Кэш по имени (грабли №2 — ключ по аргументу).
        /// </summary>
        public Material Lit(string name, Color color, float metallic, float smoothness, Color emission = default)
        {
            if (_cache.TryGetValue(name, out var cached)) return cached;

            var material = new Material(_lit) { name = name, hideFlags = HideFlags.DontSave, enableInstancing = true };

            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);

            if (emission.maxColorComponent > 0f)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission);
            }

            _owned.Add(material);
            _cache[name] = material;

            return material;
        }

        /// <summary>Свечение (вспышка, точка прицела, раскалённый ствол) — частицы, аддитивно или смешением.</summary>
        public Material Glow(string name, Texture2D texture, Color color, bool additive = true)
        {
            if (_cache.TryGetValue(name, out var cached)) return cached;

            var material = new Material(_particles) { name = name, hideFlags = HideFlags.DontSave, enableInstancing = true };

            material.mainTexture = texture;
            material.color = color;
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            material.SetFloat("_FogToBlack", additive ? 1f : 0f);
            material.renderQueue = (int)RenderQueue.Transparent;

            _owned.Add(material);
            _cache[name] = material;

            return material;
        }

        /// <summary>Мягкая точка: ореол, вспышка, капля.</summary>
        public Texture2D SoftDot => _softDot ??= Paint("Soft Dot", 64, (u, v, r) =>
        {
            var a = Mathf.Clamp01(1f - r);
            return new Color(1f, 1f, 1f, a * a * a);
        });

        /// <summary>Лента с мягкими краями поперёк — для линий эффектов.</summary>
        public Texture2D SoftLine => _softLine ??= Paint("Soft Line", 32, (u, v, r) =>
        {
            var a = Mathf.Clamp01(1f - Mathf.Abs(u));
            return new Color(1f, 1f, 1f, a * a);
        });

        private Texture2D _softDot;
        private Texture2D _softLine;

        /// <summary>Рисует текстуру функцией (u, v, r) в координатах −1..1.</summary>
        public Texture2D Paint(string name, int size, Func<float, float, float, Color> paint)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
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

        public T Own<T>(T asset) where T : Object
        {
            _owned.Add(asset);
            return asset;
        }

        /// <summary>Часть модели: меш с материалами под родителем, без теней (тень оружия у лица — пятно на весь кадр).</summary>
        public Renderer Part(Transform parent, string name, Mesh mesh, params Material[] materials)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(parent, false);

            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = ShadowCastingMode.Off;

            return renderer;
        }

        public void Dispose()
        {
            foreach (var owned in _owned)
            {
                if (owned == null) continue;

                if (Application.isPlaying) Object.Destroy(owned);
                else Object.DestroyImmediate(owned);
            }

            _owned.Clear();
            _cache.Clear();
        }
    }
}
