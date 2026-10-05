using System;

namespace MineGenerator.Catacombs
{
    /// <summary>Почему игрок неуязвим. Счётчик причин, а не флаг: рывок не снимает неуязвимость возрождения.</summary>
    public enum InvulnReason
    {
        Dash,
        Revive,
        Cutscene,

        /// <summary>Отладка стенда и прогоны: бессмертие, пока не снимут.</summary>
        Debug,

        Count
    }

    /// <summary>Что сделал удар — для событий, интерфейса и прогона.</summary>
    public struct DamageResult
    {
        /// <summary>Сколько HP ушло всего (временное плюс основное).</summary>
        public float Dealt;

        /// <summary>Сколько срезал потолок урона в секунду.</summary>
        public float Capped;

        /// <summary>Сколько срезала броня.</summary>
        public float Armored;

        public bool Killed;

        /// <summary>Удар пришёлся в неуязвимость и не прошёл вовсе.</summary>
        public bool Blocked;
    }

    /// <summary>
    /// Здоровье игрока — чистая логика, без движка (тестируется вне Unity, грабли №69).
    ///
    /// Удар идёт по цепочке: неуязвимость → броня (из КАЖДОГО укуса, но не ниже единицы,
    /// как броня Vampire Survivors) → потолок урона в секунду → временное здоровье → основное.
    ///
    /// Потолок нужен, потому что куча в несколько ярусов кусает десятками разом: без него
    /// окружённый умирал бы за долю секунды, и смерть читалась бы как сбой, а не как ошибка.
    /// Всё, что сверх доли максимума за скользящую секунду, проходит ослабленным (умолчания
    /// модели — для тестов; игровые числа ставит <see cref="PlayerVitals"/>: 15% и 0.07,
    /// подобраны SurvivalCheck под смерть окружённого за 4–7 с на гребне).
    ///
    /// Временное здоровье — из Vermintide 2: даётся за убийства вплотную и тает, если его
    /// не пополнять. Это главный приём против пассивной игры: лечиться можно, только нападая.
    /// Вместе с основным оно не больше максимума — на полосе оно серое продолжение красной.
    /// </summary>
    public sealed class VitalsModel
    {
        /// <summary>Доля максимума, которая проходит за скользящую секунду в полную силу.</summary>
        public float CapShare = 0.22f;

        /// <summary>С какой силой проходит урон сверх потолка.</summary>
        public float OverflowScale = 0.25f;

        public float CapWindow = 1f;

        /// <summary>Через сколько секунд без пополнения временное здоровье начинает таять.</summary>
        public float TempDelay = 3f;

        /// <summary>Как быстро тает временное здоровье, HP в секунду.</summary>
        public float TempDecay = 2f;

        public float MaxHealth { get; private set; } = 100f;
        public float Health { get; private set; } = 100f;
        public float Temp { get; private set; }

        /// <summary>Вычитается из каждого укуса (но не ниже 1).</summary>
        public float Armor { get; set; }

        /// <summary>HP в секунду.</summary>
        public float Regen { get; set; }

        /// <summary>Сколько раз можно возродиться без рекламы.</summary>
        public int Revives { get; set; }

        public bool Dead { get; private set; }

        /// <summary>Своё время модели — секунды с начала, ведётся <see cref="Tick"/>.</summary>
        public float Now { get; private set; }

        public float Fraction => MaxHealth > 0f ? Health / MaxHealth : 0f;

        /// <summary>Сколько HP прошло за скользящее окно потолка — для прогона.</summary>
        public float RecentIntake => SumWindow(_windowDealt);

        /// <summary>Сколько всего срезал потолок за жизнь модели — для прогона.</summary>
        public float TotalCapped { get; private set; }

        public float TotalTaken { get; private set; }

        private readonly float[] _invuln = new float[(int)InvulnReason.Count];
        private float _lastTempGain = -999f;

        // Скользящее окно потолка — кольцо: укусы сложены по кадру, так что записей
        // не больше частоты кадров плюс удары Матки.
        private const int Ring = 128;
        private readonly float[] _windowTime = new float[Ring];

        /// <summary>
        /// Сколько прошло В ПОЛНУЮ СИЛУ: в каждой скользящей секунде так проходит только первая
        /// порция до потолка, остальное — с долей <see cref="OverflowScale"/>. Итог при натиске R:
        /// потолок плюс доля от того, что сверх него.
        ///
        /// Две неудачные версии. Копить прошедшее после среза: срезанный остаток тоже копится,
        /// окно сидит на потолке, и натиск сверх него не проходит почти совсем — финальный рой
        /// убивал так же медленно, как гребень (5.6 с против 6.4). Копить входящий до среза:
        /// окно держит весь натиск и не открывается вовсе, проходит только доля — 13.4 с на гребне.
        /// </summary>
        private readonly float[] _windowAmount = new float[Ring];

        /// <summary>Прошедшее после потолка — только для показаний.</summary>
        private readonly float[] _windowDealt = new float[Ring];

        private int _windowHead;

        public bool Invulnerable
        {
            get
            {
                for (var i = 0; i < _invuln.Length; i++)
                {
                    if (_invuln[i] > 0f) return true;
                }

                return false;
            }
        }

        public bool IsHeld(InvulnReason reason) => _invuln[(int)reason] > 0f;

