using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Модели семи пушек M2 из примитивов (<see cref="MeshKit"/>), как <see cref="LauncherModel"/>.
    /// Покупных моделей оружия в проекте нет; своя модель встанет на место билдера без правки
    /// логики — точка дула у каждой названа константой.
    ///
    /// Метры, ось Z — вперёд по стволу, начало — у рукояти. Подмеши: 0 — металл, 1 — рукоять
    /// (полимер, дерево), 2 — чернь (каналы, пазы), 3 — акцент: то, что светится или краснеет
    /// (катушки, ствол под нагревом) — его материал с эмиссией, яркость задаётся блоком свойств.
    /// </summary>
    public static class GunModels
    {
        public const int Metal = 0;
        public const int Grip = 1;
        public const int Dark = 2;
        public const int Accent = 3;

        public static readonly Vector3 RevolverMuzzle = new Vector3(0f, 0.035f, 0.30f);
        public static readonly Vector3 ShotgunMuzzle = new Vector3(0f, 0.03f, 0.52f);
        public static readonly Vector3 NailgunMuzzle = new Vector3(0f, 0.02f, 0.40f);
        public static readonly Vector3 RailMuzzle = new Vector3(0f, 0.03f, 0.62f);
        public static readonly Vector3 FlamerMuzzle = new Vector3(0f, 0.0f, 0.48f);
        public static readonly Vector3 TeslaMuzzle = new Vector3(0f, 0.04f, 0.42f);

        /// <summary>Револьвер: ствол, барабан на шесть, рамка, курок, рукоять.</summary>
        public static Mesh Revolver()
        {
            var kit = new MeshKit(4);

            kit.Cylinder(Metal, new Vector3(0f, 0.035f, 0.08f), new Vector3(0f, 0.035f, 0.30f), 0.014f, 0.014f, 16);
            kit.Box(Metal, new Vector3(0f, 0.052f, 0.19f), new Vector3(0.012f, 0.012f, 0.22f), Quaternion.identity);
            kit.Disc(Dark, new Vector3(0f, 0.035f, 0.301f), Vector3.forward, 0.009f, 12);

            // Барабан — отдельной частью (Cylinder), чтобы проворачивался.
            kit.Box(Metal, new Vector3(0f, 0.02f, 0.02f), new Vector3(0.03f, 0.07f, 0.12f), Quaternion.identity);
            kit.Box(Metal, new Vector3(0f, 0.07f, -0.035f), new Vector3(0.012f, 0.02f, 0.03f), Quaternion.Euler(-30f, 0f, 0f));
            kit.Box(Grip, new Vector3(0f, -0.06f, -0.04f), new Vector3(0.032f, 0.12f, 0.045f), Quaternion.Euler(18f, 0f, 0f));
            kit.Box(Accent, new Vector3(0f, 0.065f, 0.28f), new Vector3(0.004f, 0.01f, 0.006f), Quaternion.identity);

            return kit.Build("Revolver");
        }

        public static Mesh RevolverCylinder()
        {
            var kit = new MeshKit(4);

            kit.Cylinder(Metal, new Vector3(0f, 0f, -0.03f), new Vector3(0f, 0f, 0.03f), 0.03f, 0.03f, 6);

            for (var i = 0; i < 6; i++)
            {
                var a = i * Mathf.PI / 3f;
                kit.Disc(Dark, new Vector3(Mathf.Cos(a) * 0.017f, Mathf.Sin(a) * 0.017f, 0.0305f), Vector3.forward, 0.006f, 8);
            }

            return kit.Build("Revolver Cylinder");
        }

        /// <summary>Дробовик: толстый ствол над подствольным магазином, цевьё-помпа, приклад.</summary>
        public static Mesh Shotgun()
        {
            var kit = new MeshKit(4);

            kit.Cylinder(Metal, new Vector3(0f, 0.03f, 0.06f), new Vector3(0f, 0.03f, 0.52f), 0.022f, 0.022f, 18);
            kit.Disc(Dark, new Vector3(0f, 0.03f, 0.521f), Vector3.forward, 0.016f, 14);
            kit.Cylinder(Metal, new Vector3(0f, -0.01f, 0.06f), new Vector3(0f, -0.01f, 0.46f), 0.016f, 0.016f, 14);
            kit.Box(Metal, new Vector3(0f, 0.01f, 0.0f), new Vector3(0.05f, 0.08f, 0.16f), Quaternion.identity);
            kit.Box(Grip, new Vector3(0f, -0.07f, -0.06f), new Vector3(0.036f, 0.11f, 0.05f), Quaternion.Euler(20f, 0f, 0f));
            kit.Box(Grip, new Vector3(0f, -0.02f, -0.2f), new Vector3(0.04f, 0.06f, 0.16f), Quaternion.Euler(-6f, 0f, 0f));
            kit.Box(Accent, new Vector3(0f, 0.058f, 0.5f), new Vector3(0.005f, 0.008f, 0.01f), Quaternion.identity);

            return kit.Build("Shotgun");
        }

        /// <summary>Цевьё-помпа отдельно: ходит назад-вперёд на каждом выстреле.</summary>
        public static Mesh ShotgunPump()
        {
            var kit = new MeshKit(4);

            kit.Cylinder(Grip, new Vector3(0f, -0.01f, 0.22f), new Vector3(0f, -0.01f, 0.36f), 0.026f, 0.026f, 14);

            for (var i = 0; i < 4; i++)
            {
                var z = 0.235f + i * 0.03f;
                kit.Cylinder(Dark, new Vector3(0f, -0.01f, z), new Vector3(0f, -0.01f, z + 0.008f), 0.028f, 0.028f, 14);
            }

            return kit.Build("Shotgun Pump");
        }

        /// <summary>Гвоздомёт: короб с рёбрами охлаждения, ствол-трубка, накопитель гвоздей.</summary>
        public static Mesh Nailgun()
        {
            var kit = new MeshKit(4);

            kit.Box(Metal, new Vector3(0f, 0.01f, 0.08f), new Vector3(0.06f, 0.07f, 0.22f), Quaternion.identity);

            for (var i = 0; i < 5; i++)
            {
                var z = 0.02f + i * 0.035f;
                kit.Box(Dark, new Vector3(0f, 0.05f, z), new Vector3(0.064f, 0.01f, 0.012f), Quaternion.identity);
            }

            // Ствол — подмеш акцента: он краснеет от нагрева.
            kit.Cylinder(Accent, new Vector3(0f, 0.02f, 0.19f), new Vector3(0f, 0.02f, 0.40f), 0.012f, 0.012f, 12);
            kit.Disc(Dark, new Vector3(0f, 0.02f, 0.401f), Vector3.forward, 0.007f, 10);
            kit.Cylinder(Metal, new Vector3(0f, -0.05f, 0.04f), new Vector3(0f, -0.05f, 0.16f), 0.03f, 0.03f, 16);
            kit.Box(Grip, new Vector3(0f, -0.07f, -0.05f), new Vector3(0.034f, 0.12f, 0.045f), Quaternion.Euler(16f, 0f, 0f));

            return kit.Build("Nailgun");
        }

        /// <summary>Рельса: две направляющие, катушки между ними (светятся зарядом), корпус, рукоять.</summary>
        public static Mesh Rail()
        {
            var kit = new MeshKit(4);

            kit.Box(Metal, new Vector3(0f, 0.0f, 0.02f), new Vector3(0.07f, 0.09f, 0.22f), Quaternion.identity);
            kit.Box(Metal, new Vector3(0.025f, 0.03f, 0.36f), new Vector3(0.012f, 0.03f, 0.52f), Quaternion.identity);
            kit.Box(Metal, new Vector3(-0.025f, 0.03f, 0.36f), new Vector3(0.012f, 0.03f, 0.52f), Quaternion.identity);

            for (var i = 0; i < 6; i++)
            {
                var z = 0.15f + i * 0.075f;
                kit.Cylinder(Accent, new Vector3(0f, 0.03f, z), new Vector3(0f, 0.03f, z + 0.03f), 0.03f, 0.03f, 14);
            }

            kit.Box(Grip, new Vector3(0f, -0.08f, -0.03f), new Vector3(0.036f, 0.12f, 0.048f), Quaternion.Euler(16f, 0f, 0f));
            kit.Box(Grip, new Vector3(0f, -0.01f, -0.2f), new Vector3(0.04f, 0.06f, 0.18f), Quaternion.identity);

            return kit.Build("Rail");
        }

        /// <summary>Огнемёт: баллон снизу, сопло с запальником, рукояти.</summary>
        public static Mesh Flamer()
        {
            var kit = new MeshKit(4);

            kit.Cylinder(Metal, new Vector3(0f, 0f, 0.0f), new Vector3(0f, 0f, 0.42f), 0.03f, 0.022f, 16);
            kit.Cylinder(Dark, new Vector3(0f, 0f, 0.42f), new Vector3(0f, 0f, 0.48f), 0.03f, 0.036f, 16, true, false);
            kit.Cylinder(Accent, new Vector3(0f, -0.04f, 0.4f), new Vector3(0f, -0.04f, 0.47f), 0.006f, 0.006f, 8);
            kit.Cylinder(Grip, new Vector3(0f, -0.07f, -0.02f), new Vector3(0f, -0.07f, 0.24f), 0.045f, 0.045f, 18);
            kit.Box(Grip, new Vector3(0f, -0.08f, -0.08f), new Vector3(0.034f, 0.12f, 0.045f), Quaternion.Euler(16f, 0f, 0f));
            kit.Box(Grip, new Vector3(0f, -0.06f, 0.3f), new Vector3(0.03f, 0.1f, 0.035f), Quaternion.Euler(8f, 0f, 0f));

            return kit.Build("Flamer");
        }

        /// <summary>Тесла: катушка Тесла на стволе, рога-разрядники, батарея сбоку.</summary>
        public static Mesh Tesla()
        {
            var kit = new MeshKit(4);

            kit.Box(Metal, new Vector3(0f, 0f, 0.05f), new Vector3(0.06f, 0.08f, 0.2f), Quaternion.identity);
            kit.Cylinder(Accent, new Vector3(0f, 0.04f, 0.16f), new Vector3(0f, 0.04f, 0.34f), 0.034f, 0.026f, 16);
            kit.Cylinder(Metal, new Vector3(0.02f, 0.06f, 0.34f), new Vector3(0.03f, 0.08f, 0.42f), 0.005f, 0.003f, 6);
            kit.Cylinder(Metal, new Vector3(-0.02f, 0.06f, 0.34f), new Vector3(-0.03f, 0.08f, 0.42f), 0.005f, 0.003f, 6);
            kit.Box(Dark, new Vector3(0.045f, -0.01f, 0.04f), new Vector3(0.025f, 0.05f, 0.11f), Quaternion.identity);
            kit.Box(Grip, new Vector3(0f, -0.08f, -0.04f), new Vector3(0.034f, 0.12f, 0.045f), Quaternion.Euler(16f, 0f, 0f));

            return kit.Build("Tesla");
        }

        /// <summary>
        /// Кирка-молот: рукоять, с одной стороны боёк молота, с другой — клюв кирки.
        /// Держится за конец рукояти; ось рукояти — вверх по Y, боёк вдоль Z.
        /// </summary>
        public static Mesh Hammer()
        {
            var kit = new MeshKit(4);

            kit.Cylinder(Grip, new Vector3(0f, -0.05f, 0f), new Vector3(0f, 0.55f, 0f), 0.018f, 0.016f, 12);
            kit.Box(Metal, new Vector3(0f, 0.55f, 0.06f), new Vector3(0.07f, 0.07f, 0.12f), Quaternion.identity);
            kit.Cylinder(Metal, new Vector3(0f, 0.55f, -0.01f), new Vector3(0f, 0.55f, -0.22f), 0.03f, 0.004f, 8);
            kit.Box(Dark, new Vector3(0f, 0.55f, 0.121f), new Vector3(0.072f, 0.072f, 0.004f), Quaternion.identity);
            kit.Box(Accent, new Vector3(0f, 0.08f, 0f), new Vector3(0.04f, 0.1f, 0.04f), Quaternion.identity);

            return kit.Build("Pick Hammer");
        }

        // ------------------------------------------------------------------ снаряды

        public static Mesh Coin()
        {
            var kit = new MeshKit(1);
            kit.Cylinder(0, new Vector3(0f, 0f, -0.006f), new Vector3(0f, 0f, 0.006f), 0.06f, 0.06f, 16);
            return kit.Build("Coin");
        }

        public static Mesh Nail()
        {
            var kit = new MeshKit(1);
            kit.Cylinder(0, new Vector3(0f, 0f, -0.07f), new Vector3(0f, 0f, 0.07f), 0.008f, 0.002f, 6);
            kit.Disc(0, new Vector3(0f, 0f, -0.07f), Vector3.back, 0.016f, 8);
            return kit.Build("Nail");
        }

        /// <summary>Ядро дробовика — граник, не гладкий шар (грабли №9: гладкое читается пластиком).</summary>
        public static Mesh Core()
        {
            var kit = new MeshKit(1);
            kit.Rock(0, Vector3.zero, 0.09f, 11);
            return kit.Build("Shotgun Core");
        }

        public static Mesh Magnet()
        {
            var kit = new MeshKit(2);
            kit.Box(0, new Vector3(0.03f, 0f, 0f), new Vector3(0.025f, 0.025f, 0.09f), Quaternion.identity);
            kit.Box(0, new Vector3(-0.03f, 0f, 0f), new Vector3(0.025f, 0.025f, 0.09f), Quaternion.identity);
            kit.Box(1, new Vector3(0f, 0f, -0.05f), new Vector3(0.085f, 0.025f, 0.025f), Quaternion.identity);
            return kit.Build("Magnet");
        }

        public static Mesh Rod()
        {
            var kit = new MeshKit(2);
            kit.Cylinder(0, new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0.6f), 0.02f, 0.012f, 8);
            kit.Cylinder(1, new Vector3(0f, 0f, 0.45f), new Vector3(0f, 0f, 0.6f), 0.05f, 0.05f, 10);
            return kit.Build("Lightning Rod");
        }

        /// <summary>Болт рельсы (отвёртка).</summary>
        public static Mesh Bolt()
        {
            var kit = new MeshKit(1);
            kit.Cylinder(0, new Vector3(0f, 0f, -0.18f), new Vector3(0f, 0f, 0.18f), 0.014f, 0.004f, 8);
            return kit.Build("Rail Bolt");
        }
    }
}
