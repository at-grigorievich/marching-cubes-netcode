#!/usr/bin/env python3
"""
EditMode-тесты чистой логики БЕЗ лицензии Unity.

Собирает проект (compile_check.py, проход редактора), затем маленький раннер
(LogicTestRunner.cs) Roslyn'ом из поставки редактора и гоняет им тесты из
Assembly-CSharp-Editor.dll на .NET из той же поставки.

Что проверяется честно: чистые классы — кривая давления, часы, единицы, статы, цены,
кривая опыта, миграции сохранений. Что нет: всё, что лезет в движок (объекты сцены,
физика, рендер, extern-вызовы). Такие тесты раннер помечает «нужен Unity» и не считает
провалом; гонять их — Test Runner'ом через run-unity.sh tests, где есть лицензия.

Поэтому чистую логику и пишут обычными C#-классами без MonoBehaviour (Docs/Plan/M0, M0.8).

Запуск:  python3 Tools/CloudUnity/run_logic_tests.py [--filter ИмяТеста] [--no-compile]
"""

import argparse
import glob
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, "..", ".."))
CACHE = os.environ.get("UNITY_COMPILE_CACHE", "/opt/unity-work/compile-cache")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--editor", default=os.environ.get("UNITY_EDITOR_ROOT", "/opt/unity"))
    ap.add_argument("--filter")
    ap.add_argument("--no-compile", action="store_true", help="не пересобирать проект")
    args = ap.parse_args()

    data = os.path.join(args.editor, "Editor", "Data")
    dotnet = os.path.join(data, "NetCoreRuntime", "dotnet")
    csc = os.path.join(data, "DotNetSdkRoslyn", "csc.dll")
    out = os.path.join(CACHE, "out", "editor")

    if not args.no_compile:
        code = subprocess.call([sys.executable, os.path.join(HERE, "compile_check.py"), "--editor-only"],
                               stdout=subprocess.DEVNULL)
        if code != 0:
            print("проект не компилируется — сначала compile_check.py")
            return code

    target = os.path.join(out, "Assembly-CSharp-Editor.dll")
    if not os.path.isfile(target):
        print(f"нет {target} — запустите compile_check.py")
        return 2

    # Раннер собирается против самого .NET поставки (сборки реализации годятся как ссылки).
    runtime = sorted(glob.glob(os.path.join(data, "NetCoreRuntime", "shared", "Microsoft.NETCore.App", "*")))[-1]
    version = os.path.basename(runtime)
    runner_dir = os.path.join(CACHE, "logic-runner")
    os.makedirs(runner_dir, exist_ok=True)
    runner = os.path.join(runner_dir, "LogicTestRunner.dll")
    source = os.path.join(HERE, "LogicTestRunner.cs")

    if not os.path.isfile(runner) or os.path.getmtime(runner) < os.path.getmtime(source):
        refs = [f"-reference:{p}" for p in glob.glob(os.path.join(runtime, "*.dll"))
                if not os.path.basename(p).startswith("Microsoft.VisualBasic")]
        cmd = [dotnet, csc, "-nologo", "-target:exe", "-nostdlib+", "-noconfig", f"-out:{runner}",
               "-langversion:latest"] + refs + [source]
        proc = subprocess.run(cmd, capture_output=True, text=True)
        if proc.returncode != 0:
            print(proc.stdout + proc.stderr)
            return 2
        with open(os.path.join(runner_dir, "LogicTestRunner.runtimeconfig.json"), "w") as f:
            f.write('{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"%s"},'
                    '"rollForward":"LatestMajor"}}' % version)

    managed = os.path.join(data, "Managed", "UnityEngine")
    nunit = glob.glob(os.path.join(data, "Resources", "PackageManager", "BuiltInPackages",
                                   "com.unity.ext.nunit", "**", "nunit.framework.dll"), recursive=True)
    search = [out, managed, os.path.join(data, "Managed")] + [os.path.dirname(p) for p in nunit]

    cmd = [dotnet, runner, target] + search
    if args.filter:
        cmd += ["--filter", args.filter]
    return subprocess.call(cmd)


if __name__ == "__main__":
    sys.exit(main())
