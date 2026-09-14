using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Аддитивный ореол вокруг источника — квадрат с радиальной размывкой, всегда развёрнутый
    /// к камере.
    ///
    /// Нужен потому, что сам источник света в кадре не виден: виден только результат его
    /// работы на породе. Без ореола шашка читалась как белая таблетка, приклеенная к полу, —
    /// яркое пятно с резкой кромкой, у которого нет ни свечения, ни размера. Обычно это
    /// делают блумом, но свой пост-процесс в этом проекте уже откатывали, и ореол
    /// геометрией — ровно тот же эффект без единого полноэкранного прохода.
    ///
    /// Разворот делается в <see cref="OnWillRenderObject"/>, а не в Update: так он работает
    /// и в редакторе при съёмке кадров через Camera.Render, где Update не вызывается вовсе.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class CaveGlowBillboard : MonoBehaviour
    {
        private static Mesh _quad;

        // Материал на цвет, а не на ореол: жил в уровне несколько десятков, и все они
        // одного цвета — отдельный материал на каждую ломал бы пакетную отрисовку.
        private static readonly System.Collections.Generic.Dictionary<Color, Material> Materials =
            new System.Collections.Generic.Dictionary<Color, Material>();

        private static readonly System.Collections.Generic.Dictionary<Color, Texture2D> Falloffs =
            new System.Collections.Generic.Dictionary<Color, Texture2D>();

        /// <summary>Создаёт ореол заданного размера и цвета как дочерний объект.</summary>
        /// <param name="faceCamera">
        /// true — ореол вокруг самого источника, всегда развёрнут к зрителю.
        /// false — пятно света на поверхности: квад остаётся в повороте родителя, то есть
        /// лежит на стене. Заведено было под Built-in, где попиксельных мест всего четыре:
        /// дальние лампы Unity уводила в вершинные, и они переставали освещать стену вовсе —
        /// оставалась светящаяся точка на ровной поверхности, то есть наклейка.
        ///
        /// При Forward+ вершинных источников нет и повода к такому вырождению тоже, но
        /// пятно оставлено: на WebGL2 больше 32 видимых источников в кадре пайплайн
        /// не возьмёт, и за этим потолком всё повторится один в один. Пятно рисуется
        /// независимо от того, достался источнику кластер или нет.
        /// </param>
        public static CaveGlowBillboard Attach(Transform parent, float size, Color color, bool faceCamera = true)
        {
            // Меш берём готовый, а не через CreatePrimitive: примитив приезжает с коллайдером,
            // и снять его сразу нельзя — Destroy в play-режиме отложенный. У шашки ореол висит
            // под Rigidbody, и этот лишний вогнутый MeshCollider ломал физику целиком: PhysX
            // отказывался его считать, шашка проваливалась сквозь пол и улетала в породу
            // на два десятка юнитов. Плюс он же перехватывал бы лучи копания и выстрелов.
            var quad = new GameObject("Glow Halo");
            quad.transform.SetParent(parent, false);
            quad.transform.localScale = Vector3.one * size;

            quad.AddComponent<MeshFilter>().sharedMesh = QuadMesh();

            var view = quad.AddComponent<MeshRenderer>();
            view.sharedMaterial = Material(color);
            view.shadowCastingMode = ShadowCastingMode.Off;
            view.receiveShadows = false;

            // Свет не должен получать тень от жил и шашек, а сам ореол — прозрачный.
            view.lightProbeUsage = LightProbeUsage.Off;
            view.reflectionProbeUsage = ReflectionProbeUsage.Off;

            return faceCamera ? quad.AddComponent<CaveGlowBillboard>() : null;
        }

        /// <summary>Встроенный квад Unity — тот же меш, что у PrimitiveType.Quad.</summary>
        private static Mesh QuadMesh()
        {
            return _quad != null ? _quad : _quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
        }

        private void OnWillRenderObject()
        {
            var camera = Camera.current;
            if (camera == null) return;

            // Разворачиваем плоскостью к камере. Не LookAt на позицию камеры, а по её оси
            // взгляда: у самой камеры ореол иначе заметно косит к краям кадра.
            transform.rotation = camera.transform.rotation;
        }

        /// <summary>
        /// Материал ореола. Цвет запекается в саму текстуру, а не задаётся свойством.
        ///
        /// Так повелось со времён Built-in: у Mobile/Particles/Additive из свойств есть
        /// только _MainTex — ни _Color, ни _TintColor, и цвет, выставленный через
        /// material.color, тот шейдер молча игнорировал. Ореолы получались белыми
        /// независимо от того, что им передали, и по кадру это читалось как «аддитивный
        /// слой упёрся в единицу», хотя цвета там не было с самого начала.
        ///
        /// Cave Unlit цвет знает, и запекание больше не вынужденное. Оставлено как есть
        /// намеренно: форма спада у ореола неравномерная по цвету (ядро ярче обода),
        /// и разложить её обратно на белую текстуру и тинт — это отдельная правка
        /// с отдельной проверкой по кадрам, а не побочный эффект переезда на URP.
        /// </summary>
        private static Material Material(Color color)
        {
            Material cached;
            if (Materials.TryGetValue(color, out cached) && cached != null) return cached;

            var material = CaveMaterials.Additive("Cave Glow Halo", Falloff(color), Color.white);

            Materials[color] = material;
            return material;
        }

        /// <summary>
        /// Радиальная размывка. Спад квадратичный, а не линейный: линейный даёт видимый
        /// круглый край, потому что производительная часть спада приходится на самый обод.
        /// </summary>
        private static Texture2D Falloff(Color color)
        {
            Texture2D cached;
            if (Falloffs.TryGetValue(color, out cached) && cached != null) return cached;

            const int size = 64;

            var falloff = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Cave Glow Falloff",
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color[size * size];

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = (x + 0.5f) / size * 2f - 1f;
                    var dy = (y + 0.5f) / size * 2f - 1f;

                    var shape = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    shape *= shape;

                    // Ядро поярче обода: у источника есть различимый центр, иначе ореол
                    // читается как ровное мутное пятно.
                    var value = shape * 0.75f + shape * shape * shape * 0.25f;

                    pixels[y * size + x] = new Color(color.r * value, color.g * value, color.b * value, value);
                }
            }

            falloff.SetPixels(pixels);
            falloff.Apply();

            Falloffs[color] = falloff;
            return falloff;
        }
    }
}
