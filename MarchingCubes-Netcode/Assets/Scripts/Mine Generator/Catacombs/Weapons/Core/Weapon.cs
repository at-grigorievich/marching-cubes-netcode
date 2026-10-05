using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>Что показать про пушку в интерфейсе. Без строк: числа и ключи — HUD не выделяет память.</summary>
    public struct WeaponHud
    {
        public WeaponId Id;
        public int Level;
        public bool Evolved;

        /// <summary>Патроны (гранаты, монеты) — целыми; -1 — у пушки их нет.</summary>
        public int Ammo;
        public int Capacity;

        /// <summary>Ресурс полосой 0..1: нагрев, топливо, батарея, заряд рельсы. -1 — нет.</summary>
        public float Gauge;

        /// <summary>Полоса — перегрев/перезарядка (красная), а не запас.</summary>
        public bool GaugeIsHeat;

        /// <summary>Идёт перезарядка (или блокировка перегрева), доля пройденного 0..1.</summary>
        public float Reload;

        public bool AltUnlocked;

        /// <summary>Готовность альта 0..1 и заряды альта.</summary>
        public float AltReady;
        public int AltCharges;
    }

    /// <summary>
    /// Пушка — обычный класс, а не компонент: три пушки в руках — три объекта логики, а не три
    /// Update с негарантированным порядком. Арсенал зовёт: <see cref="Tick"/> активной,
    /// <see cref="TickBackground"/> всем (перезарядка и остывание идут и у убранной — иначе
    /// смена пушки на время перезарядки не работает как приём), <see cref="TickView"/> — модели.
    /// </summary>
    public abstract class Weapon : IShotHandler
    {
        public WeaponDef Def { get; private set; }
        public WeaponId Id => Def.Id;
        public int Level { get; private set; } = 1;
        public bool Evolved { get; private set; }
        public bool AltUnlocked => Level >= WeaponDef.AltLevel;
        public bool IsEquipped { get; private set; }

        /// <summary>Убийств и урона за забег — статистика пушки, задания (M4), таблица DPS (M2).</summary>
        public int Kills { get; protected set; }
        public float DamageDealt { get; protected set; }

        protected CombatContext Ctx;
        protected WeaponNumbers N;
        protected DamageSource Source;
        protected Transform View;
        protected readonly ViewMotion Motion = new ViewMotion();

        /// <summary>Номер пушки в системе снарядов — по нему снаряд находит хозяина.</summary>
        public int ShotOwner { get; private set; } = -1;

        protected readonly List<Vector3> Gibs = new List<Vector3>();

        public WeaponNumbers Numbers => N;

        public void Setup(WeaponDef def, CombatContext ctx, Transform viewParent)
        {
            Def = def;
            Ctx = ctx;
            Source = WeaponIds.SourceOf(def.Id);

            Refresh();

            View = new GameObject(def.Key + " View") { hideFlags = HideFlags.DontSave }.transform;
            View.SetParent(viewParent, false);

            if (ctx.Projectiles != null) ShotOwner = ctx.Projectiles.Register(this);

            Build(View);
            View.gameObject.SetActive(false);

            PlayerVitals.SetTempScale(Source, N[WeaponStat.TempHealth] > 0f ? N[WeaponStat.TempHealth] : 1f);
        }

        public void SetLevel(int level)
        {
            Level = Mathf.Clamp(level, 1, WeaponDef.MaxLevel);
            Refresh();
        }

        public void Evolve()
        {
            Evolved = true;
            Refresh();
        }

        /// <summary>Пересчитать числа: уровень, эволюция, статы игрока.</summary>
        public void Refresh()
        {
            N = Def.Compute(Level, Evolved, Ctx != null ? Ctx.Stats : null);
            if (Def != null) PlayerVitals.SetTempScale(Source, N[WeaponStat.TempHealth] > 0f ? N[WeaponStat.TempHealth] : 1f);
            OnNumbersChanged();
        }

        protected virtual void OnNumbersChanged() { }

        public virtual void Equip()
        {
            IsEquipped = true;
            if (View != null) View.gameObject.SetActive(true);
            Motion.Raise();
        }

        public virtual void Holster()
        {
            IsEquipped = false;
            if (View != null) View.gameObject.SetActive(false);
        }

        /// <summary>Огонь активной пушки. Зовётся, только когда игрок в игре и ввод не заблокирован.</summary>
        public abstract void Tick(in PlayerCommands commands, float deltaTime);

        /// <summary>Перезарядка, остывание, откаты — у всех трёх пушек, держит игрок её или нет.</summary>
        public virtual void TickBackground(float deltaTime) { }

        /// <summary>Модель от первого лица — только у активной, в LateUpdate.</summary>
        public virtual void TickView(float deltaTime) => Motion.Apply(View, Ctx.Rig, deltaTime);

        public abstract WeaponHud Hud { get; }

        protected abstract void Build(Transform view);

        /// <summary>Снаряд этой пушки ударился, долетел или истёк.</summary>
        public virtual void OnShot(ref Shot shot, ShotEvent what, in RaycastHit hit) { }

        public virtual void Dispose()
        {
            if (View == null) return;

            if (Application.isPlaying) Object.Destroy(View.gameObject);
            else Object.DestroyImmediate(View.gameObject);
        }

        /// <summary>Удар с записью в статистику пушки и маркером у прицела.</summary>
        protected HitReport Strike(HitSpec spec, bool mark = true)
        {
            spec.Source = Source;
            if (spec.CritScale <= 0f) spec.CritScale = 2f;
            spec.CritChance = Mathf.Max(spec.CritChance, N[WeaponStat.Crit]);

            var report = Ctx.Strike(spec, Gibs, mark);

            Kills += report.Killed;
            DamageDealt += report.Dealt;

            return report;
        }

        /// <summary>Брызги разорванных последним ударом — не больше сорока, как у гранаты.</summary>
        protected void SprayGibs(Vector3 from, Vector3 up)
        {
            if (Ctx.Explosions == null) return;

            var count = Mathf.Min(Gibs.Count, 40);

            for (var i = 0; i < count; i++)
            {
                var away = (Gibs[i] - from).normalized + up * 0.6f;
                Ctx.Explosions.Gore(Gibs[i], away);
            }
        }

        protected Transform Eye => Ctx.Eye;

        protected static float Cooldown(float left, float deltaTime) => left > 0f ? Mathf.Max(0f, left - deltaTime) : 0f;
    }

    /// <summary>
    /// Модель живёт: отдача пружиной, отставание от мыши, покачивание на ходу, отвод от стены,
    /// опускание при перезарядке, подъём при смене. Числа — с гранатомёта, где их подбирали
    /// кадрами; у каждой пушки можно подкрутить свои.
    /// </summary>
    public sealed class ViewMotion
    {
        public Vector3 Offset = new Vector3(0.21f, -0.2f, 0.3f);
        public float Sway = 1.2f;

        /// <summary>Отдача: 1 — полный толчок, гаснет пружиной.</summary>
        public float Kick;

        /// <summary>Перезарядка: 0..1, синус опускания.</summary>
        public float Reload;

        /// <summary>Доп. наклон и сдвиг — у пушки свои позы (замах молота, заряд рельсы).</summary>
        public Vector3 ExtraPosition;
        public Vector3 ExtraEuler;

        private Vector2 _sway;
        private float _bobPhase;
        private float _retract;
        private float _raise;
        private Vector3 _lastPosition;
        private bool _hasLast;

        public void Raise() => _raise = 1f;

        public void Apply(Transform view, CatacombTestRig rig, float deltaTime)
        {
            if (view == null || rig == null) return;

            var dt = Mathf.Max(deltaTime, 1e-4f);
            var eye = rig.transform;

            Kick = Mathf.Lerp(Kick, 0f, dt * 11f);
            _raise = Mathf.Max(0f, _raise - dt / 0.15f);

            var mouse = rig.IsLooking ? rig.Commands.Look : Vector2.zero;
            _sway = Vector2.Lerp(_sway, Vector2.ClampMagnitude(mouse, 3f) * Sway, dt * 9f);

            if (!_hasLast)
            {
                _lastPosition = eye.position;
                _hasLast = true;
            }

            var moved = eye.position - _lastPosition;
            _lastPosition = eye.position;

            var speed = new Vector2(moved.x, moved.z).magnitude / dt;
            var walk = Mathf.Clamp01(speed / 6f);

            _bobPhase += speed * dt * 1.7f;

            var bob = new Vector3(Mathf.Sin(_bobPhase) * 0.007f, Mathf.Abs(Mathf.Cos(_bobPhase)) * 0.009f, 0f) * walk;

            // Упёрся в стену — пушка отводится назад и вниз, а не протыкает породу.
            var wall = Physics.Raycast(eye.position, eye.forward, out var hit, 1.1f, ~0, QueryTriggerInteraction.Ignore)
                ? Mathf.Clamp01(1f - (hit.distance - 0.35f) / 0.7f)
                : 0f;

            _retract = Mathf.Lerp(_retract, wall, dt * 10f);

            var kick = Kick * Kick;
            var raise = _raise * _raise;

            view.localPosition = Offset + bob + ExtraPosition
                                 + new Vector3(0f, 0.012f, -0.08f) * kick
                                 + new Vector3(-0.03f, -0.1f, -0.05f) * Reload
                                 + new Vector3(-0.02f, -0.06f, -0.24f) * _retract
                                 + new Vector3(0f, -0.25f, -0.05f) * raise;

            view.localRotation = Quaternion.Euler(
                -10f * kick + 18f * Reload + 22f * _retract + 30f * raise + _sway.y * 1.6f + ExtraEuler.x,
                -_sway.x * 1.8f - 2f + ExtraEuler.y,
                38f * Reload - _sway.x * 1.2f + ExtraEuler.z);
        }
    }
}
