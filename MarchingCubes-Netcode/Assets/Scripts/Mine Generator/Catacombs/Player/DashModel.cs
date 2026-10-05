using System;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Рывок — чистая логика: заряды, восстановление, длительность. Движение по нему делает
    /// стенд контроллером (сквозь породу рывок не проходит).
    ///
    /// Заряды копятся непрерывно, по одному за <see cref="Recharge"/> секунд, как полоски
    /// выносливости Ultrakill: игрок видит, сколько осталось, и рассчитывает отход.
    /// </summary>
    public sealed class DashModel
    {
        public float Distance = 5.5f;
        public float Duration = 0.16f;
        public float Recharge = 1.6f;

        /// <summary>Неуязвимость держится дольше самого рывка — на выходе из толпы не кусают в спину.</summary>
        public float Grace = 0.08f;

        public int MaxCharges { get; private set; } = 2;

        /// <summary>Заряды, дробные: целая часть — готовые рывки, дробная — восстановление следующего.</summary>
        public float Charges { get; private set; } = 2f;

        public bool Active => _left > 0f;

        /// <summary>Неуязвимость ещё держится (рывок плюс запас).</summary>
        public bool Protected => _since < Duration + Grace;

        public int Ready => (int)Math.Floor(Charges + 1e-4f);

        public int Used { get; private set; }

        /// <summary>Скорость рывка, юниты в секунду.</summary>
        public float Speed => Distance / Math.Max(0.01f, Duration);

        private float _left;
        private float _since = 99f;

        public void SetMaxCharges(int charges)
        {
            charges = Math.Max(1, charges);

            if (charges > MaxCharges) Charges += charges - MaxCharges;

            MaxCharges = charges;
            Charges = Math.Min(Charges, MaxCharges);
        }

        public void Reset()
        {
            Charges = MaxCharges;
            _left = 0f;
            _since = 99f;
            Used = 0;
        }

        public bool TryStart()
        {
            if (Active || Charges < 1f) return false;

            Charges -= 1f;
            _left = Duration;
            _since = 0f;
            Used++;

            return true;
        }

        /// <summary>Шаг: сколько пройти рывком в этом кадре, юниты (0 — рывка нет).</summary>
        public float Step(float deltaTime)
        {
            if (deltaTime <= 0f) return 0f;

            _since += deltaTime;
            Charges = Math.Min(MaxCharges, Charges + deltaTime / Math.Max(0.05f, Recharge));

            if (_left <= 0f) return 0f;

            var step = Math.Min(_left, deltaTime);
            _left -= step;

            return step * Speed;
        }

        /// <summary>Оборвать рывок — упёрся в стену.</summary>
        public void Stop() => _left = 0f;
    }
}
