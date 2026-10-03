using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Отладка стенда: наложение со статистикой и отладочные клавиши. Вынесено из
    /// <see cref="CatacombTestRig"/>, чтобы тело игрока не тащило отладку в выпускную сборку:
    /// там этот компонент не делает ничего (клавиши и наложение — только в редакторе
    /// и development-сборке).
    ///
    /// Стенд в редакторе и development-сборке заводит компонент сам, если его нет на объекте, —
    /// старые сцены не теряют клавиш. «Починить свет» пересоздаёт его с умолчаниями из кода.
    ///
    /// Клавиши — отладка, кроме тех, что остались механикой на стенде (ЛКМ, V, E):
    /// F полёт/ходьба, G сквозь стены, T к точке входа, R новый уровень, Q пробный свет,
    /// B взрыв под прицелом, N перемотка стадии на 30 с, P записать ракурс.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CatacombTestRig))]
    public sealed class CatacombDebugOverlay : MonoBehaviour
    {
        [Header("Пробный взрыв")]
        /// <summary>
        /// Радиус пробного взрыва. Пять юнитов — это верхняя оценка радиуса гранаты
        /// из открытых вопросов: медиана простреливаемой линии в уровне 14-16 юнитов,
        /// и взрыв шире пяти-шести начал бы доставать до самого игрока в коридоре.
        /// </summary>
        [Tooltip("Радиус пробного взрыва на клавишу B, если гранатомёта нет.")]
        [SerializeField, Range(1f, 12f)] private float blastRadius = 5f;

        [Header("Интерфейс")]
        [SerializeField] private bool showOverlay = true;

        private CatacombTestRig _rig;

        private void Awake()
        {
            _rig = GetComponent<CatacombTestRig>();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void Update()
        {
            if (_rig == null) return;

            if (Input.GetKeyDown(KeyCode.F)) _rig.ToggleMode();
            if (Input.GetKeyDown(KeyCode.G)) _rig.ToggleNoclip();
            if (Input.GetKeyDown(KeyCode.P)) DumpViewpoint();
            if (Input.GetKeyDown(KeyCode.T)) _rig.TeleportToSpawn();
            if (Input.GetKeyDown(KeyCode.Q)) ThrowFlare();
            if (Input.GetKeyDown(KeyCode.B)) TestBlast();

            // Перемотка стадии — отладка: гребень кривой приходит на третьей минуте,
            // и смотреть рой или финал, выжидая их вживую каждый раз, невозможно.
            var director = _rig.Director;

            if (Input.GetKeyDown(KeyCode.N) && director != null)
            {
                director.SkipAhead(30f);
                Debug.Log($"СТАДИЯ перемотана на 30 с: {director.Describe()}");
            }

            if (Input.GetKeyDown(KeyCode.R)) _rig.RegenerateLevel();
        }

        private void OnGUI()
        {
            if (!showOverlay || _rig == null) return;

            DrawOverlay();
        }
#endif

        /// <summary>
        /// Ставит пробный источник света под ноги — инструмент стенда, а не механика игры.
        ///
        /// Свет в уровне расставляет генератор (см. CaveFixtures), и управлять освещением
        /// игроку не нужно: это аркада на одного, а не кооператив, где команда договаривается,
        /// где будет светло. Клавиша оставлена, чтобы можно было пройти по уровню и глазами
        /// проверить, как встанет лампа в конкретном месте, до того как менять правило
        /// расстановки.
        /// </summary>
        private void ThrowFlare()
        {
            var origin = transform.position + transform.forward * 0.7f - Vector3.up * 0.3f;

            // Подброс вверх поверх броска вперёд: шашка летит по дуге и гасит скорость об пол,
            // а не скользит по нему до ближайшей стены.
            var velocity = transform.forward * 8f + Vector3.up * 2.2f;

            CaveFlare.Throw(origin, velocity);
        }

        /// <summary>
        /// Взрыв под прицелом без полёта гранаты — отладка: проверить разлёт и эффекты,
        /// не целясь. Граната есть (<see cref="CaveGrenadeLauncher"/>), и при нём клавиша
        /// делает ровно то же, что её попадание.
        /// </summary>
        private void TestBlast()
        {
            var origin = transform.position;
            var reach = _rig.Reach;

            var point = Physics.Raycast(origin, transform.forward, out var hit, reach)
                ? hit.point
                : origin + transform.forward * reach;

            var launcher = GetComponent<CaveGrenadeLauncher>();

            if (launcher != null && launcher.isActiveAndEnabled)
            {
                var normal = hit.collider != null ? hit.normal : -transform.forward;
                var dead = launcher.Detonate(point + normal * 0.12f, normal);

                Debug.Log($"ВЗРЫВ (отладка) в ({point.x:0.0}, {point.y:0.0}, {point.z:0.0}): убито {dead}");
                return;
            }

            var crowd = _rig.Crowd;
            var queen = _rig.Queen;

            var torn = CaveWebs.TearAt(point, blastRadius);
            var killed = crowd != null
                ? crowd.DamageSphere(point, blastRadius, CombatUnits.HitPoints, DamageSource.Debug)
                : 0;
            var hitQueen = queen != null && queen.DamageSphere(point, blastRadius, CombatUnits.HitPoints);

            Debug.Log($"ВЗРЫВ в ({point.x:0.0}, {point.y:0.0}, {point.z:0.0}) радиусом {blastRadius:0.0}: " +
                      $"паутин порвано {torn}, пауков убито {killed}" +
                      (hitQueen ? $", Матка {queen.Health:0}/{queen.MaxHealth:0}" : ""));
        }

        /// <summary>
        /// Смещение фонаря хранится в сцене, а не в коде: пересборка скриптов его не двигает.
        /// Старая сцена сохраняет старое значение, поэтому показываем его на экране.
        /// </summary>
        private string LampOffsetText()
        {
            var lamp = GetComponentInChildren<Light>();
            if (lamp == null) return "не найден";

            var offset = lamp.transform.localPosition;

            // Фонарь намеренно вынесен в сторону от глаза. Источник, стоящий ровно в камере,
            // теней не даёт вовсе: всё, что он освещает, по определению видно, а тени прячутся
            // за тем, что их отбрасывает. Замер на развилке: при выносе 0.12 тени съедают 0.0%
            // света, при 1.2 — 3.6%, при 2.5 — 9.3%. Порог проверки держим около рабочего
            // значения, иначе она ругается на правильную сцену.
            var ok = offset.sqrMagnitude > 0.5f && offset.sqrMagnitude < 9f;

            var text = $"({offset.x:0.00}, {offset.y:0.00}, {offset.z:0.00})";
            return ok ? text : $"<color=yellow>{text} — пересоберите сцену</color>";
        }

        /// <summary>
        /// Печатает в консоль всё, что нужно для воспроизведения текущего ракурса.
        /// Отдельно отвечает на главный вопрос про тёмные пятна: камера в воздухе или
        /// внутри породы. Внутри обратные грани отсекаются и виден цвет очистки камеры —
        /// выглядит как чернота, но затенением не является.
        /// </summary>
        private void DumpViewpoint()
        {
            var position = transform.position;

            var inside = Physics.CheckSphere(position, 0.2f, ~0, QueryTriggerInteraction.Ignore);

            var ahead = Physics.Raycast(position, transform.forward, out var hit, 200f)
                ? hit.distance.ToString("0.00") + " юнита"
                : "ничего в пределах 200 юнитов";

            var nearest = float.MaxValue;
            var directions = new[]
            {
                Vector3.up, Vector3.down, Vector3.left,
                Vector3.right, Vector3.forward, Vector3.back
            };

            foreach (var dir in directions)
            {
                if (Physics.Raycast(position, dir, out var probe, 50f)) nearest = Mathf.Min(nearest, probe.distance);
            }

            var world = _rig.World;
            var seed = world != null ? world.CurrentSeed : 0;
            var angles = transform.eulerAngles;

            Debug.Log(
                $"РАКУРС сид={seed} позиция=({position.x:0.00}, {position.y:0.00}, {position.z:0.00}) " +
                $"поворот=({angles.x:0.0}, {angles.y:0.0}, {angles.z:0.0}) | " +
                $"камера внутри породы: {(inside ? "ДА" : "нет")}, " +
                $"noclip: {(_rig.Noclip ? "включён" : "выключен")}, " +
                $"режим: {(_rig.Mode == CatacombTestRig.MoveMode.Fly ? "полёт" : "ходьба")} | " +
                $"до поверхности по взгляду: {ahead}, ближайшая поверхность вокруг: " +
                $"{(nearest < float.MaxValue ? nearest.ToString("0.00") + " юнита" : "дальше 50 юнитов")}");
        }

        /// <summary>
        /// Объявление стадии крупно по центру: «РОЙ», «ЭЛИТА», «ЩИТ». Мелкой строкой
        /// в углу его не заметить посреди боя, а от него зависит, что делать дальше.
        /// </summary>
        private void DrawBanner(CaveRunDirector director)
        {
            if (director == null || director.BannerLeft <= 0f || string.IsNullOrEmpty(director.Banner)) return;

            var banner = new GUIStyle(GUI.skin.label)
            {
                fontSize = 26,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                richText = true
            };

            var alpha = Mathf.Clamp01(director.BannerLeft);
            var previous = GUI.color;

            GUI.color = new Color(0f, 0f, 0f, 0.6f * alpha);
            GUI.Label(new Rect(2, Screen.height * 0.18f + 2, Screen.width, 40), director.Banner, banner);

            GUI.color = new Color(1f, 0.85f, 0.6f, alpha);
            GUI.Label(new Rect(0, Screen.height * 0.18f, Screen.width, 40), director.Banner, banner);

            GUI.color = previous;
        }

        private void DrawOverlay()
        {
            var world = _rig.World;
            var crowd = _rig.Crowd;
            var director = _rig.Director;
            var queen = _rig.Queen;

            var style = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };

            DrawBanner(director);

            GUILayout.BeginArea(new Rect(10, 10, 640, 340), GUI.skin.box);

            if (world != null && world.IsGenerating)
            {
                GUILayout.Label($"<b>Генерация: {world.Progress:P0}</b>", style);
            }
            else if (world != null)
            {
                var tris = 0;
                foreach (var chunk in world.Chunks) tris += (int)(chunk.Mesh.GetIndexCount(0) / 3);

                GUILayout.Label($"<b>Сид {world.CurrentSeed}</b>   чанков {world.Chunks.Count}   треугольников {tris:N0}", style);
                GUILayout.Label($"Режим: <b>{(_rig.Mode == CatacombTestRig.MoveMode.Fly ? "полёт" : "ходьба")}</b>" +
                                $"{(_rig.Noclip ? " (сквозь стены)" : "")}" +
                                (_rig.DiggingEnabled ? $"   радиус кисти: <b>{_rig.DigRadius:0.0}</b>" : ""), style);
                // Координаты прямо в интерфейсе: тогда любой присланный скриншот
                // самодостаточен, и не нужно ловить момент нажатия P.
                var pos = transform.position;
                var rot = transform.eulerAngles;

                GUILayout.Label($"<b>({pos.x:0.0}, {pos.y:0.0}, {pos.z:0.0})</b>  " +
                                $"поворот ({rot.x:0.0}, {rot.y:0.0})  " +
                                $"фонарь {LampOffsetText()}", style);

                if (_rig.DiggingEnabled)
                {
                    GUILayout.Label($"Добыто породы, оценка сверху: {_rig.TotalDugVolume:N0} " +
                                    $"(последний удар {_rig.LastDigVolume:N0})", style);
                }

                if (crowd != null) GUILayout.Label(crowd.Describe(), style);

                if (director != null) GUILayout.Label(director.Describe(), style);
                if (queen != null) GUILayout.Label(queen.Describe(transform.position), style);

                if (_rig.IsEntangled) GUILayout.Label("<color=#b8e0a0>СПУТАН паутиной</color>", style);

                if (!_rig.HasSpawned) GUILayout.Label("<color=yellow>Нажмите T — телепорт к точке входа</color>", style);
            }
            else
            {
                GUILayout.Label("<color=red>CatacombWorld не найден на сцене</color>", style);
            }

            GUILayout.Space(6);
            var armed = GetComponent<CaveGrenadeLauncher>() != null;
            var digging = _rig.DiggingEnabled;

            GUILayout.Label(_rig.IsLooking
                ? (digging ? "ЛКМ — копать    ПКМ — зарастить    колесо — радиус\n" : "") +
                  (armed && !digging ? "ЛКМ — граната    V — перезарядка\n" : "") +
                  "WASD — движение    Shift — ускорение    Space/Ctrl — вверх/вниз\n" +
                  "F — полёт/ходьба    G — сквозь стены    T — к точке входа\n" +
                  $"Q — пробный свет, отладка ({CaveFlare.Live.Count} из {CaveFlare.MaxLive})    " +
                  "R — новый уровень\n" +
                  "E — разбудить Матку у логова    Shift+E — к логову (отладка)    " +
                  "N — перемотать стадию на 30 с (отладка)\n" +
                  (armed ? "B — взрыв под прицелом (отладка)    " : $"B — пробный взрыв, радиус {blastRadius:0.0}    ") +
                  "P — записать ракурс    Esc — отпустить курсор"
                : "<color=yellow>Кликните по окну игры, чтобы захватить курсор</color>", style);

            GUILayout.EndArea();
        }
    }
}
