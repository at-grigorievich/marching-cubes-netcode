using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>Куда пишется профиль: локально или в облако площадки (M5).</summary>
    public interface ISaveBackend
    {
        string Name { get; }

        /// <summary>Облачное: при загрузке сливается с локальным.</summary>
        bool IsCloud { get; }

        bool TryLoad(out string json);

        void Save(string json);
    }

    /// <summary>
    /// Локальное сохранение — PlayerPrefs. На WebGL это IndexedDB, и запись туда доходит только
    /// после <c>PlayerPrefs.Save()</c>: без него закрытая вкладка теряет последнее.
    /// </summary>
    public sealed class LocalSaveBackend : ISaveBackend
    {
        public const string Key = "catacombs.profile";

        public string Name => "local";
        public bool IsCloud => false;

        public bool TryLoad(out string json)
        {
            json = Read();
            return !string.IsNullOrEmpty(json);
        }

        public void Save(string json) => Write(json);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string Read() => PlayerPrefs.GetString(Key, "");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Write(string json)
        {
            PlayerPrefs.SetString(Key, json);
            PlayerPrefs.Save();
        }
    }

    /// <summary>
    /// Облако площадки (Yandex <c>player.setData</c>, CrazyGames Data SDK — M5). Запись через площадку;
    /// чтение у площадок асинхронное, поэтому облачный профиль подмешивается позже
    /// (<see cref="SaveService.MergeCloud"/>), а при загрузке берётся локальный.
    /// </summary>
    public sealed class PlatformCloudBackend : ISaveBackend
    {
        public string Name => "cloud";
        public bool IsCloud => true;

        public bool TryLoad(out string json)
        {
            json = null;
            return false;
        }

        public void Save(string json) => Platform.Current.CloudSave(json);
    }

    /// <summary>Сохранение в памяти — для тестов и прогонов.</summary>
    public sealed class MemorySaveBackend : ISaveBackend
    {
        public string Json;
        public int Writes;

        public MemorySaveBackend(string name = "memory", bool cloud = false)
        {
            Name = name;
            IsCloud = cloud;
        }

        public string Name { get; }
        public bool IsCloud { get; }

        public bool TryLoad(out string json)
        {
            json = Json;
            return !string.IsNullOrEmpty(json);
        }

        public void Save(string json)
        {
            Json = json;
            Writes++;
        }
    }

    /// <summary>
    /// Сохранения (M4.2): локальное и облачное, слияние, миграции, частота.
    ///
    /// **Слияние** — по <see cref="PlayerProfile.SaveCounter"/>: он растёт с каждым сохранением,
    /// и больший — новее. При равенстве — больший заработанный за всё время хитин: купленное
    /// не теряется никогда.
    ///
    /// **Частота**: не в каждом кадре и не по таймеру — у облака площадок лимиты. Грязный флаг,
    /// сброс не чаще раза в 5 с; важные точки (покупка, контрольная точка, итоги) — сразу.
    ///
    /// JSON — отдельными методами: вызов в движок в теле метода делает его непроверяемым вне
    /// Unity целиком (грабли №69), а слияние и миграции проверяются тестами логики.
    /// </summary>
    public sealed class SaveService
    {
        public const float MinInterval = 5f;

        private readonly ISaveBackend _local;
        private readonly ISaveBackend _cloud;

        private bool _dirty;
        private float _sinceSave = MinInterval;

        public PlayerProfile Profile { get; private set; }

        /// <summary>Сколько раз профиль записан — для прогона.</summary>
        public int Saves { get; private set; }

        /// <summary>Откуда взят профиль при загрузке: «new», «local», «cloud».</summary>
        public string LoadedFrom { get; private set; } = "new";

        public Func<string, PlayerProfile> FromJson = DefaultFromJson;
        public Func<PlayerProfile, string> ToJson = DefaultToJson;

        public SaveService(ISaveBackend local, ISaveBackend cloud = null)
        {
            _local = local;
            _cloud = cloud;
        }

        /// <summary>Загрузить: локальное и облачное, слить, мигрировать. Нет ни того ни другого — новый.</summary>
        public PlayerProfile Load(long now)
        {
            var local = Read(_local);
            var cloud = Read(_cloud);

            var merged = Merge(local, cloud);

            LoadedFrom = merged == null ? "new" : merged == cloud ? "cloud" : "local";
            Profile = merged ?? PlayerProfile.CreateNew(now);

            ProfileMigrations.Migrate(Profile);
            return Profile;
        }

        private PlayerProfile Read(ISaveBackend backend)
        {
            if (backend == null || !backend.TryLoad(out var json)) return null;

            try
            {
                return FromJson(json);
            }
            catch (Exception)
            {
                // Битое сохранение не должно ронять игру — берётся другое или новый профиль.
                return null;
            }
        }

        /// <summary>
        /// Облачный профиль пришёл (асинхронно): если он новее нынешнего — он и становится профилем.
        /// Возвращает, сменился ли профиль.
        /// </summary>
        public bool MergeCloud(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;

            PlayerProfile cloud;

            try
            {
                cloud = FromJson(json);
            }
            catch (Exception)
            {
                return false;
            }

            if (cloud == null || Merge(Profile, cloud) != cloud || cloud == Profile) return false;
            if (Profile != null && cloud.SaveCounter == Profile.SaveCounter && cloud.ChitinEarnedTotal == Profile.ChitinEarnedTotal) return false;

            ProfileMigrations.Migrate(cloud);
            Profile = cloud;
            LoadedFrom = "cloud";
            return true;
        }

        /// <summary>Какой из двух профилей новее: больший счётчик, при равенстве — больше заработано.</summary>
        public static PlayerProfile Merge(PlayerProfile a, PlayerProfile b)
        {
            if (a == null) return b;
            if (b == null) return a;

            if (a.SaveCounter != b.SaveCounter) return a.SaveCounter > b.SaveCounter ? a : b;

            return a.ChitinEarnedTotal >= b.ChitinEarnedTotal ? a : b;
        }

        /// <summary>Отметить, что профиль изменился, — сохранится на ближайшем шаге, не чаще раза в 5 с.</summary>
        public void MarkDirty() => _dirty = true;

        public void Tick(float unscaledDeltaTime)
        {
            _sinceSave += unscaledDeltaTime;

            if (_dirty && _sinceSave >= MinInterval) Flush();
        }

        /// <summary>Сохранить сейчас — покупка, контрольная точка, итоги.</summary>
        public void Flush()
        {
            if (Profile == null) return;

            Profile.SaveCounter++;

            var json = ToJson(Profile);

            _local?.Save(json);
            _cloud?.Save(json);

            _dirty = false;
            _sinceSave = 0f;
            Saves++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static PlayerProfile DefaultFromJson(string json) => JsonUtility.FromJson<PlayerProfile>(json);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string DefaultToJson(PlayerProfile profile) => JsonUtility.ToJson(profile);
    }
}
