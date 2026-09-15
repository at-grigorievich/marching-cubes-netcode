using Unity.Mathematics;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Голос орды: шорох, громкость и тон которого ведутся числом пауков рядом.
    ///
    /// Звука в проекте не было вообще, и это самая дешёвая крупная прибавка к ощущению
    /// из всего, что можно сделать с толпой: шорох за спиной работает раньше, чем игрок
    /// успевает обернуться, а картинка так не умеет.
    ///
    /// Клип синтезируется кодом, а не берётся из пака. Причина не в экономии: нужен
    /// БЕСШОВНЫЙ слой, длину и спектр которого можно подогнать под то, что происходит,
    /// а любой готовый сэмпл шороха имеет свой ритм, и на цикле этот ритм слышен
    /// как повтор. Здесь же период выбран некратным всему, что на него накладывается.
    /// Тот же приём, что с текстурами породы — проект уже рисует их сам.
    ///
    /// Один источник на всю толпу, а не по источнику на особь: восемь сотен AudioSource
    /// не потянет ни один микшер, да и складывать восемьсот копий одного шороха
    /// бессмысленно — получится белый шум.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    [DisallowMultipleComponent]
    public sealed class CaveCrowdAudio : MonoBehaviour
    {
        [SerializeField] private SpiderCrowd crowd;

        [Tooltip("За кем слушаем. Пусто — возьмётся цель толпы.")]
        [SerializeField] private Transform listener;

        [Header("Слой шороха")]
        [Tooltip("Радиус, в котором пауки слышны, юниты.")]
        [SerializeField, Range(4f, 60f)] private float hearingRadius = 22f;

        [Tooltip("Сколько особей рядом означает полную громкость.")]
        [SerializeField, Range(1, 400)] private int loudAt = 90;

        [SerializeField, Range(0f, 1f)] private float maxVolume = 0.55f;

        /// <summary>
        /// Насколько тон поднимается на полной громкости.
        ///
        /// Подъём тона с ростом числа особей важнее самой громкости: громкость слышна
        /// как «их много», а тон — как «они близко». Оба сразу дают приближение.
        /// </summary>
        [SerializeField, Range(0.5f, 2f)] private float rushPitch = 1.35f;

        [Tooltip("За сколько секунд громкость догоняет цель. Мгновенная скачет на волнах.")]
        [SerializeField, Range(0.05f, 3f)] private float smoothing = 0.45f;

        [Header("Синтез")]
        [Tooltip("Длина кольца шороха, секунды. Некратная всему, что на неё ложится.")]
        [SerializeField, Range(1f, 12f)] private float loopSeconds = 7.3f;

        [Tooltip("Сколько щелчков лап в секунду на полной громкости.")]
        [SerializeField, Range(10f, 600f)] private float ticksPerSecond = 190f;

        [SerializeField] private int seed = 20250915;

        private AudioSource _source;
        private float _volume;

        private void Awake()
        {
            _source = GetComponent<AudioSource>();

            if (crowd == null) crowd = FindFirstObjectByType<SpiderCrowd>();

            _source.clip = BuildSkitter();
            _source.loop = true;
            _source.playOnAwake = false;
            _source.volume = 0f;

            // Плоский, не позиционный: шорох идёт отовсюду сразу, потому что пауки
            // и правда со всех сторон. Позиционный источник в центре толпы давал бы
            // ложное направление — как будто у орды есть одно место.
            _source.spatialBlend = 0f;

            _source.Play();
        }

        private void Update()
        {
            if (crowd == null || _source == null) return;

            if (listener == null) listener = crowd.transform;

            var near = crowd.CountNear(listener.position, hearingRadius);
            var wanted = math.saturate(near / (float)math.max(1, loudAt));

            // Сглаживание по времени, а не по кадрам: на разной частоте кадров иначе
            // получается разная скорость нарастания.
            _volume = math.lerp(_volume, wanted, 1f - math.exp(-Time.deltaTime / math.max(0.01f, smoothing)));

            _source.volume = _volume * maxVolume;
            _source.pitch = math.lerp(1f, rushPitch, _volume);
        }

        /// <summary>
        /// Синтезирует бесшовное кольцо шороха: россыпь коротких щелчков разной высоты.
        ///
        /// Щелчок, а не шум: сплошной шум читается как ветер или вода, а орда должна
        /// звучать как множество мелких твёрдых касаний. Высота у каждого своя, иначе
        /// получается стрекотание одной цикады.
        /// </summary>
        private AudioClip BuildSkitter()
        {
            const int rate = 22050;

            var length = Mathf.RoundToInt(rate * math.max(1f, loopSeconds));
            var data = new float[length];

            var random = new Unity.Mathematics.Random((uint)math.max(1, math.abs(seed)));

            var ticks = Mathf.RoundToInt(ticksPerSecond * loopSeconds);

            for (var i = 0; i < ticks; i++)
            {
                var start = random.NextInt(length);

                // Щелчок: затухающая синусоида в пару миллисекунд.
                var hz = random.NextFloat(900f, 5200f);
                var decay = random.NextFloat(220f, 900f);
                var gain = random.NextFloat(0.15f, 1f);

                var samples = math.min(length, (int)(rate * 0.02f));

                for (var s = 0; s < samples; s++)
                {
                    var t = s / (float)rate;
                    var value = math.sin(2f * math.PI * hz * t) * math.exp(-decay * t) * gain;

                    // Кольцевая запись: щелчок, начавшийся у конца буфера, продолжается
                    // в начале, и шва на стыке не слышно.
                    data[(start + s) % length] += value;
                }
            }

            // Нормируем по пику: щелчки складывались без учёта наложений, и без этого
            // громкость кольца зависит от того, сколько их случайно попало в одно место.
            var peak = 0f;
            for (var i = 0; i < length; i++) peak = math.max(peak, math.abs(data[i]));

            if (peak > 1e-4f)
            {
                var scale = 0.9f / peak;
                for (var i = 0; i < length; i++) data[i] *= scale;
            }

            var clip = AudioClip.Create("Crowd Skitter", length, 1, rate, false);
            clip.SetData(data, 0);

            return clip;
        }
    }
}
