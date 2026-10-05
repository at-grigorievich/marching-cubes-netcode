using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Локализация: таблица Resources/Localization/strings.csv (ключ;ru;en;tr) → словарь при
    /// старте, <c>Loc.T("hud.swarm")</c>. Пакет com.unity.localization не взят намеренно: тяжёлый
    /// для трёх языков и сотен строк (план M5.4).
    ///
    /// Все строки интерфейса — через ключи с первого дня (M1.9): переделывать готовый интерфейс
    /// под ключи дороже. Нет ключа в языке — английский, нет и там — сам ключ (видно в UiCheck).
    ///
    /// Разбор чисел и регистр — без культуры системы (InvariantCulture): в турецкой локали
    /// <c>ToUpper</c> делает из «i» «İ», и разбор строк ломается.
    /// </summary>
    public static class Loc
    {
        public const string TablePath = "Localization/strings";

        public static readonly string[] Languages = { "ru", "en", "tr" };

        private static Dictionary<string, string[]> _table;
        private static int _language = 1;

        // Язык по системе в редакторе — один раз, до первого явного выбора. Раньше это делала
        // каждая загрузка таблицы, и перезагрузка после выбора «en» возвращала русский.
        private static bool _languageChosen;

        /// <summary>Сменился язык — перерисовать подписи.</summary>
        public static event Action Changed;

        public static string Language => Languages[_language];

        public static int KeyCount
        {
            get
            {
                Load();
                return _table.Count;
            }
        }

        public static IEnumerable<string> Keys
        {
            get
            {
                Load();
                return _table.Keys;
            }
        }

        /// <summary>
        /// Выбрать язык: из SDK площадки, из профиля, если игрок выбрал сам, иначе английский.
        /// Русский — для русской системы в редакторе, чтобы разработка шла на родном языке.
        /// </summary>
        public static void SetLanguage(string code)
        {
            Load();

            var index = Array.IndexOf(Languages, (code ?? "").ToLowerInvariant());
            if (index < 0) index = 1;

            _languageChosen = true;

            if (index == _language) return;

            _language = index;
            Changed?.Invoke();
        }

        /// <summary>Язык по системе — до того, как площадка или профиль скажут своё.</summary>
        public static string SystemCode() => Application.systemLanguage switch
        {
            UnityEngine.SystemLanguage.Russian => "ru",
            UnityEngine.SystemLanguage.Ukrainian => "ru",
            UnityEngine.SystemLanguage.Belarusian => "ru",
            UnityEngine.SystemLanguage.Turkish => "tr",
            _ => "en"
        };

        public static bool Has(string key)
        {
            Load();
            return _table.ContainsKey(key);
        }

        /// <summary>Строка по ключу на текущем языке.</summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";

            Load();

            if (!_table.TryGetValue(key, out var row)) return key;

            var text = row[_language];
            if (string.IsNullOrEmpty(text)) text = row[1];
            if (string.IsNullOrEmpty(text)) text = row[0];

            return string.IsNullOrEmpty(text) ? key : text;
        }

        /// <summary>Строка по ключу с подстановкой {0}, {1}… — без культуры системы.</summary>
        public static string F(string key, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, T(key), args);

        /// <summary>Строка на заданном языке как есть, без подмены другим языком — для UiCheck.</summary>
        public static string Raw(string key, string language)
        {
            Load();

            var index = Array.IndexOf(Languages, language);
            return index >= 0 && _table.TryGetValue(key, out var row) ? row[index] : null;
        }

        /// <summary>Есть ли у ключа перевод на каждом языке — для UiCheck.</summary>
        public static bool IsComplete(string key, out string missing)
        {
            Load();
            missing = "";

            if (!_table.TryGetValue(key, out var row))
            {
                missing = "all";
                return false;
            }

            for (var i = 0; i < Languages.Length; i++)
            {
                if (string.IsNullOrEmpty(row[i])) missing += Languages[i] + " ";
            }

            return missing.Length == 0;
        }

        public static void Reload()
        {
            _table = null;
            Load();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            _table = null;
            _languageChosen = false;
            Changed = null;
        }

        private static void Load()
        {
            if (_table != null) return;

            _table = new Dictionary<string, string[]>(512, StringComparer.Ordinal);

            var asset = Resources.Load<TextAsset>(TablePath);
            if (asset == null)
            {
                Debug.LogWarning("Локализация: нет таблицы Resources/" + TablePath + ".csv");
                return;
            }

            Parse(asset.text, _table);

            if (!_languageChosen && Application.isEditor) _language = Array.IndexOf(Languages, SystemCode());
            if (_language < 0) _language = 1;
        }

        /// <summary>
        /// Строки «ключ;ru;en;tr». Точка с запятой — разделитель (в текстах бывают запятые),
        /// «\n» в тексте — перенос строки, «#» в начале — комментарий.
        /// </summary>
        public static void Parse(string text, Dictionary<string, string[]> into)
        {
            var lines = text.Split('\n');

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;

                var parts = line.Split(';');
                if (parts.Length < 2) continue;

                var key = parts[0].Trim();
                if (key.Length == 0 || key == "key") continue;

                var row = new string[Languages.Length];

                for (var i = 0; i < Languages.Length; i++)
                {
                    row[i] = i + 1 < parts.Length ? parts[i + 1].Replace("\\n", "\n").Trim() : "";
                }

                into[key] = row;
            }
        }
    }
}
