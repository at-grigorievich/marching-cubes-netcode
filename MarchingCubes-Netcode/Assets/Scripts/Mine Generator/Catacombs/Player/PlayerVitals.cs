using System;
using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Здоровье игрока: основное, временное, броня, регенерация, возрождения, неуязвимость.
    ///
    /// Вся арифметика — в <see cref="VitalsModel"/> (чистый класс, тесты вне Unity). Здесь —
    /// время, события и связь со сценой: временное здоровье за убийства вплотную, статы забега.
    ///
    /// Компонент сцены на теле игрока рядом со стендом, пересоздаётся «Починить свет»
    /// (грабли №8, №67): числа живут в сцене, и правка умолчаний иначе до неё не дойдёт.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CatacombTestRig))]
    public sealed class PlayerVitals : MonoBehaviour
    {
        [Tooltip("Здоровье без статов забега. Класс, мета и тома прибавляют сверху (PlayerStats).")]
        [SerializeField, Range(10f, 1000f)] private float baseMaxHealth = 100f;

        /// <summary>
        /// Потолок урона в секунду, доля максимума. Без него куча в несколько ярусов убивает
        /// за долю секунды, и смерть читается как сбой, а не как ошибка игрока. С ним
        /// окружённый без брони живёт несколько секунд — время на рывок.
        ///
        /// План ставил 0.22 и четверть сверх потолка, считая, что у игрока бьют около десятка.
        /// Замер SurvivalCheck: на первом гребне у стоящего бьют 33–36 (ярусы кучи кусают тоже),
        /// укусов 35 в секунду, 95 HP/с до потолка, в финальном рое 180 HP/с. Плановые числа
        /// давали бы 22 + 0.25 × 73 = 40 HP/с, то есть смерть за 2.5 с уже на гребне.
        /// Потолок 0.15 и 0.07 сверх него: 4.6 с на гребне и 3.5 с в финальном рое — то, что
        /// заказано (4–7 и 2.5–4), и разница между гребнем и финалом остаётся ощутимой.
        /// </summary>
        [Tooltip("Сколько урона за скользящую секунду проходит в полную силу, доля максимума здоровья.")]
        [SerializeField, Range(0.05f, 1f)] private float capShare = 0.15f;

        [Tooltip("С какой силой проходит урон сверх потолка.")]
        [SerializeField, Range(0f, 1f)] private float overflowScale = 0.07f;

        [Header("Временное здоровье (Vermintide 2)")]
        [Tooltip("Убийство ближе этого даёт временное здоровье, юниты.")]
        [SerializeField, Range(1f, 10f)] private float closeKillRadius = 4f;

        [Tooltip("Сколько временного здоровья за убийство вплотную, HP. Пушки ближнего боя дают больше.")]
        [SerializeField, Range(0f, 5f)] private float tempPerCloseKill = 0.6f;

        [Tooltip("Через сколько секунд без пополнения временное здоровье начинает таять.")]
        [SerializeField, Range(0f, 10f)] private float tempDelay = 3f;

        [Tooltip("Как быстро тает временное здоровье, HP в секунду.")]
        [SerializeField, Range(0f, 20f)] private float tempDecay = 2f;

        [Header("Возрождение")]
        [SerializeField, Range(0.1f, 1f)] private float reviveShare = 0.5f;

        [SerializeField, Range(0f, 10f)] private float reviveInvulnerable = 2f;

        [Tooltip("Паника толпы при возрождении, радиус: иначе возрождённого загрызают на том же кадре.")]
        [SerializeField, Range(0f, 40f)] private float revivePanicRadius = 12f;

        private readonly VitalsModel _model = new VitalsModel();
        private CatacombTestRig _rig;
        private SpiderCrowd _boundCrowd;

        /// <summary>
        /// Множитель временного здоровья по источнику убийства: дробовик и кирка дают больше,
        /// чем граната. Заполняют пушки (M2) — здесь только чтение.
        /// </summary>
        private static readonly float[] TempBySource = BuildTempTable();

        public VitalsModel Model => _model;

        public float Health => _model.Health;
        public float MaxHealth => _model.MaxHealth;
        public float Temp => _model.Temp;
        public float Fraction => _model.Fraction;
        public bool IsDead => _model.Dead;
        public bool IsInvulnerable => _model.Invulnerable;
        public int Revives => _model.Revives;

        /// <summary>Сколько секунд подряд здоровье ниже четверти — для передышки из милосердия.</summary>
        public float LowHealthSeconds { get; private set; }

        /// <summary>Удар прошёл: что пришло и что сделало.</summary>
        public event Action<DamageHit, DamageResult> Damaged;

        public event Action Died;

        public event Action Revived;

        /// <summary>Сколько раз возрождался в этом забеге.</summary>
        public int RevivesUsed { get; private set; }

        private static float[] BuildTempTable()
        {
            var table = new float[(int)DamageSource.Count];
            for (var i = 0; i < table.Length; i++) table[i] = 1f;
            return table;
        }

        /// <summary>Пушка ближнего боя объявляет, во сколько раз её убийства вплотную лечат сильнее.</summary>
        public static void SetTempScale(DamageSource source, float scale)
        {
            if ((int)source < TempBySource.Length) TempBySource[(int)source] = scale;
        }

        private void Awake()
        {
            _rig = GetComponent<CatacombTestRig>();
            ApplyTuning();
            _model.Reset(baseMaxHealth);
        }

        private void OnEnable() => Bind();

        private void OnDisable() => Unbind();

        /// <summary>Подписка на убийства толпы. Отдельно от OnEnable — грабли №17: прогоны живут в редакторе.</summary>
        public void Bind()
        {
            if (_rig == null) _rig = GetComponent<CatacombTestRig>();

            var crowd = _rig != null ? _rig.Crowd : null;
            if (crowd == _boundCrowd) return;

            Unbind();

            if (crowd == null) return;

            crowd.Killed += OnKilled;
            crowd.Bitten += OnBitten;
            _boundCrowd = crowd;
        }

        private void Unbind()
        {
            if (_boundCrowd == null) return;

            _boundCrowd.Killed -= OnKilled;
            _boundCrowd.Bitten -= OnBitten;
            _boundCrowd = null;
        }

        private void ApplyTuning()
        {
            _model.CapShare = capShare;
            _model.OverflowScale = overflowScale;
            _model.TempDelay = tempDelay;
            _model.TempDecay = tempDecay;
        }

        /// <summary>Начало забега: полное здоровье. Статы приходят отдельно — <see cref="ApplyStats"/>.</summary>
        public void ResetForRun()
        {
            ApplyTuning();
            _model.Reset(baseMaxHealth);
            LowHealthSeconds = 0f;
            RevivesUsed = 0;
        }

        /// <summary>Статы забега: максимум, броня, регенерация, возрождения.</summary>
        public void ApplyStats(PlayerStats stats)
        {
            if (stats == null) return;

            _model.SetMaxHealth(stats.MaxHealth(baseMaxHealth));
            _model.Armor = stats.Armor;
            _model.Regen = stats.Regen;
            _model.Revives = stats.Revives - RevivesUsed;
        }

        private void Update()
        {
            if (!Application.isPlaying) return;

            Tick(Time.deltaTime);
        }

        /// <summary>Шаг: регенерация, таяние временного, неуязвимость. Отдельно — для прогонов.</summary>
        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f) return;

            Bind();
            _model.Tick(deltaTime);

            LowHealthSeconds = !_model.Dead && _model.Fraction < 0.25f ? LowHealthSeconds + deltaTime : 0f;
        }

        public DamageResult TakeDamage(in DamageHit hit)
        {
            var result = _model.TakeDamage(hit);

            if (result.Dealt > 0f || result.Blocked) Damaged?.Invoke(hit, result);

            if (result.Killed) Died?.Invoke();

            return result;
        }

        public void Heal(float amount) => _model.Heal(amount);

        public void AddTemp(float amount) => _model.AddTemp(amount);

        public void HoldInvulnerable(InvulnReason reason, float seconds) => _model.HoldInvulnerable(reason, seconds);

        public void ReleaseInvulnerable(InvulnReason reason) => _model.ReleaseInvulnerable(reason);

        /// <summary>
        /// Возрождение. Платное (заряд <see cref="Revives"/>) или за рекламу — решает звонящий.
        /// Толпа вокруг паникует: иначе возрождённого загрызают на том же кадре.
        /// </summary>
        public void Revive(bool spendCharge)
        {
            if (!_model.Dead) return;

            if (spendCharge && _model.Revives > 0) _model.Revives--;

            RevivesUsed++;
            _model.Revive(reviveShare, reviveInvulnerable);
            LowHealthSeconds = 0f;

            var crowd = _rig != null ? _rig.Crowd : null;
            if (crowd != null && revivePanicRadius > 0f) crowd.Panic(_rig.BodyCentre, revivePanicRadius, 4f);

            Revived?.Invoke();
        }

        /// <summary>Отладка и прогоны: убить сразу.</summary>
        public void Kill()
        {
            if (_model.Dead) return;

            _model.Kill();
            Died?.Invoke();
        }

        private void OnBitten(DamageHit hit) => TakeDamage(hit);

        /// <summary>Убийства вплотную дают временное здоровье. Пачка за кадр — один вызов.</summary>
        private void OnKilled(IReadOnlyList<KillRecord> kills)
        {
            if (_model.Dead || tempPerCloseKill <= 0f || _rig == null) return;

            var body = _rig.BodyCentre;
            var radiusSq = closeKillRadius * closeKillRadius;
            var gain = 0f;

            for (var i = 0; i < kills.Count; i++)
            {
                var kill = kills[i];

                if ((kill.Position - body).sqrMagnitude > radiusSq) continue;

                var source = (int)kill.Source;
                gain += tempPerCloseKill * (source < TempBySource.Length ? TempBySource[source] : 1f);
            }

            if (gain > 0f) _model.AddTemp(gain);
        }
    }
}
