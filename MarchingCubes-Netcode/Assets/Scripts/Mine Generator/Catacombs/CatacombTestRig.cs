using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Тело и глаза игрока: ходьба, полёт, взгляд, удары по игроку, копание.
    ///
    /// Начинался как отладочный стенд, а стал игроком — имя оставлено, потому что оно
    /// разбросано по CLAUDE.md и прогонам. Отладка (наложение, клавиши F, G, T, R, Q, B, N, P)
    /// вынесена в <see cref="CatacombDebugOverlay"/>; ввод читается командами
    /// (<see cref="PlayerCommands"/>), а не <c>Input</c> напрямую, чтобы встал тач.
    ///
    /// Обновляется раньше остальных (<c>DefaultExecutionOrder</c>): команды кадра читает и
    /// оружие, и без порядка оно получало бы то команды этого кадра, то прошлого.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [DefaultExecutionOrder(-50)]
    public sealed class CatacombTestRig : MonoBehaviour, IPlayerBody
    {
        public enum MoveMode
        {
            Fly = 0,
            Walk = 1
        }

        [SerializeField] private CatacombWorld world;

        [Tooltip("Толпа пауков. Пусто — найдётся на сцене сама.")]
        [SerializeField] private SpiderCrowd crowd;

        [Tooltip("Директор забега. Пусто — возьмётся со сцены.")]
        [SerializeField] private CaveRunDirector director;

        [Tooltip("Матка. Пусто — возьмётся со сцены.")]
        [SerializeField] private CaveQueen queen;

        [Header("Движение")]
        [SerializeField] private MoveMode mode = MoveMode.Fly;
        [SerializeField] private float flySpeed = 18f;
        [SerializeField] private float walkSpeed = 6f;
        [SerializeField] private float boostMultiplier = 3f;
        [SerializeField] private float jumpSpeed = 6f;
        [SerializeField] private float gravity = -20f;
        [SerializeField] private float mouseSensitivity = 2.5f;

        [Header("Копание")]
        /// <summary>
        /// Копание выключено по просьбе пользователя («давай пока уберем возможность копать»).
        ///
        /// Именно выключено, а не удалено: разрушаемость — заявленная механика игры
        /// (кирка, которой игрок продалбливает ходы и добывает ресурс для патронов),
        /// и весь тракт под неё — поле плотности в рантайме, неудаляемые пустые чанки,
        /// перестройка меша по бюджету кадра — остаётся на месте. Здесь снят только ввод.
        /// </summary>
        [Tooltip("Разрешить копать и заращивать мышью. Выключено: механика отложена.")]
        [SerializeField] private bool diggingEnabled;

        [SerializeField] private float digRadius = 4f;
        [SerializeField] private float digStrength = 1f;
        [SerializeField] private float reach = 60f;

        [Tooltip("Пролетать сквозь породу. Выключено — полёт упирается в стены, как ходьба.")]
        [SerializeField] private bool noclip;

        private CharacterController _controller;
        private float _pitch;
        private float _yaw;
        private float _verticalSpeed;
        private bool _looking;
        private bool _spawned;

        private readonly RaycastHit[] _sweepHits = new RaycastHit[8];
        private readonly Collider[] _webOverlaps = new Collider[8];

        private float _lastDigVolume;
        private float _totalDugVolume;

        // Отдача и тряска камеры: поверх взгляда мышью, а не вместо него, и гаснут сами.
        private float _kick;
        private float _shake;

        private IPlayerInputSource _input = new DesktopInputSource();
        private PlayerCommands _commands = PlayerCommands.Empty;

        private PlayerVitals _vitals;
        private PlayerMobility _mobility;

        /// <summary>
        /// Статы забега — класс, мета, тома. Пусто — без бонусов. Выставляет контроллер забега.
        /// </summary>
        public PlayerStats Stats { get; set; }

        /// <summary>Ввод заблокирован: экран карточки, смерть, реклама. Взгляд и ходьба стоят.</summary>
        public bool InputBlocked { get; set; }

        /// <summary>
        /// Помощь прицеливания (тач, M5): мишени шире, монета ловит выстрел сама, автоогонь.
        /// Для мыши выключена.
        /// </summary>
        public bool AimAssist { get; set; }

        public PlayerVitals Vitals
        {
            get
            {
                EnsureRefs();
                return _vitals;
            }
        }

        public PlayerMobility Mobility
        {
            get
            {
                EnsureRefs();
                return _mobility;
            }
        }

        /// <summary>Команды этого кадра — их же читает оружие.</summary>
        public PlayerCommands Commands => _commands;

        /// <summary>Сменить источник ввода: тач (M5), бот прогона.</summary>
        public void SetInputSource(IPlayerInputSource source) => _input = source;

        // Через EnsureRefs: прогоны пересоздают толпу на сцене, и ссылка на удалённую
        // должна подхватить новую, а не остаться пустой.
        public CatacombWorld World { get { EnsureRefs(); return world; } }
        public SpiderCrowd Crowd { get { EnsureRefs(); return crowd; } }
        public CaveRunDirector Director { get { EnsureRefs(); return director; } }
        public CaveQueen Queen { get { EnsureRefs(); return queen; } }

        public MoveMode Mode => mode;
        public bool Noclip => noclip;
        public float Reach => reach;

        public bool DiggingEnabled => diggingEnabled;
        public float DigRadius => digRadius;
        public float TotalDugVolume => _totalDugVolume;
        public float LastDigVolume => _lastDigVolume;

        /// <summary>Игрок уже поставлен в точку входа этого уровня.</summary>
        public bool HasSpawned => _spawned;

        public void ToggleMode() => mode = mode == MoveMode.Fly ? MoveMode.Walk : MoveMode.Fly;

        public void ToggleNoclip() => noclip = !noclip;

        /// <summary>Курсор захвачен — игрок в игре, а не щёлкает по окну. Первый клик только захватывает.</summary>
        public bool IsLooking => _looking;

        /// <summary>Отдача: взгляд подбрасывает вверх на столько градусов и плавно возвращает.</summary>
        public void AddKick(float degrees) => _kick += degrees;

        /// <summary>
        /// Тряска камеры от взрыва: сила 1 — около трёх градусов. Не складывается бесконечно:
        /// шесть гранат подряд у самого лица трясут сильно, но не выворачивают взгляд.
        /// </summary>
        public void AddShake(float strength) => _shake = Mathf.Min(1.5f, _shake + strength);

        private float _fovPunch;
        private float _baseFov = -1f;
        private Camera _camera;

        /// <summary>Удар FOV: поле зрения расширяется на столько градусов и плавно возвращается (рельса).</summary>
        public void AddFovPunch(float degrees) => _fovPunch = Mathf.Min(20f, _fovPunch + degrees);

        private Vector3 _lungeDirection;
        private float _lungeLeft;
        private float _lungeSpeed;

        /// <summary>
        /// Выпад: сдвиг по направлению за время, мимо толпы — пробой кирки. Контроллером
        /// кусками, как рывок: сквозь породу не проходит (сначала её прокапывает сама кирка).
        /// </summary>
        public void Lunge(Vector3 direction, float distance, float duration)
        {
            _lungeDirection = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
            _lungeLeft = Mathf.Max(0.01f, duration);
            _lungeSpeed = distance / _lungeLeft;
        }

        public bool IsLunging => _lungeLeft > 0f;

        private float _deathPose;
        private Vector3 _deathFrom;

        /// <summary>
        /// Камера оседает при смерти: 0 — стоит, 1 — лежит (на 0.9 ниже, с креном). Возврат в 0 —
        /// туда, откуда оседала: возрождённый встаёт на том же месте.
        /// </summary>
        public void SetDeathPose(float pose)
        {
            pose = Mathf.Clamp01(pose);

            if (pose > 0f && _deathPose <= 0f) _deathFrom = transform.position;
            if (pose <= 0f && _deathPose > 0f) transform.position = _deathFrom;

            _deathPose = pose;

            if (pose > 0f)
            {
                var k = Mathf.SmoothStep(0f, 1f, pose);
                transform.position = _deathFrom + Vector3.down * (0.9f * k);
                transform.rotation = Quaternion.Euler(_pitch + 25f * k, _yaw, 28f * k);
            }
        }

        private void Awake()
        {
            EnsureRefs();

            var angles = transform.eulerAngles;
            _yaw = angles.y;
            _pitch = angles.x;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Отладку заводим сами, если сцена её не знает: иначе сцены, собранные до выноса
            // оверлея из стенда, потеряли бы клавиши. В play-режиме — в сцену не пишется.
            if (Application.isPlaying && GetComponent<CatacombDebugOverlay>() == null)
            {
                gameObject.AddComponent<CatacombDebugOverlay>();
            }
#endif
        }

        /// <summary>
        /// Достаёт ссылки на соседей по сцене.
        ///
        /// Отдельным методом, а не только в <c>Awake</c>: в режиме редактирования он
        /// не вызывается вовсе (грабли №17), и у инструментов проверки поля оказались бы
        /// пустыми. Поэтому его зовёт и публичная точка входа.
        /// </summary>
        private void EnsureRefs()
        {
            if (_controller == null) _controller = GetComponent<CharacterController>();
            if (_vitals == null) _vitals = GetComponent<PlayerVitals>();
            if (_mobility == null) _mobility = GetComponent<PlayerMobility>();
            if (world == null) world = FindFirstObjectByType<CatacombWorld>();
            if (crowd == null) crowd = FindFirstObjectByType<SpiderCrowd>();
            if (director == null) director = FindFirstObjectByType<CaveRunDirector>();
            if (queen == null) queen = FindFirstObjectByType<CaveQueen>();
        }

        // ------------------------------------------------------------------ удары по игроку

        /// <summary>
        /// Внешняя скорость — отброс ударной волной Матки. Гаснет сама, за доли секунды.
        ///
        /// Отдельно от ввода, а не прибавкой к нему: игрок, которого швырнуло, должен
        /// лететь, даже если держит клавишу в обратную сторону, — иначе волна, которая
        /// у Megabonk сгоняет с пилона, гасилась бы простым «жму назад».
        /// </summary>
        private Vector3 _push;
        private float _lift;

        private float _entangleLeft;
        private float _entangleScale = 1f;

        /// <summary>Сколько раз толкнуло и спутало — для прогона и интерфейса.</summary>
        public int TimesPushed { get; private set; }

        public int TimesEntangled { get; private set; }

        public bool IsEntangled => _entangleLeft > 0f;

        /// <summary>Чувствительность и инверсия из настроек профиля (M4.2) — множителем к сцене.</summary>
        public float LookScale { get; set; } = 1f;

        public bool InvertY { get; set; }

        private float _pinLeft;
        private Vector3 _drag;

        /// <summary>Сколько раз прижимали — для прогона особых.</summary>
        public int TimesPinned { get; private set; }

        public bool IsPinned => _pinLeft > 0f;

        public void Pin(float seconds)
        {
            if (IsDashing) return;

            _pinLeft = Mathf.Max(_pinLeft, seconds);
            TimesPinned++;
        }

        public void Drag(Vector3 velocity) => _drag += new Vector3(velocity.x, 0f, velocity.z);

        /// <summary>
        /// Забрать тягу этого кадра — прогон, в котором ход стенда не идёт. Без этого она копилась:
        /// «вытянул бы на 2071 юнит» за пять секунд.
        /// </summary>
        public Vector3 ConsumeDrag()
        {
            var drag = _drag;
            _drag = Vector3.zero;
            return drag;
        }

        public bool IsDashing => _mobility != null && _mobility.Dash.Active;

        public bool IsDead => _vitals != null && _vitals.IsDead;

        /// <summary>Ранить игрока. Без компонента здоровья (старая сцена) удар уходит в никуда.</summary>
        public DamageResult Hurt(in DamageHit hit)
        {
            EnsureRefs();

            return _vitals != null ? _vitals.TakeDamage(hit) : default;
        }

        public Vector3 PushVelocity => _push;

        /// <summary>Центр капсулы в мире. Трансформ стенда — это глаза, а не тело.</summary>
        public Vector3 BodyCentre
        {
            get
            {
                EnsureRefs();
                return transform.position + transform.rotation * _controller.center;
            }
        }

        /// <summary>Ось капсулы (между центрами полусфер) и её радиус — для попаданий.</summary>
        public void GetBodySegment(out Vector3 low, out Vector3 high, out float radius)
        {
            EnsureRefs();

            var centre = BodyCentre;
            var half = Mathf.Max(0f, _controller.height * 0.5f - _controller.radius);

            low = centre - Vector3.up * half;
            high = centre + Vector3.up * half;
            radius = _controller.radius;
        }

        /// <summary>Толчок. Вертикальная часть становится прыжком, горизонтальная — сносом.</summary>
        public void Push(Vector3 velocity)
        {
            _push += new Vector3(velocity.x, 0f, velocity.z);

            // Подброс откладывается до хода: стоящему на полу ход сбрасывает вертикальную
            // скорость, и выставленная прямо здесь она пропадала бы, стоило Матке
            // обновиться раньше стенда.
            if (velocity.y > 0f) _lift = Mathf.Max(_lift, velocity.y);

            TimesPushed++;
        }

        /// <summary>
        /// Спутать: ходьба медленнее на заданное время. Повторное попадание продлевает,
        /// а не складывает замедление — иначе залп из трёх плевков обездвиживал бы насмерть.
        /// </summary>
        public void Entangle(float seconds, float speedScale)
        {
            _entangleLeft = Mathf.Max(_entangleLeft, seconds);
            _entangleScale = Mathf.Clamp01(speedScale);

            TimesEntangled++;
        }

        /// <summary>
        /// Гасит отброс и путы. Отдельным шагом, чтобы прогон мог вести время сам,
        /// без Update.
        /// </summary>
        public void TickStatus(float deltaTime)
        {
            _entangleLeft = Mathf.Max(0f, _entangleLeft - deltaTime);
            _pinLeft = IsDashing ? 0f : Mathf.Max(0f, _pinLeft - deltaTime);

            // Экспоненциально: швырок заметен первые доли секунды и дальше не тянется.
            _push = Vector3.Lerp(_push, Vector3.zero, Mathf.Clamp01(deltaTime * 5f));
            if (_push.sqrMagnitude < 0.01f) _push = Vector3.zero;
        }

        private void OnEnable()
        {
            if (world != null) world.Generated += TeleportToSpawn;
        }

        private void OnDisable()
        {
            if (world != null) world.Generated -= TeleportToSpawn;
        }

        private void Update()
        {
            _commands = PlayerCommands.Empty;
            _input?.Read(ref _commands);

            // Экран карточки, смерть, реклама: команды не доходят ни до ходьбы, ни до оружия.
            // Пауза и отмена остаются — ими экран и закрывают.
            if (InputBlocked || IsDead)
            {
                var pause = _commands.Pause;
                var cancel = _commands.Cancel;
                var card = _commands.CardKey;

                _commands = PlayerCommands.Empty;
                _commands.Pause = pause;
                _commands.Cancel = cancel;
                _commands.CardKey = card;
            }

            UpdateCursor();
            UpdateLook();
            UpdateMove();
            UpdateActions();

            TickStatus(Time.deltaTime);
        }

        private void UpdateCursor()
        {
            if (_commands.Cancel) SetLooking(false);

            // Клик по окну игры захватывает курсор; копание при этом уже работает.
            if (!_looking && _commands.FirePressed) SetLooking(true);
        }

        /// <summary>
        /// Захватить курсор (игра) или отпустить (экран карточки, пауза, итоги). На таче
        /// курсора нет, и захват ничего не делает, кроме флага «игрок в игре».
        /// </summary>
        public void SetLooking(bool value)
        {
            _looking = value;
            Cursor.lockState = value ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !value;
        }

        private void UpdateLook()
        {
            if (_looking)
            {
                _yaw += _commands.Look.x * mouseSensitivity * LookScale;
                _pitch = Mathf.Clamp(_pitch - _commands.Look.y * mouseSensitivity * LookScale * (InvertY ? -1f : 1f), -89f, 89f);
            }

            var dt = Time.deltaTime;

            _kick = Mathf.Lerp(_kick, 0f, Mathf.Clamp01(dt * 9f));
            _shake = Mathf.Max(0f, _shake - dt * 2.2f);

            if (_camera == null) _camera = GetComponent<Camera>();

            if (_camera != null)
            {
                if (_baseFov < 0f) _baseFov = _camera.fieldOfView;

                _fovPunch = Mathf.Lerp(_fovPunch, 0f, Mathf.Clamp01(dt * 6f));
                _camera.fieldOfView = _baseFov + _fovPunch;
            }

            // Тряска шумом, а не случайным числом каждый кадр: белый шум читается дрожанием
            // картинки, а не толчком.
            var t = Time.time * 28f;
            var amplitude = _shake * _shake * 3f;

            var shakePitch = (Mathf.PerlinNoise(t, 0.3f) - 0.5f) * 2f * amplitude;
            var shakeYaw = (Mathf.PerlinNoise(0.7f, t) - 0.5f) * 2f * amplitude;
            var shakeRoll = (Mathf.PerlinNoise(t, t * 0.5f) - 0.5f) * amplitude;

            if (_deathPose > 0f) return;

            if (!_looking && _kick < 0.01f && amplitude < 0.01f) return;

            transform.rotation = Quaternion.Euler(_pitch - _kick + shakePitch, _yaw + shakeYaw, shakeRoll);
        }

        private void UpdateMove()
        {
            var input = new Vector3(_commands.Move.x, 0f, _commands.Move.y);

            // Ускорение — только у отладочного полёта. В ходьбе Shift — рывок.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var boost = mode == MoveMode.Fly && _commands.Sprint ? boostMultiplier : 1f;
#else
            var boost = 1f;
#endif

            if (mode == MoveMode.Fly)
            {
                _controller.enabled = false;

                var direction = transform.rotation * input;

                if (_commands.JumpHeld) direction += Vector3.up;
                if (_commands.Descend) direction += Vector3.down;

                var motion = direction.normalized * (flySpeed * boost * Time.deltaTime) + _push * Time.deltaTime;

                // Без этого полёт заносит камеру внутрь породы, и кадр чернеет:
                // обратные грани отсекаются, и видно цвет очистки камеры.
                transform.position = noclip ? transform.position + motion : SweepMove(transform.position, motion);
                return;
            }

            _controller.enabled = true;

            // Пока уровень строится, пола под ногами может не быть вовсе: чанки
            // появляются один за другим, и тяжесть роняет игрока сквозь недостроенное.
            // Замер: 100 чанков, 204 мс работы при бюджете 8 мс на кадр — это 25 кадров,
            // около 0.4 секунды и пара юнитов падения в редакторе. На WebGL кадры длиннее,
            // и те же 25 кадров дают уже заметный провал. Телепорт в точку входа потом
            // вернёт игрока наверх, и со стороны это читается как «провалился и встал».
            if (world != null && world.IsGenerating)
            {
                _verticalSpeed = 0f;
                return;
            }

            // Паутина замедляет только ходьбу: полёт — отладочная камера, и вязнуть ей
            // не в чем. Ищется опросом, а не событиями триггера, см. CaveWeb.
            var web = CurrentWeb();

            // Направление берём только по горизонтали, иначе взгляд вниз тормозит ходьбу.
            var forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            var right = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;

            var slow = (web != null ? web.SpeedScale : 1f) * (IsEntangled ? _entangleScale : 1f);
            var wish = (forward * input.z + right * input.x).normalized;

            var dash = UpdateDash(wish, forward, slow) || UpdateLunge();

            // Толпа держит — только не в рывке: рывок и есть выход из окружения.
            var hold = !dash && _mobility != null ? _mobility.MeasureHold(crowd, this) : 1f;

            var stats = Stats != null ? Stats.MoveScale : 1f;
            var speed = walkSpeed * boost * stats * slow * hold;

            // Отброс поверх ввода: швырнуло — летишь, куда бы ни жал.
            // Прижатый не ходит; тяга нити — поверх ввода, как отброс, но только пока её зовут.
            if (IsPinned && !dash) speed = 0f;

            var move = (dash ? Vector3.zero : wish * speed) + _push + _drag;
            _drag = Vector3.zero;

            if (_controller.isGrounded)
            {
                _verticalSpeed = -1f;
                if (_commands.JumpPressed) _verticalSpeed = jumpSpeed;
            }
            else
            {
                _verticalSpeed += gravity * Time.deltaTime;
            }

            if (_lift > 0f)
            {
                _verticalSpeed = Mathf.Max(_verticalSpeed, _lift);
                _lift = 0f;
            }

            move.y = _verticalSpeed;

            // Падение гасим по скорости, а не по ускорению: разгон под тяжестью идёт и
            // в паутине, просто наружу он не выходит. Иначе после долгого падения сквозь
            // затянутую щель игрок вылетал бы снизу с накопленной скоростью.
            if (web != null && move.y < 0f) move.y *= web.FallScale;

            _controller.Move(move * Time.deltaTime);
        }

        private Vector3 _dashDirection;

        private bool UpdateLunge()
        {
            if (_lungeLeft <= 0f) return false;

            var step = Mathf.Min(_lungeLeft, Time.deltaTime);
            _lungeLeft -= step;

            var distance = _lungeSpeed * step;
            var pieces = Mathf.Max(1, Mathf.CeilToInt(distance / 0.5f));
            var piece = _lungeDirection * (distance / pieces);

            for (var i = 0; i < pieces; i++)
            {
                var before = transform.position;
                _controller.Move(piece);

                if ((transform.position - before).sqrMagnitude < piece.sqrMagnitude * 0.1f)
                {
                    _lungeLeft = 0f;
                    break;
                }
            }

            return true;
        }

        /// <summary>
        /// Рывок: по направлению ввода, без ввода — вперёд по взгляду. Двигает контроллер
        /// кусками не длиннее полуюнита: сквозь породу рывок не проходит, и упёршийся в стену
        /// обрывается, а не скользит в неё всю оставшуюся длину.
        /// </summary>
        /// <returns>Идёт ли рывок на этом кадре.</returns>
        private bool UpdateDash(Vector3 wish, Vector3 forward, float slow)
        {
            if (_mobility == null) return false;

            var dash = _mobility.Dash;

            if (_commands.Dash && !dash.Active && dash.TryStart())
            {
                _dashDirection = wish.sqrMagnitude > 0.01f ? wish : forward;
                if (_vitals != null) _vitals.HoldInvulnerable(InvulnReason.Dash, dash.Duration + dash.Grace);
            }

            var distance = dash.Step(Time.deltaTime) * slow;
            if (distance <= 0f) return false;

            var pieces = Mathf.Max(1, Mathf.CeilToInt(distance / 0.5f));
            var piece = _dashDirection * (distance / pieces);

            for (var i = 0; i < pieces; i++)
            {
                var before = transform.position;
                _controller.Move(piece);

                // Упёрся: прошёл меньше трети куска — дальше только стена.
                if ((transform.position - before).sqrMagnitude < piece.sqrMagnitude * 0.1f)
                {
                    dash.Stop();
                    break;
                }
            }

            return true;
        }

        /// <summary>
        /// Паутина, в которой игрок сейчас стоит, или null.
        ///
        /// Опрос каждый кадр вместо OnTriggerEnter/Exit — намеренно. Паутина исчезает
        /// и от перегенерации уровня, и от выстрела, и события выхода из неё в обоих
        /// случаях не будет: игрок остался бы замедленным навсегда в пустом коридоре.
        /// Состояния здесь нет вовсе, поэтому и портиться нечему.
        ///
        /// Буфер фиксированный: искать надо каждый кадр, а OverlapSphere с выделением
        /// массива на этом месте давал бы мусор в куче шестьдесят раз в секунду.
        /// </summary>
        private CaveWeb CurrentWeb()
        {
            // Центр берём у контроллера, а не у трансформа: у капсулы он смещён, и запрос
            // от точки объекта щупал бы у ног, а паутина натянута на высоте груди.
            var center = transform.position + _controller.center;

            var count = Physics.OverlapSphereNonAlloc(center, _controller.radius,
                _webOverlaps, ~0, QueryTriggerInteraction.Collide);

            for (var i = 0; i < count; i++)
            {
                var web = _webOverlaps[i].GetComponent<CaveWeb>();

                if (web != null) return web;
            }

            return null;
        }

        /// <summary>
        /// Сдвигает камеру с проверкой столкновений и скольжением вдоль стен.
        /// Если камера уже внутри геометрии, столкновения на этот кадр отключаются —
        /// иначе из породы было бы не выбраться.
        /// </summary>
        private Vector3 SweepMove(Vector3 position, Vector3 motion)
        {
            var radius = Mathf.Max(0.1f, _controller.radius);
            const float skin = 0.05f;

            if (Physics.CheckSphere(position, radius, ~0, QueryTriggerInteraction.Ignore))
            {
                return position + motion;
            }

            var remaining = motion.magnitude;
            if (remaining < 1e-5f) return position;

            var direction = motion / remaining;

            for (var step = 0; step < 3 && remaining > 1e-5f; step++)
            {
                var count = Physics.SphereCastNonAlloc(position, radius, direction, _sweepHits,
                    remaining + skin, ~0, QueryTriggerInteraction.Ignore);

                var nearest = -1;
                var nearestDistance = float.MaxValue;

                for (var i = 0; i < count; i++)
                {
                    // Нулевая дистанция означает стартовое перекрытие — такой хит бесполезен.
                    if (_sweepHits[i].distance <= 0f || _sweepHits[i].distance >= nearestDistance) continue;

                    nearest = i;
                    nearestDistance = _sweepHits[i].distance;
                }

                if (nearest < 0)
                {
                    position += direction * remaining;
                    break;
                }

                var advance = Mathf.Max(0f, nearestDistance - skin);
                position += direction * advance;
                remaining -= advance;

                // Скользим вдоль стены остатком пути, а не гасим его целиком.
                var slide = Vector3.ProjectOnPlane(direction * remaining, _sweepHits[nearest].normal);

                remaining = slide.magnitude;
                if (remaining < 1e-5f) break;

                direction = slide / remaining;
            }

            return position;
        }

        private void UpdateActions()
        {
            if (diggingEnabled)
            {
                // Кисть мельче вокселя не задевает ни одного сэмпла сетки и не делает ничего.
                var minRadius = world != null ? Mathf.Max(1f, world.Settings.VoxelSize * 1.2f) : 1f;
                digRadius = Mathf.Clamp(digRadius + _commands.Scroll * 0.5f, minRadius, 20f);
            }

            if (_commands.Interact) WakeQueen();

            if (world == null || !_looking) return;

            if (!diggingEnabled) return;

            if (_commands.FireHeld) Modify(true);
            else if (_commands.AltHeld) Modify(false);
        }

        /// <summary>
        /// Новый уровень со случайным сидом — отладка стенда (клавиша R в
        /// <see cref="CatacombDebugOverlay"/>), а в M4 — переход между ярусами.
        /// </summary>
        public void RegenerateLevel()
        {
            if (world == null) return;

            _spawned = false;
            _totalDugVolume = 0f;

            // Старые шашки остались бы висеть в воздухе там, где породы больше нет,
            // или оказались бы замурованы в новой.
            CaveFlare.ClearAll();

            world.Generate(Random.Range(int.MinValue, int.MaxValue));
        }

        /// <summary>
        /// Будит Матку, если игрок у логова. Это и есть цель стадии: найти логово
        /// и решить, когда ты готов. Чем позже — тем она живучее.
        /// </summary>
        private void WakeQueen()
        {
            if (queen == null || director == null) return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Shift — перенестись к логову. Отладка: логово в дальнем конце уровня,
            // и проверять бой, каждый раз добираясь туда пешком, невозможно.
            if (Input.GetKey(KeyCode.LeftShift))
            {
                if (!queen.HasLair) return;

                _controller.enabled = false;
                transform.position = StandingOn(queen.LairPosition + Vector3.up * 0.2f) + Vector3.forward * 3f;
                _controller.enabled = mode == MoveMode.Walk;

                Debug.Log("ПЕРЕНОС к логову Матки (отладка)");
                return;
            }
#endif

            if (director.TrySummonQueen(transform.position)) return;

            Debug.Log(queen.Describe(transform.position));
        }

        private void Modify(bool dig)
        {
            var origin = transform.position;
            var point = Physics.Raycast(origin, transform.forward, out var hit, reach)
                ? hit.point
                : origin + transform.forward * reach;

            var changed = dig
                ? world.Dig(point, digRadius, digStrength)
                : world.Fill(point, digRadius, digStrength);

            if (!changed) return;

            // Именно оценка: сколько породы ушло на самом деле, знает только поле плотности,
            // а вычитывать его каждый удар дорого. Повторные удары в одну точку теперь
            // ничего не меняют, поэтому оценка завышает — на это и есть пометка в интерфейсе.
            _lastDigVolume = 4f / 3f * Mathf.PI * digRadius * digRadius * digRadius * digStrength;
            if (dig) _totalDugVolume += _lastDigVolume;
        }

        /// <summary>
        /// Ставит игрока в точку входа — НОГАМИ на пол, а не трансформом.
        ///
        /// Раньше сюда клался трансформ, и это неверно дважды.
        ///
        /// Во-первых, трансформ стенда — это ГЛАЗА: у капсулы центр смещён вниз,
        /// и подошвы лежат на 1.1 юнита ниже точки объекта. Положенный в пол трансформ
        /// утапливал капсулу в породу целиком, контроллер несколько кадров разбирался
        /// с начальным пересечением и ронял игрока на настоящий пол — со стороны это
        /// и читается как «сперва проваливается, потом встаёт».
        ///
        /// Во-вторых, точка входа из планировки — это НОМИНАЛЬНЫЙ пол: нижняя грань
        /// бокса, которым зал вырезан. Настоящая поверхность с ней не совпадает.
        /// Шум стен двигает её на ±NoiseAmplitude, SmoothMin на стыке с коридором
        /// выгрызает глубже, а площадка лестницы уводит пол ещё ниже. Замер по сидам
        /// 1337 / 2 / 777 / 55555: настоящий пол лежит на 0.2-2.3 юнита НИЖЕ номинала,
        /// и подошвы оказывались то на 0.9 юнита в породе, то на 1.2 в воздухе.
        ///
        /// Теперь на всех четырёх сидах зазор под подошвами ровно в толщину скина,
        /// ног в породе нет, и за секунду под тяжестью игрок опускается на 0.001 юнита.
        /// </summary>
        public void TeleportToSpawn()
        {
            EnsureRefs();

            if (world == null || !world.TryGetSpawnPoint(out var spawn)) return;

            var wasEnabled = _controller.enabled;

            // Контроллер выключается ДО поиска пола, и это обязательно по двум причинам.
            // Он держит свою копию позиции в PhysX, и присваивание трансформа на включённом
            // контроллере она замечает не всегда. А главное — капсула игрока это коллайдер,
            // и луч из FloorUnder находит её раньше пола: при замере луч из середины зала
            // упёрся в макушку самого игрока и отчитался о поле на 0.74 юнита выше
            // настоящего. Переставить эти две строки местами значит поставить игрока
            // себе на голову.
            _controller.enabled = false;

            transform.position = StandingOn(FloorUnder(spawn));

            _controller.enabled = wasEnabled;
            _verticalSpeed = 0f;
            _spawned = true;
        }

        /// <summary>
        /// Настоящая поверхность пола под номинальной точкой входа.
        ///
        /// Луч ЗДЕСЬ допустим: он начинается в середине зала, то есть в пустоте,
        /// и ищет пол изнутри. Грабли №1 запрещают лучи, НАЧАТЫЕ в сплошной породе, —
        /// поэтому старт всё же проверяется по полю плотности, и если зал почему-то
        /// завален, остаётся номинал: упасть с номинала не хуже, чем встать неизвестно где.
        /// </summary>
        private Vector3 FloorUnder(Vector3 nominal)
        {
            var height = world.Settings != null ? world.Settings.RoomHeight : 4.5f;

            var from = nominal + Vector3.up * (height * 0.5f);
            if (world.IsSolid(from)) return nominal;

            // Вниз ищем на две высоты зала: номинал промахивается на пару юнитов
            // (на сиде 777 пол под точкой входа на 2.3 ниже номинала), а дальше искать
            // нельзя — 6.75 юнита вниз это уже меньше расстояния между этажами,
            // и провалившийся глубже луч отправил бы игрока на чужой этаж.
            return Physics.Raycast(from, Vector3.down, out var hit, height * 2f, ~0, QueryTriggerInteraction.Ignore)
                ? hit.point
                : nominal;
        }

        /// <summary>Позиция трансформа, при которой подошвы капсулы стоят на точке.</summary>
        private Vector3 StandingOn(Vector3 ground)
        {
            var feet = _controller.center.y - _controller.height * 0.5f;

            // Зазор в толщину скина: сесть ровно на поверхность значит начать кадр
            // в пересечении с ней, а это ровно то, от чего здесь и уходим.
            return ground + Vector3.up * (_controller.skinWidth - feet);
        }
    }
}
