using Unity.Mathematics;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Одиночка — особый паук (M3.2): не особь толпы, а объект со своей логикой. Их единицы,
    /// и тащить их правила в джоб толпы (тысячи строк под Burst) значило бы усложнить то, что
    /// тщательно выверено. Здесь — общее: здоровье в HP, статусы, шаг по поверхности по тому же
    /// полю потока, что у толпы, прижим к камню тем же правилом, отрисовка через
    /// <see cref="SoloSpiderView"/>, попадания через <see cref="ISoloTarget"/>.
    ///
    /// Координаты внутри — локальные координаты генерации (как у толпы и поля потока); наружу —
    /// мировые. Шаг по поверхности повторяет <c>SpiderMoveJob.Advance</c> в упрощённом виде:
    /// сначала шаг целиком, потом по осям (грабли №35), проходимость — по глубине, а не по
    /// клетке под координатой (грабли №11), прижим изнутри — сразу, снаружи — плавно.
    /// </summary>
    public abstract class SoloSpider : ISoloTarget
    {
        public readonly SoloSpiderView View = new SoloSpiderView();

        public float Health { get; protected set; }
        public float MaxHealth { get; protected set; }

        /// <summary>
        /// Точка тела у камня и нормаль поверхности — в локальных координатах генерации. Как
        /// позиция особи толпы: на <see cref="Hover"/> над поверхностью, так её держат и прижим,
        /// и <c>TryFindSurface</c>.
        /// </summary>
        protected float3 Foot;
        protected float3 Up = new float3(0f, 1f, 0f);

        protected quaternion Rotation = quaternion.identity;

        /// <summary>Скорость, юниты в секунду, — задаёт поведение.</summary>
        protected float Speed = 3f;

        /// <summary>Зазор между лапами и камнем — как у толпы, по масштабу вида.</summary>
        protected float Hover => View.Kind != null ? View.Kind.Hover * View.Scale : 0.06f;

        protected CaveSpecials Owner { get; private set; }
        protected SpiderCrowd Crowd => Owner.Crowd;
        protected IPlayerBody Player => Owner.Player;

        /// <summary>Сколько секунд живёт — для меток и расписания.</summary>
        public float Age { get; private set; }

        public bool Dead { get; private set; }

        /// <summary>Уже можно убрать: смерть доиграна и труп погас.</summary>
        public bool Gone => Dead && _deadFor > 4f;

        private float _deadFor;

        // Статусы — те же, что у толпы (M2.2), полями.
        private float _burnLeft;
        private float _burnDps;
        private float _slowLeft;
        private float _slowShare;
        private float _stunLeft;
        private float3 _push;

        public abstract SpecialKind Type { get; }

        /// <summary>Вид пака, на котором держится особый, и его множитель и оттенок.</summary>
        protected abstract string KindName { get; }
        protected abstract float SizeScale { get; }
        protected abstract Color Hue { get; }

        /// <summary>Взять вид из толпы по имени; нет такого — самый крупный.</summary>
        public bool Bind(SpiderCrowd crowd)
        {
            if (crowd == null || crowd.KindCount == 0) return false;

            SpiderKind kind = null;

            for (var i = 0; i < crowd.KindCount; i++)
            {
                var candidate = crowd.KindAt(i);
                if (candidate != null && candidate.name == KindName) kind = candidate;
            }

            if (kind == null) kind = crowd.KindAt(crowd.HeaviestKind);

            return View.Bind(kind, SizeScale, Hue);
        }

        // ------------------------------------------------------------------ ISoloTarget

        public bool IsAlive => !Dead;

        public virtual Vector3 Centre => WorldFoot + (Vector3)(math.mul(Owner.WorldRotation, Up) * (View.BodyLift - Hover));

        public float HitRadius => View.HitRadius;

        public virtual int Threat => 50;

        public bool CritAlways => true;

        public Vector3 WorldFoot => Owner.ToWorld(Foot);
        public Vector3 WorldUp => math.mul(Owner.WorldRotation, Up);

        /// <summary>Куда смотрит голова, мир.</summary>
        public Vector3 WorldForward => (Vector3)math.mul(Owner.WorldRotation, math.forward(Rotation)) * View.Facing;

        public float TakeHit(float damage, HitKind kind, DamageSource source, Vector3 from)
        {
            if (Dead || damage <= 0f) return 0f;

            var dealt = Mathf.Min(Health, damage * DamageTakenScale(kind, from));
            Health -= dealt;

            OnHurt(dealt, kind, from);

            if (Health <= 0.01f) Die(source);

            return dealt;
        }

        public void Stagger(Vector3 push, float seconds)
        {
            if (Dead) return;

            _push += Owner.ToLocalDirection(push);
            _stunLeft = Mathf.Max(_stunLeft, seconds);

            OnStaggered(seconds);
        }

        /// <summary>Горение и замедление — от огнемёта и гвоздей, как у толпы.</summary>
        public void Burn(float dps, float seconds)
        {
            _burnDps = Mathf.Max(_burnDps, dps);
            _burnLeft = Mathf.Max(_burnLeft, seconds);
        }

        public void Slow(float share, float seconds)
        {
            _slowShare = Mathf.Max(_slowShare, share);
            _slowLeft = Mathf.Max(_slowLeft, seconds);
        }

        protected bool Stunned => _stunLeft > 0f;

        protected float SpeedScale => (_slowLeft > 0f ? 1f - _slowShare : 1f) * (Stunned ? 0f : 1f);

        // ------------------------------------------------------------------ жизнь

        public void Spawn(CaveSpecials owner, float3 foot, float3 up, float health)
        {
            Owner = owner;
            Foot = foot;
            Up = math.normalizesafe(up, new float3(0f, 1f, 0f));
            MaxHealth = Health = health;

            OnSpawn();
            SoloTargets.Add(this);
        }

        /// <summary>Шаг: статусы, поведение, клип.</summary>
        public void Tick(float deltaTime)
        {
            Age += deltaTime;

            if (Dead)
            {
                _deadFor += deltaTime;
                View.Advance(deltaTime);
                return;
            }

            if (_burnLeft > 0f)
            {
                _burnLeft -= deltaTime;
                TakeHit(_burnDps * deltaTime, HitKind.Fire, DamageSource.Flamer, WorldFoot);
                if (Dead) return;
            }

            _slowLeft = Mathf.Max(0f, _slowLeft - deltaTime);
            _stunLeft = Mathf.Max(0f, _stunLeft - deltaTime);

            if (math.lengthsq(_push) > 1e-4f)
            {
                Step(_push * deltaTime);
                _push *= math.saturate(1f - deltaTime * 6f);
            }

            Behave(deltaTime);

            View.Advance(deltaTime);
        }

        public virtual void Draw(int layer)
        {
            View.Draw(WorldFoot - WorldUp * Hover, math.mul(Owner.WorldRotation, Rotation), layer, false);
        }

        public void Remove()
        {
            SoloTargets.Remove(this);
        }

        /// <summary>Умереть без чужого удара — бомбардир, лопнувший сам.</summary>
        protected void Kill(DamageSource source) => Die(source);

        /// <summary>Встать на ближайшую поверхность у точки (мир) — после прыжка.</summary>
        protected void Land(Vector3 worldPoint)
        {
            if (!Owner.TryGetSampler(out var field)) return;

            var local = Owner.ToLocal(worldPoint);
            if (!field.TryFindSurface(local, Hover, out var point)) return;

            field.SampleAt(point, out _, out var normal, out _, out var valid);

            Foot = point;
            if (valid) Up = normal;
        }

        private void Die(DamageSource source)
        {
            if (Dead) return;

            Dead = true;
            Health = 0f;
            View.Play(SpiderClip.Dead);
            SoloTargets.Remove(this);

            OnDeath(source);
            Owner.ReportKill(this, source);
        }

        // ------------------------------------------------------------------ поведение

        protected abstract void Behave(float deltaTime);

        protected virtual void OnSpawn() { }

        protected virtual void OnDeath(DamageSource source) { }

        protected virtual void OnHurt(float dealt, HitKind kind, Vector3 from) { }

        protected virtual void OnStaggered(float seconds) { }

        protected virtual float DamageTakenScale(HitKind kind, Vector3 from) => 1f;

        // ------------------------------------------------------------------ движение

        /// <summary>
        /// Идти по поверхности: к игроку по полю потока (toward = true) или прочь (false),
        /// со скоростью <see cref="Speed"/> × замедление. Возвращает, сдвинулась ли.
        /// </summary>
        protected bool Walk(float deltaTime, bool toward = true)
        {
            if (!Owner.TryGetSampler(out var field)) return false;

            float3 direction;

            if (toward)
            {
                field.SampleAt(Foot, out var flow, out _, out _, out var valid);
                if (!valid) return Reattach(field);
                direction = flow;
            }
            else if (!field.TryEscape(Foot, Up, out direction))
            {
                return false;
            }

            var tangent = direction - Up * math.dot(direction, Up);
            if (math.lengthsq(tangent) < 1e-4f) return false;

            tangent = math.normalize(tangent);

            var moved = Step(tangent * (Speed * SpeedScale * deltaTime));
            Face(tangent, deltaTime);
            View.Play(moved ? SpiderClip.Walk : SpiderClip.Idle);

            return moved;
        }

        /// <summary>Идти по поверхности к точке (локальные координаты), без поля потока.</summary>
        protected bool WalkTo(float3 target, float deltaTime)
        {
            var direction = target - Foot;
            var tangent = direction - Up * math.dot(direction, Up);
            if (math.lengthsq(tangent) < 0.01f) return false;

            tangent = math.normalize(tangent);

            var moved = Step(tangent * (Speed * SpeedScale * deltaTime));
            Face(tangent, deltaTime);
            View.Play(moved ? SpiderClip.Walk : SpiderClip.Idle);

            return moved;
        }

        /// <summary>Шаг по поверхности со скольжением вдоль препятствий и прижимом.</summary>
        protected bool Step(float3 step)
        {
            if (!Owner.TryGetSampler(out var field)) return false;

            if (!field.CanStand(Foot + step))
            {
                if (!field.CanStand(Foot + new float3(step.x, 0f, 0f))) step.x = 0f;
                if (!field.CanStand(Foot + new float3(0f, step.y, 0f))) step.y = 0f;
                if (!field.CanStand(Foot + new float3(0f, 0f, step.z))) step.z = 0f;
            }

            if (math.lengthsq(step) < 1e-8f) return false;

            var previous = Foot;
            Foot += step;

            field.SampleAt(Foot, out _, out var normal, out var depth, out var valid);

            if (!valid)
            {
                Foot = previous;
                return false;
            }

            Up = math.normalizesafe(math.lerp(Up, normal, 0.25f), normal);

            // Прижим: изнутри — сразу, снаружи — плавно (как Cling у толпы).
            var rate = depth < 0f ? 1f : 0.5f;
            Foot -= normal * ((depth - Hover) * rate);

            return true;
        }

        /// <summary>Под ногами нет поверхности — вернуться к ближайшей (грабли №36).</summary>
        private bool Reattach(SpiderFlowField.FlowSampler field)
        {
            if (!field.TryFindSurface(Foot, Hover, out var point)) return false;

            Foot = point;
            return true;
        }

        /// <summary>Довернуться к направлению по касательной (вырожденный случай — без поворота).</summary>
        protected void Face(float3 direction, float deltaTime, float degreesPerSecond = 360f)
        {
            var tangent = direction - Up * math.dot(direction, Up);
            if (math.lengthsq(tangent) < 1e-4f) return;

            var wanted = quaternion.LookRotationSafe(math.normalize(tangent) * View.Facing, Up);
            var angle = Quaternion.Angle(Rotation, wanted);
            var step = degreesPerSecond * deltaTime;

            Rotation = angle <= step ? wanted : math.slerp(Rotation, wanted, step / angle);
        }

        /// <summary>Повернуться к игроку (касательно к своей поверхности).</summary>
        protected void FacePlayer(float deltaTime, float degreesPerSecond = 360f)
        {
            if (Player == null) return;

            Face(Owner.ToLocalDirection(Player.BodyCentre - WorldFoot), deltaTime, degreesPerSecond);
        }

        // ------------------------------------------------------------------ помощники

        protected float DistanceToPlayer => Player != null ? Vector3.Distance(Centre, Player.BodyCentre) : float.MaxValue;

        /// <summary>Видит ли игрока: луч из тела не упирается в породу.</summary>
        protected bool SeesPlayer()
        {
            if (Player == null) return false;

            var from = Centre;
            var to = Player.BodyCentre;
            var delta = to - from;
            var distance = delta.magnitude;

            return distance < 1f || !Physics.Raycast(from, delta / distance, distance - 0.8f, ~0, QueryTriggerInteraction.Ignore);
        }
    }
}
