using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MineGenerator.Catacombs.EditorTools
{
    /// <summary>
    /// Приводит тестовую сцену к умолчаниям из кода и СОХРАНЯЕТ её.
    ///
    /// Существует ровно из-за граблей №8: значения полей живут в сохранённой сцене,
    /// и правка умолчаний в коде до них не доходит. Пункт меню «Починить свет»
    /// компоненты пересоздаёт, но сцену не сохраняет — он рассчитан на человека,
    /// который нажмёт Ctrl+S. При закрытом редакторе нажимать некому.
    ///
    /// На этом уже споткнулись: паника после вспышки была удлинена в коде с пяти секунд
    /// до десяти, прогон (он пересоздаёт компоненты сам) показывал разбегание на 74%,
    /// а в игре по-прежнему стояло сохранённое `panicTime: 5` — и пользователь видел,
    /// что толпа никуда не девается. Прогон и игра расходились не механикой, а тем,
    /// откуда каждый берёт числа.
    /// </summary>
    public static class CatacombSceneRefresh
    {
        private const string ScenePath = "Assets/Scenes/test.unity";
        private const string StyleMenu = "Tools/Mine Generator/Свет: как в DRG (тьма и цвет)";

        public static void Run()
        {
            var code = 0;

            try
            {
                Report();
            }
            catch (Exception error)
            {
                Debug.LogError("ОБНОВЛЕНИЕ СЦЕНЫ УПАЛО: " + error);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        /// <summary>То же без выхода из процесса — для вызова при открытом редакторе.</summary>
        public static void Report()
        {
            // Открываем сцену, только если открыта другая. Перезагрузка уже открытой
            // выбросила бы несохранённые правки пользователя без спроса — а инструмент
            // зовут как раз тогда, когда человек сидит в этой сцене и тестирует.
            var scene = EditorSceneManager.GetActiveScene();

            if (scene.path != ScenePath) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            if (!EditorApplication.ExecuteMenuItem(StyleMenu)) throw new Exception("не найден пункт меню: " + StyleMenu);

            var generators = UnityEngine.Object.FindFirstObjectByType<CaveGenerators>();
            if (generators == null) throw new Exception("CaveGenerators не появился — починка света не отработала");

            // Ссылку на толпу «Починить свет» проставляет только если толпа уже есть
            // на сцене к моменту пересоздания генераторов. Порядок не гарантирован,
            // поэтому связываем ещё раз и уже наверняка.
            var crowd = UnityEngine.Object.FindFirstObjectByType<SpiderCrowd>();

            if (crowd != null)
            {
                var serialized = new SerializedObject(generators);
                serialized.FindProperty("crowd").objectReferenceValue = crowd;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            var fixtures = UnityEngine.Object.FindFirstObjectByType<CaveFixtures>();

            Debug.Log($"СЦЕНА ОБНОВЛЕНА и сохранена: генераторов {generators.Count}, " +
                      $"светильников {(fixtures != null ? fixtures.Count : 0)}, " +
                      $"толпа {(crowd != null ? "привязана" : "НЕ НАЙДЕНА")}");
        }
    }
}
