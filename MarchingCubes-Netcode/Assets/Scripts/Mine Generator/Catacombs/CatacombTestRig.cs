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

        [Tooltip("Толпа пауков. Пусто — найдётся на сцене сама.")]
        [SerializeField] private SpiderCrowd crowd;

        [Tooltip("Генераторы света. Пусто — возьмётся со сцены.")]
        [SerializeField] private CaveGenerators generators;

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

        [Header("Пробный взрыв")]
        /// <summary>
        /// Радиус пробного взрыва. Пять юнитов — это верхняя оценка радиуса гранаты
        /// из открытых вопросов: медиана простреливаемой линии в уровне 14-16 юнитов,
        /// и взрыв шире пяти-шести начал бы доставать до самого игрока в коридоре.
        /// </summary>
        [Tooltip("Радиус пробного взрыва на клавишу B.")]
        [SerializeField, Range(1f, 12f)] private float blastRadius = 5f;

        [Header("Интерфейс")]
        [SerializeField] private bool showOverlay = true;

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

        private void Awake()
        {
            EnsureRefs();

            var angles = transform.eulerAngles;
            _yaw = angles.y;
            _pitch = angles.x;
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
            if (world == null) world = FindFirstObjectByType<CatacombWorld>();
            if (crowd == null) crowd = FindFirstObjectByType<SpiderCrowd>();
            if (generators == null) generators = FindFirstObjectByType<CaveGenerators>();
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

            var speed = walkSpeed * boost * (web != null ? web.SpeedScale : 1f);
            var move = (forward * input.z + right * input.x).normalized * speed;

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

            // Падение гасим по скорости, а не по ускорению: разгон под тяжестью идёт и
            // в паутине, просто наружу он не выходит. Иначе после долгого падения сквозь
            // затянутую щель игрок вылетал бы снизу с накопленной скоростью.
            if (web != null && move.y < 0f) move.y *= web.FallScale;

            _controller.Move(move * Time.deltaTime);
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
                digRadius = Mathf.Clamp(digRadius + Input.mouseScrollDelta.y * 0.5f, minRadius, 20f);
            }

            if (Input.GetKeyDown(KeyCode.F)) mode = mode == MoveMode.Fly ? MoveMode.Walk : MoveMode.Fly;
            if (Input.GetKeyDown(KeyCode.G)) noclip = !noclip;
            if (Input.GetKeyDown(KeyCode.P)) DumpViewpoint();
            if (Input.GetKeyDown(KeyCode.T)) TeleportToSpawn();
            if (Input.GetKeyDown(KeyCode.Q)) ThrowFlare();
            if (Input.GetKeyDown(KeyCode.B)) TestBlast();
            if (Input.GetKeyDown(KeyCode.E)) StartGenerator();

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

        /// <summary>
        /// Пробный взрыв под прицелом: рвёт паутину и бьёт пауков.
        ///
        /// Это заготовка гранаты, а не механика: VFX и снаряда пока нет, и проверить
        /// иначе, что оба тракта поражения вообще зовутся, негде. Когда появится граната,
        /// её попадание должно сделать ровно эти два вызова — и тогда клавиша уйдёт.
        /// </summary>
        private void TestBlast()
        {
            var origin = transform.position;

            var point = Physics.Raycast(origin, transform.forward, out var hit, reach)
                ? hit.point
                : origin + transform.forward * reach;

            var torn = CaveWebs.TearAt(point, blastRadius);
            var killed = crowd != null ? crowd.DamageAt(point, blastRadius) : 0;

            Debug.Log($"ВЗРЫВ в ({point.x:0.0}, {point.y:0.0}, {point.z:0.0}) радиусом {blastRadius:0.0}: " +
                      $"паутин порвано {torn}, пауков убито {killed}");
        }

        /// <summary>
        /// Запускает генератор, если игрок рядом. Это и есть цель забега: включённый
        /// генератор освещает район навсегда и становится выходом.
        /// </summary>
        private void StartGenerator()
        {
            if (generators == null) return;

            // Shift — дожечь мгновенно. Разгорание длится полторы минуты, и проверять
            // вспышку, панику и запрет спавна, выжидая их вживую каждый раз, невозможно.
            // Отладка, а не механика: игроку эти полторы минуты и есть вся кульминация.
            if (Input.GetKey(KeyCode.LeftShift))
            {
                Debug.Log(generators.ForceFinish()
                    ? "ГЕНЕРАТОР ДОЖЖЁН принудительно (отладка)"
                    : "дожигать нечего — сначала запустите генератор");
                return;
            }

            if (generators.TryActivate(transform.position))
            {
                Debug.Log("ГЕНЕРАТОР ЗАПУЩЕН — держитесь, пока разгорается");
                return;
            }

            if (generators.TryGetNearestDark(transform.position, out var point, out var distance))
            {
                Debug.Log($"Ближайший генератор в {distance:0} юнитах " +
                          $"({point.x:0}, {point.y:0}, {point.z:0}) — подойдите вплотную");
            }
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

        /// <summary>
        /// Состояние забега одной строкой: сколько районов отвоёвано, что с текущим
        /// генератором и куда идти за следующим.
        /// </summary>
        private string GeneratorText()
        {
            var progress = $"Районов засвечено: <b>{generators.LitCount} из {generators.Count}</b>";

            if (generators.Cleared) return progress + "   <color=lime>УРОВЕНЬ ПРОЙДЕН</color>";

            if (generators.Charging > 0f)
            {
                return progress + $"   <color=orange>РАЗГОРАЕТСЯ: {generators.Charging:P0}</color>";
            }

            if (generators.TryGetNearestDark(transform.position, out _, out var distance))
            {
                return progress + $"   до ближайшего генератора <b>{distance:0}</b> юнитов";
            }

            return progress;
        }

        private void OnGUI()
        {
            if (!showOverlay) return;

            var style = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };

            GUILayout.BeginArea(new Rect(10, 10, 580, 300), GUI.skin.box);

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

                if (crowd != null) GUILayout.Label(crowd.Describe(), style);

                if (generators != null && generators.Count > 0) GUILayout.Label(GeneratorText(), style);

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
                  "E — запустить генератор    Shift+E — дожечь мгновенно (отладка)\n" +
                  $"B — пробный взрыв, радиус {blastRadius:0.0}    " +
                  "P — записать ракурс    Esc — отпустить курсор"
                : "<color=yellow>Кликните по окну игры, чтобы захватить курсор</color>", style);

            GUILayout.EndArea();
        }
    }
}
