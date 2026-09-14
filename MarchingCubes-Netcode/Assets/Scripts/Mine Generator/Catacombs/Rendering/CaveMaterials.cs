using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Материалы неосвещаемой мелочи: ореолы, пылинки, паутина, светящиеся ядра ламп.
    ///
    /// Собраны в одном месте не ради красоты, а потому что в URP режим смешения — это
    /// свойства материала (_SrcBlend, _DstBlend, _ZWrite) плюс очередь отрисовки, и
    /// раскиданные по пяти файлам они разъезжались бы молча: материал с неверной очередью
    /// не падает, он просто рисуется не тогда, когда надо.
    ///
    /// До URP здесь брались три встроенных шейдера — Mobile/Particles/Additive, Unlit/Color
    /// и Unlit/Transparent. Формально они и сейчас рисуются, но тумана URP не знают и
    /// в SRP Batcher не собираются, поэтому всё переведено на собственный Cave Unlit.
    /// </summary>
    public static class CaveMaterials
    {
        private const string ShaderName = "Mine Generator/Cave Unlit";

        private static Shader _shader;

        private static Shader UnlitShader()
        {
            if (_shader != null) return _shader;

            // Запасной вариант — штатный URP-овский. Имена свойств у него другие
            // (_BaseMap, _BaseColor), но оба помечены [MainTexture] и [MainColor],
            // поэтому material.mainTexture и material.color доезжают и до него.
            _shader = Shader.Find(ShaderName) ?? Shader.Find("Universal Render Pipeline/Unlit");

            return _shader;
        }

        /// <summary>
        /// Аддитивный материал: то, что складывается с кадром, а не закрывает его.
        /// Ореолы источников, пылинки, ядро сигнальной шашки.
        /// </summary>
        public static Material Additive(string name, Texture2D texture, Color color)
        {
            var material = Create(name, texture, color);

            material.SetFloat(SrcBlend, (float)BlendMode.One);
            material.SetFloat(DstBlend, (float)BlendMode.One);

            // Глубину не пишет: два ореола, наложенные друг на друга, должны сложиться,
            // а не отсечь один другой по Z.
            material.SetFloat(ZWrite, 0f);
            material.renderQueue = (int)RenderQueue.Transparent;

            return material;
        }

        /// <summary>
        /// Полупрозрачный материал по альфе. Паутина — это нити на просвет,
        /// а не поверхность.
        /// </summary>
        public static Material AlphaBlended(string name, Texture2D texture, Color color)
        {
            var material = Create(name, texture, color);

            material.SetFloat(SrcBlend, (float)BlendMode.SrcAlpha);
            material.SetFloat(DstBlend, (float)BlendMode.OneMinusSrcAlpha);

            material.SetFloat(ZWrite, 0f);
            material.renderQueue = (int)RenderQueue.Transparent;

            return material;
        }

        /// <summary>
        /// Непрозрачный неосвещаемый цвет — ядра ламп и жил. Светятся сами и не должны
        /// зависеть от того, попал ли на них свет собственного источника.
        /// </summary>
        public static Material UnlitOpaque(string name, Color color)
        {
            var material = Create(name, null, color);

            material.SetFloat(SrcBlend, (float)BlendMode.One);
            material.SetFloat(DstBlend, (float)BlendMode.Zero);

            material.SetFloat(ZWrite, 1f);
            material.renderQueue = (int)RenderQueue.Geometry;

            return material;
        }

        private static Material Create(string name, Texture2D texture, Color color)
        {
            var material = new Material(UnlitShader())
            {
                name = name,
                color = color,
                hideFlags = HideFlags.HideAndDontSave
            };

            if (texture != null) material.mainTexture = texture;

            return material;
        }

        private static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
        private static readonly int ZWrite = Shader.PropertyToID("_ZWrite");
    }
}
