using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Следы взрывов на породе: воронка в поле плотности и гарь вокруг неё.
    ///
    /// Воронка — настоящая правка породы (<see cref="CatacombWorld.Dig"/>): меш чанка
    /// перестраивается по бюджету кадра, коллайдер вместе с ним, толпа переразмечает сетку
    /// навигации вокруг (<see cref="SpiderCrowd.RefreshNavigation"/>). Ради этого проект
    /// и стоит на Marching Cubes с живой плотностью.
    ///
    /// Гарь — не декаль и не текстура в чанке, а глобальный массив пятен, который читает
    /// шейдер породы (CaveTriplanar, CaveScorch). Декали URP на WebGL — лишний проход
    /// и ложатся на пауков тоже; запекать гарь в меш — значит заводить в чанке ещё одно поле
    /// и перестраивать его на каждый взрыв, даже без воронки. Пятен в шейдер уходит
    /// <see cref="ShaderSlots"/> ближайших к игроку — дальше в темноте их не видно, —
    /// а помнится до <see cref="MaxMarks"/>: вернувшись, игрок видит свои следы.
    /// </summary>
    public sealed class CaveBlastMarks : MonoBehaviour
    {
        /// <summary>Сколько пятен читает шейдер породы. Совпадает с CAVE_SCORCH_SLOTS в CaveTriplanar.</summary>
        public const int ShaderSlots = 16;

        /// <summary>Сколько пятен помнится за уровень. Старые уходят первыми.</summary>
        public const int MaxMarks = 256;

        private static readonly int ScorchId = Shader.PropertyToID("_CaveScorch");
        private static readonly int ScorchInfoId = Shader.PropertyToID("_CaveScorchInfo");
        private static readonly int ScorchCountId = Shader.PropertyToID("_CaveScorchCount");

        private CatacombWorld _world;
        private SpiderCrowd _crowd;
        private Transform _viewer;

        private float _craterRadius = 1.7f;
        private float _craterDepth = 0.7f;
        private float _scorchRadius = 3f;

        private readonly List<Mark> _marks = new List<Mark>();
        private readonly List<Vector3> _craters = new List<Vector3>();
        private readonly List<int> _order = new List<int>();

        private readonly Vector4[] _spots = new Vector4[ShaderSlots];
        private readonly Vector4[] _info = new Vector4[ShaderSlots];

        private struct Mark
        {
            public Vector3 Centre;
            public float Radius;
            public float Born;
            public float Seed;
        }

        /// <summary>Сколько секунд пятно тлеет после взрыва.</summary>
        public const float HeatSeconds = 5f;

        /// <summary>Воронок выкопано за уровень — для прогона и интерфейса.</summary>
        public int CratersDug { get; private set; }

        /// <summary>Взрывов, для которых воронка не копалась: повтор в ту же яму или камня рядом нет.</summary>
        public int CratersSkipped { get; private set; }

        public int MarkCount => _marks.Count;

        public void Bind(CatacombWorld world, SpiderCrowd crowd, Transform viewer,
                         float craterRadius, float craterDepth, float scorchRadius)
        {
            if (_world != null) _world.Generated -= Clear;

            _world = world;
            _crowd = crowd;
            _viewer = viewer;

            _craterRadius = Mathf.Max(0.5f, craterRadius);
            _craterDepth = Mathf.Clamp(craterDepth, 0f, _craterRadius);
            _scorchRadius = Mathf.Max(0.5f, scorchRadius);

            if (_world != null) _world.Generated += Clear;
        }

        /// <summary>Новый уровень — новые стены: старые следы висели бы в пустоте.</summary>
        public void Clear()
        {
            _marks.Clear();
            _craters.Clear();
            CratersDug = 0;
            CratersSkipped = 0;
            Upload();
        }

        private void OnDisable()
        {
            Shader.SetGlobalFloat(ScorchCountId, 0f);
        }

        private void OnDestroy() => Release();

        /// <summary>
        /// Отвязаться от мира и погасить гарь в шейдере. В редакторе <c>OnDestroy</c>
        /// у обычного компонента не зовётся (грабли №17), поэтому инструменты проверки
        /// зовут это сами — иначе мир держал бы подписку на уничтоженный компонент.
        /// </summary>
        public void Release()
        {
            if (_world != null) _world.Generated -= Clear;
            _world = null;

            Shader.SetGlobalFloat(ScorchCountId, 0f);
        }

        private void LateUpdate() => Upload();

        /// <summary>
        /// Взрыв в точке: воронка у ближайшего камня и пятно гари.
        ///
        /// Воронка копается у ближайшей к взрыву поверхности, а не у точки удара: граната
        /// в толпе рвётся взрывателем по близости, в воздухе над пауком, и ближайший камень
        /// бывает в полуметре-полутора. Чем выше взрыв над камнем, тем мельче яма, а выше
        /// радиуса воронки её нет вовсе — взрыв в воздухе оставляет только гарь.
        /// </summary>
        /// <returns>Выкопана ли воронка.</returns>
        public bool Blast(Vector3 point)
        {
            if (_world == null || _world.Settings == null) return false;

            if (!TryFindSurface(point, _scorchRadius, out var surface, out var normal, out var height))
                return false;

            AddScorch(point);

            var depth = _craterDepth * (1f - height / _craterRadius);
            if (depth < 0.15f) return false;

            // Сфера сидит над камнем так, чтобы уйти в него на depth: снаружи она целиком
            // в пустоте хода и ничего не меняет, в камне вырезает чашу.
            var centre = surface + normal * (_craterRadius - depth);

            // Повторный взрыв в ту же яму её не углубляет: очередь из шести гранат в одну
            // точку иначе прокопала бы колодец — сквозь пол в нижний ход или за стенку.
            // Воронка на краю старой (центры дальше 0.8 радиуса) расширяет яму вбок.
            foreach (var old in _craters)
            {
                if ((old - centre).sqrMagnitude >= _craterRadius * _craterRadius * 0.64f) continue;

                CratersSkipped++;
                return false;
            }

            if (!_world.Dig(centre, _craterRadius)) return false;

            _craters.Add(centre);
            if (_craters.Count > MaxMarks * 2) _craters.RemoveAt(0);

            CratersDug++;

            if (_crowd != null) _crowd.RefreshNavigation(centre, _craterRadius + _world.Settings.SurfaceSoftness);

            return true;
        }

        private void AddScorch(Vector3 point)
        {
            var now = Time.time;

            // Гранаты очередью в одну кучу — одно пятно, которое разгорается заново
            // и чуть растёт, а не шесть одинаковых в слотах шейдера.
            for (var i = 0; i < _marks.Count; i++)
            {
                var mark = _marks[i];
                if ((mark.Centre - point).sqrMagnitude > 0.8f) continue;

                mark.Born = now;
                mark.Radius = Mathf.Min(mark.Radius * 1.1f, _scorchRadius * 1.35f);
                _marks[i] = mark;
                return;
            }

            _marks.Add(new Mark
            {
                Centre = point,
                Radius = _scorchRadius * Random.Range(0.85f, 1.15f),
                Born = now,
                Seed = Random.value
            });

            if (_marks.Count > MaxMarks) _marks.RemoveAt(0);
        }

        /// <summary>
        /// Ближайший камень к точке: лучи по 26 направлениям по полю плотности (не физикой:
        /// грабли №1), потом нормаль градиентом плотности в точке касания.
        /// </summary>
        private bool TryFindSurface(Vector3 point, float reach, out Vector3 surface, out Vector3 normal, out float height)
        {
            surface = point;
            normal = Vector3.up;
            height = 0f;

            const float step = 0.2f;

            if (_world.IsSolid(point))
            {
                normal = Gradient(point, Vector3.up);
                return true;
            }

            var best = float.MaxValue;
            var bestDirection = Vector3.zero;

            for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
            for (var z = -1; z <= 1; z++)
            {
                if (x == 0 && y == 0 && z == 0) continue;

                var direction = new Vector3(x, y, z).normalized;

                for (var t = step; t <= Mathf.Min(reach, best); t += step)
                {
                    if (!_world.IsSolid(point + direction * t)) continue;

                    if (t < best)
                    {
                        best = t;
                        bestDirection = direction;
                    }

                    break;
                }
            }

            if (best == float.MaxValue) return false;

            // Уточнение половинным делением: шаг в пятую юнита для глубины ямы грубоват.
            var outside = best - step;
            var inside = best;

            for (var i = 0; i < 5; i++)
            {
                var mid = (outside + inside) * 0.5f;

                if (_world.IsSolid(point + bestDirection * mid)) inside = mid;
                else outside = mid;
            }

            height = outside;
            surface = point + bestDirection * outside;
            normal = Gradient(surface, -bestDirection);

            return true;
        }

        /// <summary>Нормаль поверхности — градиент плотности: он смотрит в пустоту, то есть от камня.</summary>
        private Vector3 Gradient(Vector3 at, Vector3 fallback)
        {
            const float h = 0.5f;

            float Sample(Vector3 p) => _world.TrySampleDensity(p, out var d) ? d : 0f;

            var gradient = new Vector3(
                Sample(at + Vector3.right * h) - Sample(at - Vector3.right * h),
                Sample(at + Vector3.up * h) - Sample(at - Vector3.up * h),
                Sample(at + Vector3.forward * h) - Sample(at - Vector3.forward * h));

            return gradient.sqrMagnitude > 1e-8f ? gradient.normalized : fallback;
        }

        /// <summary>
        /// Отдаёт шейдеру породы ближайшие к игроку пятна. Массивы всегда полной длины:
        /// Unity запоминает длину глобального массива при первой записи.
        /// </summary>
        private void Upload()
        {
            var count = 0;

            if (_marks.Count > 0)
            {
                var eye = _viewer != null ? _viewer.position : Vector3.zero;
                _eye = eye;

                _order.Clear();
                for (var i = 0; i < _marks.Count; i++) _order.Add(i);

                _byDistance ??= CompareByDistance;
                _order.Sort(_byDistance);

                var now = Time.time;

                for (var i = 0; i < _order.Count && count < ShaderSlots; i++)
                {
                    var mark = _marks[_order[i]];

                    // Дальше семидесяти юнитов пятно не разглядеть: туман и темнота.
                    if ((mark.Centre - eye).sqrMagnitude > 70f * 70f) break;

                    var heat = Mathf.Clamp01(1f - (now - mark.Born) / HeatSeconds);

                    _spots[count] = new Vector4(mark.Centre.x, mark.Centre.y, mark.Centre.z, mark.Radius);
                    _info[count] = new Vector4(heat * heat, mark.Seed, 0f, 0f);
                    count++;
                }
            }

            for (var i = count; i < ShaderSlots; i++)
            {
                _spots[i] = new Vector4(0f, -10000f, 0f, 0.001f);
                _info[i] = Vector4.zero;
            }

            Shader.SetGlobalVectorArray(ScorchId, _spots);
            Shader.SetGlobalVectorArray(ScorchInfoId, _info);
            Shader.SetGlobalFloat(ScorchCountId, count);
        }

        // Сортировка каждый кадр: делегат заведён один раз, чтобы не мусорить замыканием.
        private Vector3 _eye;
        private System.Comparison<int> _byDistance;

        private int CompareByDistance(int a, int b) =>
            (_marks[a].Centre - _eye).sqrMagnitude.CompareTo((_marks[b].Centre - _eye).sqrMagnitude);
    }
}