        /// <summary>Неуязвимость на время. Повторная та же причина продлевает, а не складывает.</summary>
        public void HoldInvulnerable(InvulnReason reason, float seconds)
        {
            var i = (int)reason;
            _invuln[i] = Math.Max(_invuln[i], seconds);
        }

        public void ReleaseInvulnerable(InvulnReason reason) => _invuln[(int)reason] = 0f;

        /// <summary>Начать заново: полное здоровье, без временного, живой.</summary>
        public void Reset(float maxHealth)
        {
            MaxHealth = Math.Max(1f, maxHealth);
            Health = MaxHealth;
            Temp = 0f;
            Dead = false;
            Now = 0f;
            TotalCapped = 0f;
            TotalTaken = 0f;
            _lastTempGain = -999f;

            Array.Clear(_invuln, 0, _invuln.Length);
            Array.Clear(_windowTime, 0, Ring);
            Array.Clear(_windowAmount, 0, Ring);
            Array.Clear(_windowDealt, 0, Ring);
        }

        /// <summary>
        /// Новый максимум (том «Живучесть», мета). Прибавка максимума прибавляет и текущее —
        /// как в VS: иначе взятый на половине здоровья том ощущался бы пустым.
        /// </summary>
        public void SetMaxHealth(float maxHealth)
        {
            maxHealth = Math.Max(1f, maxHealth);

            if (maxHealth > MaxHealth && !Dead) Health += maxHealth - MaxHealth;

            MaxHealth = maxHealth;
            Health = Math.Min(Health, MaxHealth);
            Temp = Math.Min(Temp, Math.Max(0f, MaxHealth - Health));
        }

        public DamageResult TakeDamage(in DamageHit hit)
        {
            var result = new DamageResult();

            if (Dead || hit.Amount <= 0f) return result;

            if (Invulnerable)
            {
                result.Blocked = true;
                return result;
            }

            // Броня — из каждого укуса, но укус не слабее единицы. Сумма укусов за кадр
            // это приближение: точно, когда все укусы сильнее брони, а слабее брони
            // укус и так даёт единицу.
            var count = Math.Max(1, hit.Count);
            var amount = hit.Count == 0
                ? hit.Amount
                : hit.PerBiteArmor || count == 1
                    ? Math.Max(count, hit.Amount - Armor * count)
                    : Math.Max(1f, hit.Amount - Armor);

            result.Armored = hit.Amount - amount;

            var cap = CapShare * MaxHealth;
            var room = Math.Max(0f, cap - SumWindow(_windowAmount));

            var full = Math.Min(amount, room);
            var dealt = full + (amount - full) * OverflowScale;

            result.Capped = amount - dealt;
            TotalCapped += result.Capped;

            Record(full, dealt);

            var fromTemp = Math.Min(Temp, dealt);
            Temp -= fromTemp;
            Health -= dealt - fromTemp;

            TotalTaken += dealt;
            result.Dealt = dealt;

            if (Health <= 0.001f)
            {
                Health = 0f;
                Temp = 0f;
                Dead = true;
                result.Killed = true;
            }

            return result;
        }

        public void Heal(float amount)
        {
            if (Dead || amount <= 0f) return;

            Health = Math.Min(MaxHealth, Health + amount);
            Temp = Math.Min(Temp, Math.Max(0f, MaxHealth - Health));
        }

        /// <summary>Временное здоровье — за убийство вплотную. Вместе с основным не больше максимума.</summary>
        public void AddTemp(float amount)
        {
            if (Dead || amount <= 0f) return;

            Temp = Math.Min(Temp + amount, Math.Max(0f, MaxHealth - Health));
            _lastTempGain = Now;
        }

        /// <summary>Возрождение: доля здоровья и неуязвимость, чтобы не загрызли на том же кадре.</summary>
        public void Revive(float share, float invulnerableSeconds)
        {
            Dead = false;
            Health = Math.Max(1f, MaxHealth * share);
            Temp = 0f;

            Array.Clear(_windowAmount, 0, Ring);
            Array.Clear(_windowDealt, 0, Ring);

            HoldInvulnerable(InvulnReason.Revive, invulnerableSeconds);
        }

        /// <summary>Убить сразу — отладка и прогоны.</summary>
        public void Kill()
        {
            Health = 0f;
            Temp = 0f;
            Dead = true;
        }

        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f) return;

            Now += deltaTime;

            for (var i = 0; i < _invuln.Length; i++)
            {
                if (_invuln[i] > 0f && !float.IsPositiveInfinity(_invuln[i])) _invuln[i] = Math.Max(0f, _invuln[i] - deltaTime);
            }

            if (Dead) return;

            if (Regen > 0f) Health = Math.Min(MaxHealth, Health + Regen * deltaTime);

            if (Temp > 0f && Now - _lastTempGain > TempDelay)
            {
                Temp = Math.Max(0f, Temp - TempDecay * deltaTime);
            }

            Temp = Math.Min(Temp, Math.Max(0f, MaxHealth - Health));
        }

        private void Record(float amount, float dealt)
        {
            _windowTime[_windowHead] = Now;
            _windowAmount[_windowHead] = amount;
            _windowDealt[_windowHead] = dealt;
            _windowHead = (_windowHead + 1) % Ring;
        }

        private float SumWindow(float[] amounts)
        {
            var sum = 0f;
            var from = Now - CapWindow;

            for (var i = 0; i < Ring; i++)
            {
                if (amounts[i] > 0f && _windowTime[i] > from) sum += amounts[i];
            }

            return sum;
        }
    }
}
