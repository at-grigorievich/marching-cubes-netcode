using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Что толпе делать прямо сейчас — по мнению директора забега (<see cref="CaveRunDirector"/>).
    ///
    /// Пока директивы нет, толпа живёт по своим полям, как жила до директора: так её
    /// меряет <c>CatacombCrowdCheck</c>, и числа поведения проверяются отдельно от сложности.
    /// Как только директор есть, население, досыл и состав орды задаёт он, а толпа
    /// отвечает только за то, КАК особи бегут.
    /// </summary>
    public struct CrowdDirective
    {
        /// <summary>Сколько особей держать живыми — до трапеции коротких волн поверх.</summary>
        public int Population;

        /// <summary>Размах коротких волн толпы поверх директивы. Единица — волн нет.</summary>
        public float MicroWave;

        /// <summary>Сколько особей в секунду досылать взамен убитых.</summary>
        public float SpawnRate;

        /// <summary>Множитель живучести новых особей. Дробная часть бросается жребием.</summary>
        public float HealthScale;

        /// <summary>Множитель скорости новых особей.</summary>
        public float SpeedScale;

        /// <summary>
        /// Виды с живучестью больше этой не заводятся. Так тяжёлые виды приходят
        /// по ходу забега, а не с первой секунды.
        /// </summary>
        public int MaxKindHealth;

        /// <summary>Сколько особей пускать в ближний круг игрока.</summary>
        public int NearCap;

        /// <summary>Вид роя, если рой идёт. -1 — роя нет.</summary>
        public int SwarmKind;

        /// <summary>Какая доля новых особей во время роя — его вида.</summary>
        public float SwarmShare;

        /// <summary>Окраска новых особей. Белая — как есть.</summary>
        public Vector3 Hue;

        /// <summary>Остервенение финального роя: засад нет, все бегут сразу.</summary>
        public bool Frenzy;
    }

    /// <summary>
    /// Ссылка на конкретную особь, переживающая переиспользование её слота.
    /// Нужна элите: директор следит, жива ли она, а слот через пару секунд после
    /// смерти занимает кто-то другой.
    /// </summary>
    public readonly struct SpiderHandle
    {
        public readonly int Slot;
        public readonly int Serial;

        public SpiderHandle(int slot, int serial)
        {
            Slot = slot;
            Serial = serial;
        }

        public static SpiderHandle None => new SpiderHandle(-1, 0);

        public bool IsValid => Slot >= 0;
    }
}
