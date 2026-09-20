using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Замер шага по запечённым кадрам бега: насколько далеко ходит лапа за цикл.
    ///
    /// Нужен потому, что «скорость анимации не совпадает со скоростью движения» —
    /// жалоба, которую нельзя проверить, глядя в инспектор. Фаза бега ведётся пройденным
    /// путём и делится на <see cref="SpiderKind.StrideLength"/>, поэтому вопрос сводится
    /// к одному числу: сколько юнитов проходит особь за цикл на самом деле. Раньше оно
    /// было ДОГАДКОЙ (1.1 длины тела), и сверить её было не с чем.
    ///
    /// Печатает размах хода вершин по всем трём осям, а не только по оси движения:
    /// если окажется, что лапы ходят в основном по X, значит у пака другое соглашение
    /// об осях, и это надо знать до того, как подгонять числа.
    /// </summary>
    public static class SpiderStrideProbe
    {
        private const string KindFolder = "Assets/Spiders";

        public static void Run()
        {
            var code = 0;

            try
            {
                Report();
            }
            catch (Exception error)
            {
                Debug.LogError("ЗАМЕР ШАГА УПАЛ: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        /// <summary>То же без выхода из процесса — для вызова при открытом редакторе.</summary>
        public static void Report()
        {
            var kinds = AssetDatabase.FindAssets("t:SpiderKind", new[] { KindFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<SpiderKind>)
                .Where(k => k != null && k.IsReady)
                .ToList();

            var text = new StringBuilder();

            text.AppendLine("вид            | тело Z | ход X | ход Y | ход Z | шаг сейчас | циклов/с при своей скорости");

            foreach (var kind in kinds)
            {
                var walk = kind.GetClip(SpiderClip.Walk);
                var pixels = kind.Positions.GetPixels();
                var verts = kind.Positions.width;

                var spanX = new float[verts];
                var spanY = new float[verts];
                var spanZ = new float[verts];

                for (var v = 0; v < verts; v++)
                {
                    var minimum = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var maximum = new Vector3(float.MinValue, float.MinValue, float.MinValue);

                    for (var f = 0; f < walk.FrameCount; f++)
                    {
                        var c = pixels[(walk.StartRow + f) * verts + v];
                        var p = new Vector3(c.r, c.g, c.b);

                        minimum = Vector3.Min(minimum, p);
                        maximum = Vector3.Max(maximum, p);
                    }

                    var span = maximum - minimum;

                    spanX[v] = span.x;
                    spanY[v] = span.y;
                    spanZ[v] = span.z;
                }

                Array.Sort(spanX);
                Array.Sort(spanY);
                Array.Sort(spanZ);

                var at90 = Mathf.Clamp(Mathf.RoundToInt(verts * 0.9f), 0, verts - 1);

                // Циклов в секунду при собственной скорости вида: это и есть то, что
                // видно глазами. Три с небольшим — нормальный бег; десять — мельтешение,
                // и оно же читается как «анимация не совпадает со скоростью».
                var strideWorld = kind.StrideLength * kind.Scale;
                var cycles = strideWorld > 0.001f ? kind.MoveSpeed / strideWorld : 0f;

                text.AppendLine($"{kind.name,-14} | {kind.RestBounds.size.z,6:0.00} | {spanX[at90],5:0.00} | " +
                                $"{spanY[at90],5:0.00} | {spanZ[at90],5:0.00} | {kind.StrideLength,10:0.00} | {cycles,5:0.0}");
            }

            Debug.Log($"ЗАМЕР ШАГА по запечённым кадрам бега:{Environment.NewLine}{text}");
        }
    }
}
