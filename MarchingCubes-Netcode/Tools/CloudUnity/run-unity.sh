#!/usr/bin/env bash
# Прогоны проекта в batchmode — то, что в CLAUDE.md делается `-executeMethod` на Windows.
# Нужна лицензия (см. setup-unity.sh). Без неё — только compile_check.py.
#
#   bash Tools/CloudUnity/run-unity.sh method MineGenerator.Catacombs.EditorTools.CatacombRunCheck.Run
#   bash Tools/CloudUnity/run-unity.sh tests            # EditMode-тесты (Test Runner), отчёт в XML
#   bash Tools/CloudUnity/run-unity.sh open             # только открыть проект (импорт, компиляция)
#
# Лог — в /opt/unity-work/logs/<время>-<что>.log, в конце печатается его хвост и ошибки.
# Код возврата — код Unity (для тестов: 0 — все прошли, 2 — есть упавшие).
#
# Первое открытие проекта импортирует ассеты — на этом проекте это долго (запечённые пауки,
# покупные паки); Library кэшируется рядом с проектом и в git не попадает (.gitignore).
# После прогона печатается `git status`: Unity может тронуть UserSettings/ и ProjectSettings/ —
# такие правки НЕ коммитить без причины.
#
# Не проверено в облачной сессии с лицензией (её там не было) — первый запуск смотреть глазами.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$(cd "$HERE/../.." && pwd)"
UNITY="${UNITY_EDITOR_ROOT:-/opt/unity}/Editor/Unity"
LOGS=/opt/unity-work/logs
mkdir -p "$LOGS"

[[ -x "$UNITY" ]] || { echo "нет редактора: $UNITY — сначала Tools/CloudUnity/setup-unity.sh" >&2; exit 2; }

mode="${1:-}"; shift || true
stamp="$(date +%Y%m%d-%H%M%S)"
args=(-batchmode -nographics -projectPath "$PROJECT" -buildTarget WebGL)

case "$mode" in
  method)
    method="${1:?нужно полное имя метода с пространством имён (грабли №18)}"
    log="$LOGS/$stamp-${method##*.}.log"
    args+=(-executeMethod "$method" -quit -logFile "$log")
    ;;
  tests)
    log="$LOGS/$stamp-tests.log"
    results="$LOGS/$stamp-editmode.xml"
    # -runTests сам закрывает редактор; -quit с ним не ставят
    args+=(-runTests -testPlatform EditMode -testResults "$results" -logFile "$log")
    ;;
  open)
    log="$LOGS/$stamp-open.log"
    args+=(-quit -logFile "$log")
    ;;
  *)
    sed -n '2,12p' "$0"; exit 2 ;;
esac

# Без дисплея: -nographics хватает для прогонов без кадров. Прогоны, снимающие кадры
# (CatacombBatchCheck), требуют графики — тогда через xvfb-run, если он установлен.
runner=()
if [[ "$mode" == method && "${UNITY_WITH_GRAPHICS:-0}" == 1 ]] && command -v xvfb-run >/dev/null; then
  args=("${args[@]/-nographics}")
  runner=(xvfb-run -a)
fi

"${runner[@]}" "$UNITY" "${args[@]}"
code=$?

if grep -q "No valid Unity Editor license found" "$log" 2>/dev/null; then
  echo "НЕТ ЛИЦЕНЗИИ — прогон не состоялся. Секрет UNITY_LICENSE и setup-unity.sh, см. Docs/Plan/verification.md" >&2
  exit 3
fi

echo "---- ошибки компиляции и исключения (до 40) ----"
grep -E "error CS[0-9]+|Exception|Assertion failed" "$log" | head -40
echo "---- хвост лога ----"
tail -n 40 "$log"
[[ "$mode" == tests ]] && echo "отчёт тестов: $results"
echo "лог: $log"
echo "---- git status (правки Unity в UserSettings/ProjectSettings без причины не коммитить) ----"
git -C "$PROJECT" status --short | head -20
exit $code
