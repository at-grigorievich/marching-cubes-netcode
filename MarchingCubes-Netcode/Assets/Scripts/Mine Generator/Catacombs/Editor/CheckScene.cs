using System;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Общая сборка сцены для прогонов M1 и дальше: открыть тестовую сцену, пересоздать
    /// компоненты «Починить свет» (грабли №8, №25), собрать уровень, пересоздать толпу,
    /// поставить игрока на точку входа (грабли №30).
    ///
    /// Шаг симуляции — <see cref="Advance"/>: директор, Матка, толпа, статус игрока и его
    /// здоровье, раздача убийств — то, что в игре делают Update'ы, а в прогоне никто.
    /// </summary>
    internal sealed class CheckScene
    {
        public const string ScenePath = "Assets/Scenes/test.unity";
        private const string StyleMenu = "Tools/Mine Generator/Свет: как в DRG (тьма и цвет)";

        public CatacombWorld World;
        public SpiderCrowd Crowd;
        public CaveRunDirector Director;
        public CaveQueen Queen;
        public CaveFixtures Fixtures;
        public CatacombTestRig Player;
        public PlayerVitals Vitals;
        public PlayerMobility Mobility;
        public Vector3 Entry;

        /// <summary>Собрать сцену. Null — не вышло; причина уже в тексте отчёта.</summary>
        public static CheckScene Open(StringBuilder text, ref int failed, int? seed = null)
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            if (!EditorApplication.ExecuteMenuItem(StyleMenu)) throw new Exception("не найден пункт меню: " + StyleMenu);

            var scene = new CheckScene
            {
                World = UnityEngine.Object.FindAnyObjectByType<CatacombWorld>(),
                Player = UnityEngine.Object.FindAnyObjectByType<CatacombTestRig>()
            };

            if (scene.World == null) throw new Exception("НЕТ CatacombWorld на сцене " + ScenePath);
            if (scene.Player == null) throw new Exception("НЕТ CatacombTestRig на сцене " + ScenePath);

            scene.World.GenerateImmediate(seed);

            scene.Fixtures = UnityEngine.Object.FindAnyObjectByType<CaveFixtures>();
            scene.Director = UnityEngine.Object.FindAnyObjectByType<CaveRunDirector>();
            scene.Queen = UnityEngine.Object.FindAnyObjectByType<CaveQueen>();
            scene.Vitals = scene.Player.GetComponent<PlayerVitals>();
            scene.Mobility = scene.Player.GetComponent<PlayerMobility>();

            if (scene.Director == null) throw new Exception("НЕТ CaveRunDirector — «Починить свет» его не создал");
            if (scene.Queen == null) throw new Exception("НЕТ CaveQueen — «Починить свет» её не создал");
            if (scene.Vitals == null) throw new Exception("НЕТ PlayerVitals — «Починить свет» его не создал");

            if (!scene.World.TryGetSpawnPoint(out scene.Entry)) throw new Exception("у уровня нет точки входа");

            scene.PlaceBody(scene.Entry);

            scene.Crowd = CatacombCrowdCheck.EnsureCrowd(scene.World, text, ref failed);
            if (scene.Crowd == null) return null;

            scene.Crowd.Rebuild();

            if (!scene.Crowd.HasField)
            {
                text.AppendLine("  ПЛОХО: сетка навигации не построена");
                failed++;
                return null;
            }

            scene.Queen.EnsureLinks();
            scene.Queen.Rebuild();
            scene.Director.EnsureLinks();
            scene.Director.Restart();

            scene.Vitals.Bind();
            scene.Vitals.ResetForRun();
            if (scene.Mobility != null) scene.Mobility.ResetForRun();

            return scene;
        }

        /// <summary>Шаг всего, что в игре идёт своими Update'ами.</summary>
        public void Advance(float deltaTime)
        {
            Director.Tick(deltaTime);
            Queen.Tick(deltaTime);
            Crowd.Simulate(deltaTime);
            Player.TickStatus(deltaTime);
            Vitals.Tick(deltaTime);
            Crowd.FlushKills();
        }

        /// <summary>Ставит стенд так, чтобы подошвы капсулы стояли на полу под точкой.</summary>
        public void PlaceBody(Vector3 floor)
        {
            var controller = Player.GetComponent<CharacterController>();
            var feet = controller.center.y - controller.height * 0.5f;

            Player.transform.rotation = Quaternion.identity;
            Player.transform.position = floor + Vector3.up * (controller.skinWidth - feet + 0.05f);
        }

        /// <summary>Свежая стадия, перемотанная на долю: толпа заселяется заново под неё.</summary>
        public void RestartAt(float stageShare, float warmupSeconds, float step)
        {
            PlaceBody(Entry);

            Director.Restart();
            Crowd.Rebuild();
            Director.SkipAhead(Director.StageSeconds * stageShare - warmupSeconds);

            // Прогрев под неуязвимостью: толпа досылается к цели и сбегается к игроку,
            // а мерить смерть надо уже в окружении, а не по дороге к нему.
            Vitals.ResetForRun();
            Vitals.HoldInvulnerable(InvulnReason.Debug, float.PositiveInfinity);

            for (var time = 0f; time < warmupSeconds; time += step)
            {
                PlaceBody(Entry);
                Advance(step);
            }

            Vitals.ReleaseInvulnerable(InvulnReason.Debug);
        }

        public static void Expect(bool ok, string what, StringBuilder text, ref int failed)
        {
            text.AppendLine(ok ? $"  ок: {what}" : $"  ПЛОХО: {what}");
            if (!ok) failed++;
        }

        public static string Clock(float seconds)
        {
            seconds = Mathf.Max(0f, seconds);
            return $"{(int)(seconds / 60f):00}:{(int)(seconds % 60f):00}";
        }
    }
}
