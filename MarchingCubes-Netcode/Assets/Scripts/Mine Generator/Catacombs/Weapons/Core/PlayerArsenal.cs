using System;
using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Арсенал игрока: три слота, активная пушка, смена, общие системы боя — снаряды, линии
    /// эффектов, взрывы, воронки и гарь, звук.
    ///
    /// Смена почти мгновенная (0.15 с), как в Ultrakill: переключение посреди очереди должно
    /// быть приёмом, а не наказанием. Убранная пушка дотикивает перезарядку и остывание.
    ///
    /// Компонент сцены на теле игрока, пересоздаётся «Починить свет» (грабли №8, №67) —
    /// он же проставляет шейдеры ссылками: <c>Shader.Find</c> в сборке находит только то,
    /// на что кто-то ссылается. Всё, что арсенал создаёт, — DontSave и в сцену не пишется.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CatacombTestRig))]
    [DefaultExecutionOrder(-40)]
    public sealed class PlayerArsenal : MonoBehaviour
    {
        public const int Slots = 3;

        /// <summary>Сколько секунд смена пушки — почти мгновенно, как в Ultrakill.</summary>
        public const float SwitchTime = 0.15f;

        [Header("Шейдеры (проставляет «Починить свет»)")]
        [Tooltip("Universal Render Pipeline/Lit — металл и полимер оружия.")]
        [SerializeField] private Shader litShader;

        [Tooltip("Mine Generator/Cave Particles — огонь, дым, искры, вспышки, линии.")]
        [SerializeField] private Shader particleShader;

        [Header("Стартовая пушка без контроллера забега")]
        [SerializeField] private WeaponId startWeapon = WeaponId.Launcher;

        [Header("Следы взрыва")]
        [Tooltip("Копать ли воронку в породе. Гарь остаётся и без неё.")]
        [SerializeField] private bool craters = true;

        [Tooltip("Радиус сферы, которой выкапывается воронка, юниты. Воксель породы — юнит, " +
                 "и сфера меньше полутора в меше почти не проявляется.")]
        [SerializeField, Range(0.8f, 4f)] private float craterRadius = 1.7f;

        [Tooltip("Насколько воронка уходит в камень при ударе вплотную, юниты.")]
        [SerializeField, Range(0f, 2f)] private float craterDepth = 0.85f;

        [Tooltip("Радиус пятна гари вокруг взрыва, юниты. Три и больше заливали весь пол в кадре.")]
        [SerializeField, Range(0.5f, 6f)] private float scorchRadius = 2.4f;

        private CatacombTestRig _rig;
        private Transform _viewRoot;
        private readonly Weapon[] _slots = new Weapon[Slots];
        private readonly List<GameObject> _owned = new List<GameObject>();

        private int _active = -1;
        private float _switchLeft;
        private bool _prepared;
        private bool _loadoutGiven;

        public CombatContext Context { get; private set; }

        public int ActiveSlot => _active;

        public Weapon Active => _active >= 0 ? _slots[_active] : null;

        public IReadOnlyList<Weapon> Weapons => _slots;

        /// <summary>Пушка сменилась (слот, пушка) — для интерфейса и подсказок.</summary>
        public event Action<int, Weapon> Switched;

        /// <summary>Сколько пушек в руках.</summary>
        public int Count
        {
            get
            {
                var count = 0;
                foreach (var weapon in _slots) if (weapon != null) count++;
                return count;
            }
        }

        public bool HasFreeSlot => Count < Slots;

        private void Awake() => _rig = GetComponent<CatacombTestRig>();

        private void Start()
        {
            if (!Application.isPlaying) return;

            Prepare();

            // Контроллер забега выдаёт стартовую пушку класса. Без него (старые сцены, стенд) —
            // своя стартовая, чтобы стрелять было чем, как раньше с гранатомётом.
            if (!_loadoutGiven) Give(startWeapon);
        }

        /// <summary>
        /// Собрать общие системы боя. Отдельно от Start: прогоны работают в режиме редактирования,
        /// где Start не зовётся (грабли №17).
        /// </summary>
        public void Prepare()
        {
            if (_prepared) return;

            if (_rig == null) _rig = GetComponent<CatacombTestRig>();

#if UNITY_EDITOR
            if (litShader == null) litShader = Shader.Find("Universal Render Pipeline/Lit");
            if (particleShader == null) particleShader = Shader.Find("Mine Generator/Cave Particles");
#endif

            if (litShader == null || particleShader == null)
            {
                Debug.LogError("АРСЕНАЛ: не проставлены шейдеры — запустите «Починить свет и туман в текущей сцене»");
                enabled = false;
                return;
            }

            _prepared = true;

            var materials = new WeaponMaterials(litShader, particleShader);

            var root = new GameObject("Combat Systems") { hideFlags = HideFlags.DontSave };
            _owned.Add(root);

            var explosions = root.AddComponent<CaveExplosions>();
            explosions.Build(particleShader);

            var marks = root.AddComponent<CaveBlastMarks>();
            marks.Bind(_rig.World, _rig.Crowd, transform, craterRadius, craters ? craterDepth : 0f, scorchRadius);

            var fx = root.AddComponent<WeaponFx>();
            fx.Build(materials.Glow("Weapon Lines", materials.SoftLine, Color.white));
            fx.Camera = GetComponent<Camera>();

            var projectiles = root.AddComponent<ProjectileSystem>();
            projectiles.Crowd = _rig.Crowd;
            projectiles.Queen = _rig.Queen;
            projectiles.Player = _rig;
            projectiles.PlayerCollider = GetComponent<CharacterController>();
            projectiles.Fx = fx;

            var audioRoot = new GameObject("Weapon Audio") { hideFlags = HideFlags.DontSave };
            audioRoot.transform.SetParent(transform, false);
            _owned.Add(audioRoot);

            var audio = audioRoot.AddComponent<WeaponAudio>();
            audio.Build();

            _viewRoot = new GameObject("Weapon Views") { hideFlags = HideFlags.DontSave }.transform;
            _viewRoot.SetParent(transform, false);
            _owned.Add(_viewRoot.gameObject);

            Context = new CombatContext
            {
                Rig = _rig,
                Eye = transform,
                Crowd = _rig.Crowd,
                Queen = _rig.Queen,
                World = _rig.World,
                Explosions = explosions,
                Marks = marks,
                Projectiles = projectiles,
                Fx = fx,
                Audio = audio,
                Feedback = new HitFeedback(_rig),
                Stats = _rig.Stats,
                Materials = materials,
                PlayerCollider = GetComponent<CharacterController>()
            };
        }

        private void OnDestroy()
        {
            foreach (var weapon in _slots) weapon?.Dispose();

            Context?.Materials?.Dispose();

            foreach (var owned in _owned)
            {
                if (owned == null) continue;

                if (Application.isPlaying) Destroy(owned);
                else DestroyImmediate(owned);
            }
        }

        // ------------------------------------------------------------------ состав

        /// <summary>Снять все пушки — начало забега, контроллер выдаст стартовую сам.</summary>
        public void ClearLoadout()
        {
            Prepare();

            for (var i = 0; i < Slots; i++)
            {
                _slots[i]?.Dispose();
                _slots[i] = null;
            }

            _active = -1;
            _loadoutGiven = true;

            Context?.Projectiles?.Clear();
        }

        public bool Has(WeaponId id) => Get(id) != null;

        public Weapon Get(WeaponId id)
        {
            foreach (var weapon in _slots)
            {
                if (weapon != null && weapon.Id == id) return weapon;
            }

            return null;
        }

        /// <summary>Выдать пушку в свободный слот и взять её в руки. Нет слота — false.</summary>
        public bool Give(WeaponId id, int level = 1)
        {
            Prepare();
            if (!_prepared || Has(id)) return false;

            var slot = Array.IndexOf(_slots, null);
            if (slot < 0) return false;

            var weapon = Create(id);
            if (weapon == null) return false;

            _loadoutGiven = true;

            weapon.Setup(WeaponCatalog.Get(id), Context, _viewRoot);
            weapon.SetLevel(level);

            _slots[slot] = weapon;

            Select(slot);
            return true;
        }

        /// <summary>Поднять уровень пушки (карточка). Возвращает новый уровень или 0.</summary>
        public int Upgrade(WeaponId id)
        {
            var weapon = Get(id);
            if (weapon == null || weapon.Level >= WeaponDef.MaxLevel) return 0;

            weapon.SetLevel(weapon.Level + 1);
            return weapon.Level;
        }

        public bool Evolve(WeaponId id)
        {
            var weapon = Get(id);
            if (weapon == null || weapon.Evolved) return false;

            weapon.Evolve();
            return true;
        }

        /// <summary>Статы забега поменялись — пересчитать числа всех пушек.</summary>
        public void SetStats(PlayerStats stats)
        {
            Prepare();
            if (Context == null) return;

            Context.Stats = stats;

            foreach (var weapon in _slots) weapon?.Refresh();
        }

        private static Weapon Create(WeaponId id) => id switch
        {
            WeaponId.Launcher => new GrenadeLauncherWeapon(),
            WeaponId.Revolver => new RevolverWeapon(),
            WeaponId.Shotgun => new ShotgunWeapon(),
            WeaponId.Nailgun => new NailgunWeapon(),
            WeaponId.Rail => new RailWeapon(),
            WeaponId.Flamer => new FlamerWeapon(),
            WeaponId.Tesla => new TeslaWeapon(),
            WeaponId.PickHammer => new PickHammerWeapon(),
            _ => null
        };

        public void Select(int slot)
        {
            if (slot < 0 || slot >= Slots || _slots[slot] == null || slot == _active) return;

            if (_active >= 0) _slots[_active]?.Holster();

            _active = slot;
            _switchLeft = SwitchTime;
            _slots[slot].Equip();

            Context?.Audio?.Play(SoundId.Reload, 0.35f, 1.4f);

            Switched?.Invoke(slot, _slots[slot]);
        }

        private void Cycle(int direction)
        {
            if (Count < 2) return;

            var slot = _active;

            for (var i = 0; i < Slots; i++)
            {
                slot = (slot + direction + Slots) % Slots;

                if (_slots[slot] == null) continue;

                Select(slot);
                return;
            }
        }

        // ------------------------------------------------------------------ кадр

        private void Update()
        {
            if (!_prepared || !Application.isPlaying) return;

            Tick(_rig.Commands, Time.deltaTime, _rig.IsLooking && !_rig.InputBlocked && !_rig.IsDead && !_rig.IsPinned);
        }

        /// <summary>Шаг: смена, огонь активной, фон всех. Отдельно — прогоны стреляют сами.</summary>
        public void Tick(in PlayerCommands commands, float deltaTime, bool canFire)
        {
            if (deltaTime <= 0f) return;

            if (Context != null) Context.Time += deltaTime;

            if (canFire)
            {
                if (commands.WeaponSlot >= 0) Select(commands.WeaponSlot);
                else if (commands.WeaponCycle != 0) Cycle(commands.WeaponCycle);
            }

            _switchLeft = Mathf.Max(0f, _switchLeft - deltaTime);

            var active = Active;

            if (canFire && active != null && _switchLeft <= 0f) active.Tick(commands, deltaTime);

            foreach (var weapon in _slots) weapon?.TickBackground(deltaTime);
        }

        private void LateUpdate()
        {
            if (!_prepared || !Application.isPlaying) return;

            Active?.TickView(Time.deltaTime);
        }

        // ------------------------------------------------------------------ отладка и прогоны

        /// <summary>
        /// Взрыв гранаты в точке без полёта — клавиша B стенда и прогоны. Числа — гранатомёта
        /// в руках, а без него — каталожные первого уровня.
        /// </summary>
        public int Detonate(Vector3 point, Vector3 normal)
        {
            Prepare();
            if (Context == null) return 0;

            if (Get(WeaponId.Launcher) is GrenadeLauncherWeapon launcher) return launcher.Detonate(point, normal);

            var numbers = WeaponCatalog.Get(WeaponId.Launcher).Compute(1, false, Context.Stats);
            var gibs = new List<Vector3>();

            return GrenadeLauncherWeapon.Blast(Context, point, normal, numbers.Area, numbers.Damage,
                DamageSource.Debug, gibs, true, null).Killed;
        }

        /// <summary>Сколько убито пушками за уровень — по журналу толпы.</summary>
        public int TotalKills
        {
            get
            {
                var crowd = _rig != null ? _rig.Crowd : null;
                if (crowd == null) return 0;

                var total = 0;
                for (var i = 0; i < (int)WeaponId.Count; i++) total += crowd.KillsBy(WeaponIds.SourceOf((WeaponId)i));

                return total;
            }
        }
    }
}
