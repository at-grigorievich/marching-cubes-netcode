using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Модель многозарядного гранатомёта — барабанного, по мотивам MGL: шестизарядный барабан,
    /// короткий толстый ствол калибра 40 мм, рама над барабаном, планка с коллиматором,
    /// пистолетная рукоять, цевьё с передней рукоятью и приклад.
    ///
    /// Собирается кодом (см. <see cref="MeshKit"/>). Метры, ось Z — вперёд по стволу,
    /// начало координат — на оси барабана. Ствол стоит напротив верхней каморы барабана,
    /// поэтому его ось выше оси барабана на радиус круга камор.
    ///
    /// Барабан — отдельный меш: он проворачивается на шестую часть оборота с каждым выстрелом
    /// и крутится при перезарядке. Остальное — один меш на три материала.
    /// </summary>
    public static class LauncherModel
    {
        /// <summary>Подмеши корпуса: воронёный металл, полимер, чернь (канал ствола, пазы).</summary>
        public const int Metal = 0;
        public const int Polymer = 1;
        public const int Dark = 2;

        /// <summary>Подмеш барабана с латунными донцами гранат в каморах.</summary>
        public const int Brass = 1;

        /// <summary>Радиус круга камор: на столько ось ствола выше оси барабана.</summary>
        public const float ChamberRing = 0.046f;

        public const float DrumRadius = 0.078f;

        /// <summary>Дульный срез — отсюда вылетает граната и сюда встаёт вспышка.</summary>
        public static readonly Vector3 Muzzle = new Vector3(0f, ChamberRing, 0.37f);

        /// <summary>Где висит красная точка коллиматора.</summary>
        public static readonly Vector3 SightDot = new Vector3(0f, DrumRadius + 0.074f, -0.035f);

        public static Mesh BuildBody()
        {
            var kit = new MeshKit(3);

            var barrelY = ChamberRing;
            var top = DrumRadius;

            // Задняя плита барабана и передняя рама, в которой ходит ствол.
            kit.Cylinder(Metal, new Vector3(0f, 0f, -0.086f), new Vector3(0f, 0f, -0.072f), 0.084f, 0.084f, 24);
            kit.Box(Metal, new Vector3(0f, barrelY * 0.4f, 0.08f), new Vector3(0.1f, 0.13f, 0.016f), Quaternion.identity);

            // Ствол: труба, утолщение у дула, чернёный канал на срезе.
            kit.Cylinder(Metal, new Vector3(0f, barrelY, 0.08f), new Vector3(0f, barrelY, 0.345f), 0.029f, 0.029f, 20);
            kit.Cylinder(Metal, new Vector3(0f, barrelY, 0.345f), new Vector3(0f, barrelY, 0.37f), 0.034f, 0.034f, 20,
                true, false);
            kit.Disc(Metal, new Vector3(0f, barrelY, 0.37f), Vector3.forward, 0.034f, 20);
            kit.Disc(Dark, new Vector3(0f, barrelY, 0.3705f), Vector3.forward, 0.022f, 20);

            // Кольца кожуха на стволе: по ним на бегу видно, что это труба, а не палка.
            for (var i = 0; i < 4; i++)
            {
                var z = 0.12f + i * 0.045f;
                kit.Cylinder(Metal, new Vector3(0f, barrelY, z), new Vector3(0f, barrelY, z + 0.009f), 0.033f, 0.033f, 20);
            }

            // Верхняя рама над барабаном и планка на ней.
            kit.Box(Metal, new Vector3(0f, top + 0.012f, 0.0f), new Vector3(0.026f, 0.018f, 0.2f), Quaternion.identity);
            kit.Box(Metal, new Vector3(0f, top + 0.03f, 0.03f), new Vector3(0.03f, 0.012f, 0.3f), Quaternion.identity);

            for (var i = 0; i < 11; i++)
            {
                kit.Box(Dark, new Vector3(0f, top + 0.038f, -0.1f + i * 0.026f), new Vector3(0.033f, 0.005f, 0.008f),
                    Quaternion.identity);
            }

            // Коллиматор: основание, две стойки, козырёк. Красную точку рисует гранатомёт.
            kit.Box(Metal, new Vector3(0f, top + 0.044f, -0.035f), new Vector3(0.038f, 0.012f, 0.055f), Quaternion.identity);
            kit.Box(Metal, new Vector3(-0.022f, top + 0.074f, -0.035f), new Vector3(0.006f, 0.052f, 0.04f), Quaternion.identity);
            kit.Box(Metal, new Vector3(0.022f, top + 0.074f, -0.035f), new Vector3(0.006f, 0.052f, 0.04f), Quaternion.identity);
            kit.Box(Metal, new Vector3(0f, top + 0.102f, -0.035f), new Vector3(0.05f, 0.006f, 0.04f), Quaternion.identity);
            kit.Box(Dark, new Vector3(0f, top + 0.074f, -0.012f), new Vector3(0.038f, 0.046f, 0.003f), Quaternion.identity);

            // Нижняя рама, спусковая скоба, спуск.
            kit.Box(Metal, new Vector3(0f, -top - 0.006f, 0.0f), new Vector3(0.03f, 0.02f, 0.19f), Quaternion.identity);
            kit.Box(Metal, new Vector3(0f, -top - 0.05f, -0.005f), new Vector3(0.008f, 0.008f, 0.075f), Quaternion.identity);
            kit.Box(Metal, new Vector3(0f, -top - 0.03f, 0.03f), new Vector3(0.008f, 0.045f, 0.008f), Quaternion.identity);
            kit.Box(Dark, new Vector3(0f, -top - 0.03f, -0.005f), new Vector3(0.006f, 0.03f, 0.01f),
                Quaternion.Euler(15f, 0f, 0f));

            // Пистолетная рукоять — полимер, наклонена назад.
            kit.Box(Polymer, new Vector3(0f, -top - 0.075f, -0.07f), new Vector3(0.034f, 0.12f, 0.048f),
                Quaternion.Euler(-18f, 0f, 0f));

            // Цевьё вокруг ствола и передняя рукоять под ним.
            kit.Box(Polymer, new Vector3(0f, barrelY - 0.004f, 0.25f), new Vector3(0.072f, 0.052f, 0.14f), Quaternion.identity);
            kit.Cylinder(Polymer, new Vector3(0f, barrelY - 0.03f, 0.26f), new Vector3(0f, -0.085f, 0.265f), 0.018f, 0.016f, 12);

            // Приклад: труба назад и затыльник. В кадре виден краем — у правого нижнего угла.
            kit.Cylinder(Metal, new Vector3(0f, -0.02f, -0.086f), new Vector3(0f, -0.035f, -0.3f), 0.013f, 0.013f, 10);
            kit.Box(Polymer, new Vector3(0f, -0.05f, -0.31f), new Vector3(0.04f, 0.1f, 0.025f), Quaternion.identity);

            return kit.Build("Grenade Launcher");
        }

        /// <summary>Барабан: шесть камор по кругу, пазы между ними, латунные донца гранат сзади.</summary>
        public static Mesh BuildDrum()
        {
            var kit = new MeshKit(3);

            kit.Cylinder(Metal, new Vector3(0f, 0f, -0.07f), new Vector3(0f, 0f, 0.072f), DrumRadius, DrumRadius, 36);

            for (var i = 0; i < 6; i++)
            {
                var angle = i * Mathf.PI / 3f;
                var at = new Vector3(Mathf.Sin(angle), Mathf.Cos(angle), 0f);

                // Камора: чернёное дуло спереди, латунное донце гранаты сзади.
                kit.Disc(Dark, at * ChamberRing + new Vector3(0f, 0f, 0.0725f), Vector3.forward, 0.021f, 16);
                kit.Disc(Brass, at * ChamberRing + new Vector3(0f, 0f, -0.0705f), Vector3.back, 0.02f, 16);

                // Паз между каморами — полоса на боку барабана.
                var between = angle + Mathf.PI / 6f;
                var side = new Vector3(Mathf.Sin(between), Mathf.Cos(between), 0f);

                kit.Box(Dark, side * (DrumRadius - 0.002f), new Vector3(0.012f, 0.006f, 0.11f),
                    Quaternion.LookRotation(Vector3.forward, side));
            }

            return kit.Build("Grenade Launcher Drum");
        }

        /// <summary>Граната 40 мм: оливковый корпус, латунный поясок, серебристая головка.</summary>
        public static Mesh BuildGrenade()
        {
            var kit = new MeshKit(2);

            kit.Cylinder(0, new Vector3(0f, 0f, -0.035f), new Vector3(0f, 0f, 0.012f), 0.02f, 0.02f, 14);
            kit.Cylinder(1, new Vector3(0f, 0f, 0.012f), new Vector3(0f, 0f, 0.018f), 0.021f, 0.021f, 14, false, false);
            kit.Cylinder(0, new Vector3(0f, 0f, 0.018f), new Vector3(0f, 0f, 0.045f), 0.02f, 0.007f, 14, false, true);

            return kit.Build("Grenade");
        }

        /// <summary>Квадрат со стороной 1, лицом назад, к игроку. Точка коллиматора.</summary>
        public static Mesh BuildQuad()
        {
            var mesh = new Mesh { name = "Sight Dot", hideFlags = HideFlags.DontSave };

            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f)
            };

            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
        }

        /// <summary>
        /// Вспышка у дула: две скрещённые плоскости вдоль ствола и одна поперёк. Вдоль — чтобы
        /// сбоку вспышка читалась языком пламени, поперёк — чтобы спереди звездой.
        /// </summary>
        public static Mesh BuildMuzzleFlash()
        {
            var mesh = new Mesh { name = "Muzzle Flash", hideFlags = HideFlags.DontSave };

            var vertices = new Vector3[12];
            var uvs = new Vector2[12];
            var triangles = new int[18];

            void Plane(int index, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                var v = index * 4;

                vertices[v] = a;
                vertices[v + 1] = b;
                vertices[v + 2] = c;
                vertices[v + 3] = d;

                uvs[v] = new Vector2(0f, 0f);
                uvs[v + 1] = new Vector2(1f, 0f);
                uvs[v + 2] = new Vector2(1f, 1f);
                uvs[v + 3] = new Vector2(0f, 1f);

                var t = index * 6;

                triangles[t] = v;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }

            const float w = 0.5f;

            Plane(0, new Vector3(-w, 0f, 0f), new Vector3(w, 0f, 0f), new Vector3(w, 0f, 1.6f), new Vector3(-w, 0f, 1.6f));
            Plane(1, new Vector3(0f, -w, 0f), new Vector3(0f, w, 0f), new Vector3(0f, w, 1.6f), new Vector3(0f, -w, 1.6f));
            Plane(2, new Vector3(-w, -w, 0.1f), new Vector3(w, -w, 0.1f), new Vector3(w, w, 0.1f), new Vector3(-w, w, 0.1f));

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
        }
    }
}
