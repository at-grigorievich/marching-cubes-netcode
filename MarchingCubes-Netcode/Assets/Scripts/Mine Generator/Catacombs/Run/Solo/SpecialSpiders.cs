using Unity.Mathematics;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    // ====================================================================== плевун

    /// <summary>
    /// Плевун: сидит на стене или своде в 12–20 юнитах и раз в 4 с плюёт кислотой дугой.
    /// Попадание — 10 HP, на полу остаётся лужа на 4 с. Метка — 0.8 с замаха: зелёный отсвет
    /// и звук. Ответ игрока — снять издалека (револьвер, рельса), лужу обойти, плевок отбить
    /// парированием кирки.
    /// </summary>
    public sealed class SpitterSpider : SoloSpider
    {
        private const float Interval = 4f;
        private const float Windup = 0.8f;
        private const float KeepFrom = 10f;
        private const float KeepTo = 18f;

        private float _cooldown = 2f;
        private float _windup = -1f;

        public override SpecialKind Type => SpecialKind.Spitter;
        public override int Threat => 60;

        protected override string KindName => "spider_peacock";
        protected override float SizeScale => 1.3f;
        protected override Color Hue => new Color(0.65f, 1.15f, 0.55f);

        protected override void OnSpawn() => Speed = 3.2f;

        protected override void Behave(float deltaTime)
        {
            var distance = DistanceToPlayer;
            var sees = SeesPlayer();

            if (_windup >= 0f)
            {
                FacePlayer(deltaTime, 240f);
                _windup += deltaTime;

                if (_windup >= Windup)
                {
                    _windup = -1f;
                    _cooldown = Interval;
                    Owner.FireAcid(this, Head, Player.BodyCentre);
                }

                return;
            }

            _cooldown -= deltaTime;

            // Держит дистанцию: далеко или не видит — подползает, близко — отходит.
            if (!sees || distance > KeepTo) Walk(deltaTime, true);
            else if (distance < KeepFrom) Walk(deltaTime, false);
            else
            {
                View.Play(SpiderClip.Idle);
                FacePlayer(deltaTime, 180f);
            }

            if (sees && distance < KeepTo + 4f && _cooldown <= 0f && !Stunned)
            {
                _windup = 0f;
                View.Play(SpiderClip.Attack, true);
                Owner.Cue(this, SpecialCue.Spit);
            }
        }

        protected override void OnStaggered(float seconds)
        {
            // Оглушение сбивает замах: плевок не вылетает, перезарядка заново.
            if (seconds > 0f && _windup >= 0f)
            {
                _windup = -1f;
                _cooldown = Interval * 0.5f;
            }
        }

        private Vector3 Head => Centre + WorldForward * (View.HitRadius * 0.5f);
    }

    // ====================================================================== бомбардир

    /// <summary>
    /// Бомбардир: раздутый жёлтый тарантул идёт к игроку и в 2.8 юнита раздувается (1 с), потом
    /// лопается кислотным облаком радиусом 4 — **по паукам тоже**. Убитый на подходе лопается
    /// сразу там, где упал, и облако косит толпу вокруг него — комбинация в духе Ultrakill:
    /// убийства облака идут в журнал той пушки, что его подорвала.
    /// </summary>
    public sealed class BomberSpider : SoloSpider
    {
        public const float CloudRadius = 4f;

        private const float SwellAt = 2.8f;
        private const float SwellSeconds = 1f;

        /// <summary>Облако по паукам: 6 попаданий — косит всё, кроме тарантулов и элиты.</summary>
        private const float CloudDamageToSpiders = 60f;

        /// <summary>Облако по игроку: четверть здоровья на старте — больно, но не смерть.</summary>
        private const float CloudDamageToPlayer = 25f;

        private float _swell = -1f;
        private bool _popped;

        public override SpecialKind Type => SpecialKind.Bomber;
        public override int Threat => 40;

        protected override string KindName => "tarantula";
        protected override float SizeScale => 1.1f;
        protected override Color Hue => new Color(1.35f, 1.2f, 0.35f);

        /// <summary>Сколько пауков убило облако — для прогона и заданий.</summary>
        public int CloudKills { get; private set; }

        protected override void OnSpawn() => Speed = 2.6f;

        protected override void Behave(float deltaTime)
        {
            if (_swell >= 0f)
            {
                _swell += deltaTime;

                // Раздувается: пульс оттенка всё чаще.
                var pulse = 0.5f + 0.5f * Mathf.Sin(_swell * (8f + _swell * 20f));
                View.SetHue(Color.Lerp(Hue, new Color(1.8f, 1.6f, 0.4f), pulse));

                if (_swell >= SwellSeconds) Pop(DamageSource.Special, true);
                return;
            }

            Walk(deltaTime, true);

            if (DistanceToPlayer < SwellAt && SeesPlayer() && !Stunned)
            {
                _swell = 0f;
                View.Play(SpiderClip.Attack, true);
                Owner.Cue(this, SpecialCue.Swell);
            }
        }

        protected override void OnDeath(DamageSource source) => Pop(source, false);

        /// <summary>Лопнуть: облако по паукам и по игроку, лужа на камне.</summary>
        private void Pop(DamageSource source, bool self)
        {
            if (_popped) return;
            _popped = true;

            var centre = Centre;

            CloudKills = Crowd != null ? Crowd.DamageSphere(centre, CloudRadius, CloudDamageToSpiders, source) : 0;

            if (Player != null && !Player.IsDashing && Vector3.Distance(Player.BodyCentre, centre) < CloudRadius)
            {
                Player.Hurt(DamageHit.Simple(CloudDamageToPlayer, centre, DamageKind.Acid));
            }

            Owner.AcidCloud(centre, WorldUp, CloudRadius);

            if (self) Kill(DamageSource.Special);
        }
    }

    // ====================================================================== ловчий

    /// <summary>
    /// Ловчий: подкрадывается по ходам и с 8 юнитов, визжа 1 с, прыгает на игрока и прижимает:
    /// 1.5 с игрок не ходит и не стреляет, 8 HP/с. Ответ — рывок в момент визга (прыжок уходит
    /// в пустоту и Ловчий оглушён), парирование кирки сбивает прыжок, на таче прижим снимается
    /// сам. После прижима отскакивает и через 6 с может снова.
    /// </summary>
    public sealed class LurkerSpider : SoloSpider
    {
        private const float LeapFrom = 8f;
        private const float ShriekSeconds = 1f;
        private const float LeapSpeed = 14f;
        private const float PinSeconds = 1.5f;
        private const float PinDps = 8f;
        private const float Cooldown = 6f;

        private enum Phase { Stalk, Shriek, Leap, Pinning, Recover }

        private Phase _phase = Phase.Stalk;
        private float _timer;
        private Vector3 _flightPosition;
        private Vector3 _flightVelocity;

        /// <summary>
        /// Куда прыгнет — запоминается в НАЧАЛЕ визга: визг и есть метка «прыгну сюда». Наводись
        /// прыжок в конце визга, рывок в момент визга не спасал бы — прыжок шёл бы за игроком.
        /// </summary>
        private Vector3 _leapTarget;

        public override SpecialKind Type => SpecialKind.Lurker;
        public override int Threat => 70;

        protected override string KindName => "spider_jumper";
        protected override float SizeScale => 1.4f;
        protected override Color Hue => new Color(0.55f, 0.5f, 0.6f);

        /// <summary>Сколько раз прижал игрока и сколько промахнулся — для прогона.</summary>
        public int Pins { get; private set; }
        public int Misses { get; private set; }

        protected override void OnSpawn() => Speed = 4.5f;

        public override Vector3 Centre => _phase == Phase.Leap || _phase == Phase.Pinning ? _flightPosition : base.Centre;

        protected override void Behave(float deltaTime)
        {
            switch (_phase)
            {
                case Phase.Stalk:
                    _timer -= deltaTime;
                    Walk(deltaTime, true);

                    if (_timer <= 0f && DistanceToPlayer < LeapFrom && SeesPlayer() && !Stunned)
                    {
                        _phase = Phase.Shriek;
                        _timer = ShriekSeconds;
                        _leapTarget = Player.BodyCentre;
                        View.Play(SpiderClip.Attack, true);
                        Owner.Cue(this, SpecialCue.Shriek);
                    }

                    break;

                case Phase.Shriek:
                    FacePlayer(deltaTime, 400f);
                    _timer -= deltaTime;

                    if (_timer <= 0f) StartLeap();
                    break;

                case Phase.Leap:
                    Fly(deltaTime);
                    break;

                case Phase.Pinning:
                    _timer -= deltaTime;
                    _flightPosition = Player.BodyCentre + Vector3.up * 0.4f;

                    Player.Hurt(DamageHit.Over(PinDps * deltaTime, _flightPosition, DamageKind.Bite));

                    // Рывок сбрасывает: прижим не держит того, кто в рывке.
                    if (_timer <= 0f || Player.IsDashing) JumpOff();
                    break;

                case Phase.Recover:
                    _timer -= deltaTime;
                    Walk(deltaTime, false);

                    if (_timer <= 0f)
                    {
                        _phase = Phase.Stalk;
                        _timer = Cooldown;
                    }

                    break;
            }
        }

        private void StartLeap()
        {
            _phase = Phase.Leap;
            _flightPosition = base.Centre;

            var target = _leapTarget;
            var flat = new Vector3(target.x - _flightPosition.x, 0f, target.z - _flightPosition.z);
            var time = Mathf.Max(0.2f, flat.magnitude / LeapSpeed);

            _flightVelocity = flat / time + Vector3.up * ((target.y - _flightPosition.y + 0.5f * Gravity * time * time) / time);
            _timer = time + 0.6f;
        }

        private const float Gravity = 20f;

        private void Fly(float deltaTime)
        {
            var previous = _flightPosition;

            _flightVelocity += Vector3.down * (Gravity * deltaTime);
            _flightPosition += _flightVelocity * deltaTime;
            _timer -= deltaTime;

            // Долетел до игрока, а тот не в рывке — прижал.
            if (Vector3.Distance(_flightPosition, Player.BodyCentre) < 1.6f)
            {
                if (Player.IsDashing)
                {
                    Miss();
                    return;
                }

                _phase = Phase.Pinning;
                _timer = PinSeconds;
                Pins++;
                Player.Pin(PinSeconds);
                return;
            }

            // Упёрся в камень или пролетел — промах, падает на ближайшую поверхность.
            var delta = _flightPosition - previous;
            if (_timer <= 0f || (delta.sqrMagnitude > 1e-6f &&
                                 Physics.Raycast(previous, delta.normalized, delta.magnitude, ~0, QueryTriggerInteraction.Ignore)))
            {
                Miss();
            }
        }

        private void Miss()
        {
            Misses++;
            Land(_flightPosition);

            // Фаза — до оглушения: оглушение в прыжке само зовёт промах, и наоборот был бы круг.
            _phase = Phase.Recover;
            _timer = 1.5f;
            Stagger(Vector3.zero, 1f);
        }

        private void JumpOff()
        {
            Land(_flightPosition);
            _phase = Phase.Recover;
            _timer = 1.5f;
        }

        protected override void OnStaggered(float seconds)
        {
            // Парирование и оглушение сбивают прыжок и прижим.
            if (seconds <= 0f) return;

            if (_phase == Phase.Leap) Miss();
            else if (_phase == Phase.Pinning) JumpOff();
            else if (_phase == Phase.Shriek)
            {
                _phase = Phase.Stalk;
                _timer = Cooldown * 0.5f;
            }
        }

        public override void Draw(int layer)
        {
            if (_phase == Phase.Leap || _phase == Phase.Pinning)
            {
                var forward = _flightVelocity.sqrMagnitude > 0.01f ? _flightVelocity.normalized : Vector3.forward;
                View.Draw(_flightPosition - Vector3.up * View.BodyLift, Quaternion.LookRotation(forward * View.Facing, Vector3.up),
                    layer, false);
                return;
            }

            base.Draw(layer);
        }
    }

    // ====================================================================== ткач

    /// <summary>
    /// Ткач: держится в 12–18 юнитах и раз в 7 с, натянув нить (0.8 с замаха), стреляет ей.
    /// Нить, попавшая в игрока, 2 с тянет его к ткачу — то есть к толпе. Ответ — перебить нить
    /// (любой выстрел по её линии), убить ткача или рывок в сторону.
    /// </summary>
    public sealed class WeaverSpider : SoloSpider
    {
        private const float Interval = 7f;
        private const float Windup = 0.8f;
        private const float PullSeconds = 2f;
        private const float PullSpeed = 5f;
        private const float KeepFrom = 11f;
        private const float KeepTo = 18f;

        private float _cooldown = 3f;
        private float _windup = -1f;
        private float _pullLeft;
        private WeaverThread _thread;

        public override SpecialKind Type => SpecialKind.Weaver;
        public override int Threat => 55;

        protected override string KindName => "Argiope";
        protected override float SizeScale => 1.3f;
        protected override Color Hue => new Color(1.1f, 1.05f, 1.2f);

        public bool Pulling => _pullLeft > 0f;

        /// <summary>Сколько раз нить зацепила игрока и сколько её перебили — для прогона.</summary>
        public int Catches { get; private set; }
        public int ThreadsCut { get; private set; }

        protected override void OnSpawn() => Speed = 3.4f;

        protected override void Behave(float deltaTime)
        {
            if (_pullLeft > 0f)
            {
                UpdatePull(deltaTime);
                return;
            }

            if (_windup >= 0f)
            {
                FacePlayer(deltaTime, 300f);
                _windup += deltaTime;

                if (_windup >= Windup)
                {
                    _windup = -1f;
                    _cooldown = Interval;
                    Owner.FireThread(this, Centre, Player.BodyCentre);
                }

                return;
            }

            _cooldown -= deltaTime;

            var distance = DistanceToPlayer;
            var sees = SeesPlayer();

            if (!sees || distance > KeepTo) Walk(deltaTime, true);
            else if (distance < KeepFrom) Walk(deltaTime, false);
            else
            {
                View.Play(SpiderClip.Idle);
                FacePlayer(deltaTime, 180f);
            }

            if (sees && distance < KeepTo + 2f && _cooldown <= 0f && !Stunned)
            {
                _windup = 0f;
                View.Play(SpiderClip.Attack, true);
                Owner.Cue(this, SpecialCue.Thread);
            }
        }

        /// <summary>Нить попала в игрока — тянуть.</summary>
        public void Catch()
        {
            if (Dead) return;

            Catches++;
            _pullLeft = PullSeconds;
            _thread = new WeaverThread(this);
            _thread.Register();
        }

        private void UpdatePull(float deltaTime)
        {
            _pullLeft -= deltaTime;

            var from = Centre;
            var to = Player.BodyCentre;

            // Рывок рвёт нить, перебитая — тоже.
            if (Player.IsDashing || _thread == null || _thread.Cut || _pullLeft <= 0f)
            {
                if (_thread != null && _thread.Cut) ThreadsCut++;
                Release();
                return;
            }

            var pull = from - to;
            pull.y = 0f;

            if (pull.sqrMagnitude > 1f) Player.Drag(pull.normalized * PullSpeed);

            _thread.Update(from, to);
            Owner.DrawThread(from, to);
            FacePlayer(deltaTime, 300f);
        }

        private void Release()
        {
            _pullLeft = 0f;
            _thread?.Unregister();
            _thread = null;
        }

        protected override void OnDeath(DamageSource source) => Release();

        protected override void OnStaggered(float seconds)
        {
            if (seconds > 0f && _windup >= 0f)
            {
                _windup = -1f;
                _cooldown = Interval * 0.5f;
            }
        }
    }

    /// <summary>
    /// Нить ткача как цель: три точки по линии (четверть, половина, три четверти), по которым
    /// попадает любая пушка. Попадание по любой — нить перебита. Сфера, а не отрезок: формы
    /// попаданий M2 умеют только шар цели, а трёх шаров на линии хватает, чтобы выстрел
    /// поперёк нити её задел.
    /// </summary>
    public sealed class WeaverThread
    {
        public bool Cut { get; private set; }

        private readonly WeaverSpider _owner;
        private readonly Point[] _points = new Point[3];

        public WeaverThread(WeaverSpider owner)
        {
            _owner = owner;
            for (var i = 0; i < _points.Length; i++) _points[i] = new Point(this, (i + 1) / 4f);
        }

        public void Register()
        {
            foreach (var point in _points) SoloTargets.Add(point);
        }

        public void Unregister()
        {
            foreach (var point in _points) SoloTargets.Remove(point);
        }

        public void Update(Vector3 from, Vector3 to)
        {
            foreach (var point in _points) point.Position = Vector3.Lerp(from, to, point.Share);
        }

        private sealed class Point : ISoloTarget
        {
            public Vector3 Position;
            public readonly float Share;
            private readonly WeaverThread _thread;

            public Point(WeaverThread thread, float share)
            {
                _thread = thread;
                Share = share;
            }

            public bool IsAlive => !_thread.Cut;
            public Vector3 Centre => Position;
            public float HitRadius => 0.45f;
            public int Threat => 0;
            public bool CritAlways => false;

            public float TakeHit(float damage, HitKind kind, DamageSource source, Vector3 from)
            {
                _thread.Cut = true;
                return 0f;
            }

            public void Stagger(Vector3 push, float seconds) { }
        }
    }
}
