using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Подвижность игрока: рывок и «толпа держит».
    ///
    /// Рывок — главное защитное действие. Ultrakill и Megabonk держатся на подвижности,
    /// а в коридорной давке без рывка выход один — умереть. Поэтому он есть до оружия.
    ///
    /// «Толпа держит»: у пауков нет коллайдеров, и капсула игрока проходила сквозь толпу —
    /// окружение не было ловушкой вовсе. Теперь каждый, кто касается капсулы, замедляет
    /// ходьбу; рывок это замедление не замечает. Опрос каждый кадр, как у паутины, а не
    /// события входа-выхода: держит тот, кто касается СЕЙЧАС.
    ///
    /// Числа — в этом компоненте, а не в стенде: стенд сцена хранит давно, и правка его
    /// умолчаний до неё не доходит (грабли №8). Этот пересоздаёт «Починить свет».
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CatacombTestRig))]
    public sealed class PlayerMobility : MonoBehaviour
    {
        [Header("Рывок")]
        [SerializeField, Range(1f, 15f)] private float dashDistance = 5.5f;
        [SerializeField, Range(0.05f, 0.6f)] private float dashDuration = 0.16f;

        [Tooltip("Восстановление одного заряда, секунды.")]
        [SerializeField, Range(0.2f, 10f)] private float dashRecharge = 1.6f;

        [Tooltip("Неуязвимость сверх длительности рывка, секунды.")]
        [SerializeField, Range(0f, 0.5f)] private float dashGrace = 0.08f;

        [Header("Толпа держит")]
        [Tooltip("На сколько замедляет каждый, кто касается капсулы.")]
        [SerializeField, Range(0f, 0.5f)] private float holdPerSpider = 0.09f;

        [Tooltip("Медленнее этой доли толпа не держит.")]
        [SerializeField, Range(0f, 1f)] private float holdFloor = 0.35f;

        [Tooltip("Запас к радиусу капсулы, в котором паук считается касающимся, юниты.")]
        [SerializeField, Range(0f, 2f)] private float holdReach = 0.6f;

        private readonly DashModel _dash = new DashModel();

        public DashModel Dash => _dash;

        /// <summary>Сколько особей касается капсулы на этом кадре.</summary>
        public int Touching { get; private set; }

        /// <summary>Множитель ходьбы от толпы на этом кадре.</summary>
        public float HoldScale { get; private set; } = 1f;

        private void Awake() => ApplyTuning();

        private void ApplyTuning()
        {
            _dash.Distance = dashDistance;
            _dash.Duration = dashDuration;
            _dash.Recharge = dashRecharge;
            _dash.Grace = dashGrace;
        }

        public void ResetForRun()
        {
            ApplyTuning();
            _dash.Reset();
        }

        public void ApplyStats(PlayerStats stats)
        {
            if (stats != null) _dash.SetMaxCharges(stats.DashCharges);
        }

        /// <summary>Сколько держит толпа: опрос касающихся капсулы.</summary>
        public float MeasureHold(SpiderCrowd crowd, IPlayerBody body)
        {
            Touching = 0;
            HoldScale = 1f;

            if (crowd == null || body == null || holdPerSpider <= 0f) return 1f;

            body.GetBodySegment(out var low, out var high, out var radius);

            Touching = crowd.CountTouching(low, high, radius + holdReach);
            HoldScale = Mathf.Max(holdFloor, 1f - holdPerSpider * Touching);

            return HoldScale;
        }
    }
}
