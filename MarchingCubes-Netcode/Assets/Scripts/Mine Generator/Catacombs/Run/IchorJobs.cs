using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MineGenerator.Catacombs
{
    /// <summary>Что за капля: опыт, валюта или кокон с добычей.</summary>
    public enum DropKind
    {
        /// <summary>Ихор — опыт.</summary>
        Ichor = 0,

        /// <summary>Хитин — валюта меты (M4).</summary>
        Chitin = 1,

        /// <summary>Кокон с добычей — сундук VS: гарантированная эволюция или улучшение пушки.</summary>
        Cocoon = 2,

        Count
    }

    /// <summary>Капля на камне. Локальные координаты генерации — в них же поле потока толпы.</summary>
    public struct IchorDrop
    {
        public float3 Position;
        public float3 Normal;
        public float Age;
        public float Value;
        public float Seed;

        /// <summary>Сколько капель слилось в эту — для размера.</summary>
        public float Merged;

        public int Kind;
        public int Alive;
    }

    /// <summary>
    /// Шаг капель: лежит, потом ползёт к игроку по полю потока (тем же ходам, что пауки),
    /// разгоняясь; в радиусе магнита — напрямую; в радиусе подбора — подобрана.
    ///
    /// Один поток, а не параллельно: капель шесть сотен, а подобранное складывается в сумму
    /// по видам — без атомиков так проще, и на WebGL воркеров всё равно нет.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct IchorJob : IJob
    {
        public NativeArray<IchorDrop> Drops;

        public SpiderFlowField.FlowSampler Field;
        public int FieldValid;

        /// <summary>Тело игрока, локально.</summary>
        public float3 Player;

        public float DeltaTime;
        public float LieSeconds;
        public float CrawlFrom;
        public float CrawlTo;
        public float CrawlRamp;
        public float MagnetRadius;
        public float MagnetSpeed;
        public float PickupRadius;

        /// <summary>Подобранное по видам: [вид] — сумма ценности, [вид + 3] — штук.</summary>
        public NativeArray<float> Collected;

        /// <summary>0 — живых капель после шага.</summary>
        public NativeArray<int> Alive;

        public void Execute()
        {
            var alive = 0;
            var magnetSq = MagnetRadius * MagnetRadius;
            var pickupSq = PickupRadius * PickupRadius;

            for (var i = 0; i < Drops.Length; i++)
            {
                var drop = Drops[i];
                if (drop.Alive == 0) continue;

                drop.Age += DeltaTime;

                var toPlayer = Player - drop.Position;
                var distanceSq = math.lengthsq(toPlayer);

                if (distanceSq <= pickupSq)
                {
                    drop.Alive = 0;
                    Drops[i] = drop;

                    var kind = math.clamp(drop.Kind, 0, 2);
                    Collected[kind] += drop.Value;
                    Collected[kind + 3] += 1f;
                    continue;
                }

                alive++;

                if (distanceSq <= magnetSq && drop.Kind != (int)DropKind.Cocoon)
                {
                    // Магнит тянет напрямую: в трёх юнитах стенки между каплей и игроком не бывает
                    // настолько, чтобы это было заметно, а прямой подлёт читается как «поймал».
                    var distance = math.sqrt(distanceSq);
                    var step = math.min(distance, MagnetSpeed * DeltaTime);

                    drop.Position += toPlayer / math.max(1e-4f, distance) * step;
                }
                else if (drop.Age > LieSeconds && drop.Kind != (int)DropKind.Cocoon)
                {
                    Crawl(ref drop, toPlayer);
                }

                Drops[i] = drop;
            }

            Alive[0] = alive;
        }

        /// <summary>
        /// Ползёт по полю потока, прижимаясь к камню, как паук. Разгон от 2 до 8 юнитов в секунду:
        /// сразу после лежания капля медленная и её видно, а дальняя добегает быстро.
        /// </summary>
        private void Crawl(ref IchorDrop drop, float3 toPlayer)
        {
            var speed = math.min(CrawlTo, CrawlFrom + (drop.Age - LieSeconds) * CrawlRamp);
            var direction = float3.zero;

            if (FieldValid != 0)
            {
                Field.SampleAt(drop.Position, out var flow, out var normal, out var depth, out var valid);

                if (valid)
                {
                    direction = flow;
                    drop.Normal = normal;

                    // Прижим к камню — на ширину капли над ним.
                    drop.Position -= normal * ((depth - 0.15f) * math.saturate(DeltaTime * 10f));
                }
            }

            // Вне поля (или у самого игрока, где поток вырождается) — прямо на игрока.
            if (math.lengthsq(direction) < 1e-4f || math.lengthsq(toPlayer) < 9f) direction = math.normalizesafe(toPlayer);

            drop.Position += direction * (speed * DeltaTime);
        }
    }
}
