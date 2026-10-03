#!/usr/bin/env bash
# Ставит редактор Unity той версии, что в ProjectSettings/ProjectVersion.txt, в облачный
# контейнер (Ubuntu) — без Unity Hub, прямо с download.unity3d.com.
#
#   bash Tools/CloudUnity/setup-unity.sh            # редактор (~4.1 ГБ архив, ~8 ГБ на диске)
#   bash Tools/CloudUnity/setup-unity.sh --webgl    # + модуль WebGL (~1.3 ГБ) — для сборки и замера размера
#
# Лицензия. Без неё редактор в batchmode проект не открывает («No valid Unity Editor license
# found»), и прогоны из CLAUDE.md не запустить. Компиляцию можно проверить и без лицензии —
# Tools/CloudUnity/compile_check.py. Лицензия берётся из переменных окружения (секреты среды):
#   UNITY_LICENSE                 — содержимое файла Unity_lic.ulf (Personal; GameCI-способ)
#   UNITY_SERIAL + UNITY_EMAIL + UNITY_PASSWORD — активация Plus/Pro серийным номером
# Значения никогда не печатаются и не пишутся в репозиторий.
#
# Проверено в облачной сессии: редактор ставится и запускается (`-version`), без лицензии
# batchmode честно отказывает. Ветка с лицензией не проверена — лицензии в той сессии не было.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$(cd "$HERE/../.." && pwd)"
VERSION_LINE="$(grep m_EditorVersionWithRevision "$PROJECT/ProjectSettings/ProjectVersion.txt")"
VERSION="$(sed -E 's/.*: ([^ ]+) \(([0-9a-f]+)\).*/\1/' <<<"$VERSION_LINE")"
CHANGESET="$(sed -E 's/.*: ([^ ]+) \(([0-9a-f]+)\).*/\2/' <<<"$VERSION_LINE")"
ROOT="/opt/unity-$VERSION"
CACHE="${UNITY_DOWNLOAD_CACHE:-/opt/unity-work/downloads}"
BASE="https://download.unity3d.com/download_unity/$CHANGESET"

WANT_WEBGL=0
for arg in "$@"; do
  case "$arg" in
    --webgl) WANT_WEBGL=1 ;;
    *) echo "неизвестный ключ: $arg" >&2; exit 2 ;;
  esac
done

mkdir -p "$CACHE"

fetch() {  # докачка с повтором: прокси облака иногда рвёт длинные передачи
  local url="$1" out="$2"
  for attempt in 1 2 3 4 5 6; do
    if curl -fsS -C - --retry 5 --retry-delay 3 -o "$out" "$url"; then return 0; fi
    echo "  повтор $attempt: $url" >&2
    sleep $((attempt * 2))
  done
  return 1
}

if [[ -x "$ROOT/Editor/Unity" ]]; then
  echo "редактор $VERSION уже стоит: $ROOT"
else
  echo "качаю редактор $VERSION ($CHANGESET)…"
  archive="$CACHE/Unity-$VERSION.tar.xz"
  fetch "$BASE/LinuxEditorInstaller/Unity-$VERSION.tar.xz" "$archive"
  mkdir -p "$ROOT.tmp"
  tar -xJf "$archive" -C "$ROOT.tmp"
  mv "$ROOT.tmp" "$ROOT"
  rm -f "$archive"
fi
ln -sfn "$ROOT" /opt/unity

if [[ $WANT_WEBGL -eq 1 ]]; then
  if [[ -d "$ROOT/Editor/Data/PlaybackEngines/WebGLSupport" ]]; then
    echo "модуль WebGL уже стоит"
  else
    echo "качаю модуль WebGL…"
    archive="$CACHE/WebGL-$VERSION.tar.xz"
    fetch "$BASE/LinuxEditorTargetInstaller/UnitySetup-WebGL-Support-for-Editor-$VERSION.tar.xz" "$archive"
    # архив модуля разложен от корня установки: Editor/Data/PlaybackEngines/WebGLSupport/…
    tar -xJf "$archive" -C "$ROOT"
    rm -f "$archive"
    [[ -d "$ROOT/Editor/Data/PlaybackEngines/WebGLSupport" ]] || { echo "модуль WebGL не лёг на место" >&2; exit 1; }
  fi
fi

"$ROOT/Editor/Unity" -batchmode -nographics -quit -logFile /dev/null -version >/dev/null 2>&1 || true
echo "редактор: $("$ROOT/Editor/Unity" -version 2>/dev/null | tail -1)"

# --- лицензия -------------------------------------------------------------------------
ULF_DIR="$HOME/.local/share/unity3d/Unity"
if [[ -n "${UNITY_LICENSE:-}" ]]; then
  mkdir -p "$ULF_DIR"
  printf '%s' "$UNITY_LICENSE" > "$ULF_DIR/Unity_lic.ulf"
  chmod 600 "$ULF_DIR/Unity_lic.ulf"
  echo "лицензия: записан Unity_lic.ulf из UNITY_LICENSE"
elif [[ -n "${UNITY_SERIAL:-}" && -n "${UNITY_EMAIL:-}" && -n "${UNITY_PASSWORD:-}" ]]; then
  echo "лицензия: активация серийным номером…"
  "$ROOT/Editor/Unity" -batchmode -nographics -quit -logFile /opt/unity-work/activate.log \
    -serial "$UNITY_SERIAL" -username "$UNITY_EMAIL" -password "$UNITY_PASSWORD" || true
  grep -qiE 'activat(ed|ion) (succeeded|successful)|license.*(valid|updated)' /opt/unity-work/activate.log \
    && echo "лицензия активирована" || echo "активация не подтверждена — см. /opt/unity-work/activate.log"
else
  echo "лицензии нет (UNITY_LICENSE или UNITY_SERIAL+UNITY_EMAIL+UNITY_PASSWORD не заданы)."
  echo "Доступна только проверка компиляции: python3 Tools/CloudUnity/compile_check.py"
fi
