using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Отладочный стенд: полетать по катакомбам, пройти их ногами и покопать.
    /// Не часть игры — нужен, чтобы глазами проверить генерацию и разрушаемость.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class CatacombTestRig : MonoBehaviour
    {
        public enum MoveMode
        {
            Fly = 0,
            Walk = 1
        }

        [SerializeField] private CatacombWorld world;

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

        [Header("Интерфейс")]
        [SerializeField] private bool showOverlay = true;

        private CharacterController _controller;
        private float _pitch;
        private float _yaw;
        private float _verticalSpeed;
        private bool _looking;
        private bool _spawned;

        private readonly RaycastHit[] _sweepHits = new RaycastHit[8];

        private float _lastDigVolume;
        private float _totalDugVolume;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();

            var angles = transform.eulerAngles;
            _yaw = angles.y;
            _pitch = angles.x;

            if (world == null) world = FindFirstObjectByType<CatacombWorld>();
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
            UpdateCursor();
            UpdateLook();
            UpdateMove();
            UpdateActions();
        }

        private void UpdateCursor()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) SetLooking(false);

            // Клик по окну игры захватывает курсор; копание при этом уже работает.
            if (!_looking && Input.GetMouseButtonDown(0)) SetLooking(true);
        }

        private void SetLooking(bool value)
        {
            _looking = value;
            Cursor.lockState = value ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !value;
        }

        private void UpdateLook()
        {
            if (!_looking) return;

            _yaw += Input.GetAxisRaw("Mouse X") * mouseSensitivity;
            _pitch = Mathf.Clamp(_pitch - Input.GetAxisRaw("Mouse Y") * mouseSensitivity, -89f, 89f);

            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        private void UpdateMove()
        {
            var input = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
            var boost = Input.GetKey(KeyCode.LeftShift) ? boostMultiplier : 1f;

            if (mode == MoveMode.Fly)
            {
                _controller.enabled = false;

                var direction = transform.rotation * input;

                if (Input.GetKey(KeyCode.Space)) direction += Vector3.up;
                if (Input.GetKey(KeyCode.LeftControl)) direction += Vector3.down;

                var motion = direction.normalized * (flySpeed * boost * Time.deltaTime);

                // Без этого полёт заносит камеру внутрь породы, и кадр чернеет:
                // обратные грани отсекаются, и видно цвет очистки камеры.
                transform.position = noclip ? transform.position + motion : SweepMove(transform.position, motion);
                return;
            }

            _controller.enabled = true;

            // Направление берём только по горизонтали, иначе взгляд вниз тормозит ходьбу.
            var forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            var right = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;
            var move = (forward * input.z + right * input.x).normalized * (walkSpeed * boost);

            if (_controller.isGrounded)
            {
                _verticalSpeed = -1f;
                if (Input.GetKeyDown(KeyCode.Space)) _verticalSpeed = jumpSpeed;
            }
            else
            {
                _verticalSpeed += gravity * Time.deltaTime;
            }

            move.y = _verticalSpeed;
            _controller.Move(move * Time.deltaTime);
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
                digRadius = Mathf.Clamp(digRadius + Input.mouseScrollDelta.y * 0.5f, minRadius, 20f);
            }

            if (Input.GetKeyDown(KeyCode.F)) mode = mode == MoveMode.Fly ? MoveMode.Walk : MoveMode.Fly;
            if (Input.GetKeyDown(KeyCode.G)) noclip = !noclip;
            if (Input.GetKeyDown(KeyCode.P)) DumpViewpoint();
            if (Input.GetKeyDown(KeyCode.T)) TeleportToSpawn();
            if (Input.GetKeyDown(KeyCode.Q)) ThrowFlare();

            if (Input.GetKeyDown(KeyCode.R) && world != null)
            {
                _spawned = false;
                _totalDugVolume = 0f;

                // Старые шашки остались бы висеть в воздухе там, где породы больше нет,
                // или оказались бы замурованы в новой.
                CaveFlare.ClearAll();

                world.Generate(Random.Range(int.MinValue, int.MaxValue));
            }

            if (world == null || !_looking) return;

            if (!diggingEnabled) return;

            if (Input.GetMouseButton(0)) Modify(true);
            else if (Input.GetMouseButton(1)) Modify(false);
        }

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
        /// Печатает в консоль всё, что нужно для воспроизведения текущего ракурса.
        /// Отдельно отвечает на главный вопрос про тёмные пятна: камера в воздухе или
        /// внутри породы. Внутри обратные грани отсекаются и виден цвет очистки камеры —
        /// выглядит как чернота, но затенением не является.
        /// </summary>
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

            var seed = world != null ? world.CurrentSeed : 0;
            var angles = transform.eulerAngles;

            Debug.Log(
                $"РАКУРС сид={seed} позиция=({position.x:0.00}, {position.y:0.00}, {position.z:0.00}) " +
                $"поворот=({angles.x:0.0}, {angles.y:0.0}, {angles.z:0.0}) | " +
                $"камера внутри породы: {(inside ? "ДА" : "нет")}, " +
                $"noclip: {(noclip ? "включён" : "выключен")}, " +
                $"режим: {(mode == MoveMode.Fly ? "полёт" : "ходьба")} | " +
                $"до поверхности по взгляду: {ahead}, ближайшая поверхность вокруг: " +
                $"{(nearest < float.MaxValue ? nearest.ToString("0.00") + " юнита" : "дальше 50 юнитов")}");
        }

        private void TeleportToSpawn()
        {
            if (world == null || !world.TryGetSpawnPoint(out var spawn)) return;

            var wasEnabled = _controller.enabled;
            _controller.enabled = false;

            transform.position = spawn;

            _controller.enabled = wasEnabled;
            _verticalSpeed = 0f;
            _spawned = true;
        }

        private void OnGUI()
        {
            if (!showOverlay) return;

            var style = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };

            GUILayout.BeginArea(new Rect(10, 10, 420, 260), GUI.skin.box);

            if (world != null && world.IsGenerating)
            {
                GUILayout.Label($"<b>Генерация: {world.Progress:P0}</b>", style);
            }
            else if (world != null)
            {
                var tris = 0;
                foreach (var chunk in world.Chunks) tris += (int)(chunk.Mesh.GetIndexCount(0) / 3);

                GUILayout.Label($"<b>Сид {world.CurrentSeed}</b>   чанков {world.Chunks.Count}   треугольников {tris:N0}", style);
                GUILayout.Label($"Режим: <b>{(mode == MoveMode.Fly ? "полёт" : "ходьба")}</b>{(noclip ? " (сквозь стены)" : "")}" +
                                (diggingEnabled ? $"   радиус кисти: <b>{digRadius:0.0}</b>" : ""), style);
                // Координаты прямо в интерфейсе: тогда любой присланный скриншот
                // самодостаточен, и не нужно ловить момент нажатия P.
                var pos = transform.position;
                var rot = transform.eulerAngles;

                GUILayout.Label($"<b>({pos.x:0.0}, {pos.y:0.0}, {pos.z:0.0})</b>  " +
                                $"поворот ({rot.x:0.0}, {rot.y:0.0})  " +
                                $"фонарь {LampOffsetText()}", style);

                if (diggingEnabled)
                {
                    GUILayout.Label($"Добыто породы, оценка сверху: {_totalDugVolume:N0} " +
                                    $"(последний удар {_lastDigVolume:N0})", style);
                }

                if (!_spawned) GUILayout.Label("<color=yellow>Нажмите T — телепорт к точке входа</color>", style);
            }
            else
            {
                GUILayout.Label("<color=red>CatacombWorld не найден на сцене</color>", style);
            }

            GUILayout.Space(6);
            GUILayout.Label(_looking
                ? (diggingEnabled ? "ЛКМ — копать    ПКМ — зарастить    колесо — радиус\n" : "") +
                  "WASD — движение    Shift — ускорение    Space/Ctrl — вверх/вниз\n" +
                  "F — полёт/ходьба    G — сквозь стены    T — к точке входа\n" +
                  $"Q — пробный свет, отладка ({CaveFlare.Live.Count} из {CaveFlare.MaxLive})    " +
                  "R — новый уровень\n" +
                  "P — записать ракурс    Esc — отпустить курсор"
                : "<color=yellow>Кликните по окну игры, чтобы захватить курсор</color>", style);

            GUILayout.EndArea();
        }
    }
}
