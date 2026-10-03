using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Тело игрока глазами врага: куда целиться, во что попадать, чем толкнуть и спутать.
    ///
    /// Матка, а дальше особые пауки (M3) работают через этот интерфейс, а не через стенд:
    /// так враг не знает про камеру, ввод и отладку, и тело можно подменить в прогоне.
    /// Сериализованные ссылки остаются на <see cref="CatacombTestRig"/> — интерфейсы Unity
    /// не сериализует.
    /// </summary>
    public interface IPlayerBody
    {
        /// <summary>Центр капсулы в мире.</summary>
        Vector3 BodyCentre { get; }

        /// <summary>Ось капсулы (между центрами полусфер) и её радиус — для попаданий.</summary>
        void GetBodySegment(out Vector3 low, out Vector3 high, out float radius);

        /// <summary>Толчок: вертикальная часть — подброс, горизонтальная — снос.</summary>
        void Push(Vector3 velocity);

        /// <summary>Спутать: ходьба медленнее на время.</summary>
        void Entangle(float seconds, float speedScale);

        bool IsEntangled { get; }
    }
}
