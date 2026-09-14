using System;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Отчёт о состоянии шейдеров катакомб и о том, какой пайплайн сейчас назначен.
    ///
    /// Нужен потому, что в batchmode шейдеры компилируются лениво: прогон без единого
    /// отрисованного кадра заканчивается успехом даже тогда, когда шейдер не собирается
    /// вовсе. ShaderUtil спрашивает про это напрямую и заставляет компиляцию произойти.
    /// </summary>
    public static class CatacombShaderCheck
    {
        private static readonly string[] Shaders =
        {
            "Mine Generator/Cave Triplanar",
            "Mine Generator/Cave Unlit"
        };

        public static void Run()
        {
            var code = 0;

            try
            {
                code = Report();
            }
            catch (Exception error)
            {
                Debug.LogError("ПРОВЕРКА ШЕЙДЕРОВ УПАЛА: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        private static int Report()
        {
            var text = new StringBuilder();
            var failed = 0;

            text.AppendLine("=== Пайплайн ===");

            var pipeline = GraphicsSettings.currentRenderPipeline;

            text.AppendLine(pipeline != null
                ? $"  назначен: {pipeline.name} ({pipeline.GetType().Name})"
                : "  НЕ НАЗНАЧЕН — проект всё ещё на Built-in");

            if (pipeline == null) failed++;

            text.AppendLine("=== Шейдеры ===");

            foreach (var name in Shaders)
            {
                var shader = Shader.Find(name);

                if (shader == null)
                {
                    text.AppendLine($"  {name}: НЕ НАЙДЕН");
                    failed++;
                    continue;
                }

                // Заставляет компиляцию произойти здесь и сейчас, а не при первой отрисовке.
                var errors = ShaderUtil.GetShaderMessageCount(shader);

                if (errors == 0)
                {
                    text.AppendLine($"  {name}: собирается, сообщений нет");
                    continue;
                }

                var messages = ShaderUtil.GetShaderMessages(shader);
                var hard = 0;

                foreach (var message in messages)
                {
                    var severity = message.severity == ShaderCompilerMessageSeverity.Error ? "ОШИБКА" : "warning";

                    if (message.severity == ShaderCompilerMessageSeverity.Error) hard++;

                    text.AppendLine($"    [{severity}] {message.platform}: {message.message} {message.messageDetails}".TrimEnd());
                }

                text.AppendLine($"  {name}: сообщений {messages.Length}, из них ошибок {hard}");

                if (hard > 0) failed++;
            }

            if (failed > 0)
            {
                Debug.LogError($"ПРОВЕРКА НЕ ПРОШЛА, проблемных пунктов {failed}:{Environment.NewLine}{text}");
                return 1;
            }

            Debug.Log($"Проверка пройдена:{Environment.NewLine}{text}");
            return 0;
        }
    }
}
