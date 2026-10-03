#!/usr/bin/env python3
"""
Проверка компиляции скриптов проекта БЕЗ лицензии Unity.

Зачем. Редактор в batchmode без активированной лицензии не открывает проект вовсе
(«No valid Unity Editor license found»), а в облачной сессии лицензии может не быть.
Компилятор при этом лицензии не требует: Roslyn, .NET и сборки движка лежат в поставке
редактора. Скрипт повторяет то, что делает конвейер компиляции Unity:

  * берёт пакеты ровно тех версий, что в Packages/packages-lock.json, из поставки редактора
    (BuiltInPackages и офлайн-архивы Editor/*.tgz), докачивая недостающее с packages.unity.com;
  * разбирает .asmdef/.asmref пакетов и Assets (ссылки по имени и по GUID, defineConstraints,
    versionDefines, includePlatforms, overrideReferences, allowUnsafeCode);
  * собирает их по уровням зависимостей параллельно, затем Assembly-CSharp(-Editor);
  * второй проход — «как в выпускной сборке WebGL»: рантайм без UNITY_EDITOR и без ссылок
    на UnityEditor, против плеерных сборок движка из модуля WebGL. Он ловит грабли №6 и №68
    (UnityEditor в рантайм-скрипте ломает сборку плеера, а в редакторе компилируется).

Две особенности 6000.4, которые здесь повторены вручную, — см. PACKAGE_SHIMS и «uGUI видна
всем» в compile_pass. Сборки пакетов кэшируются в $UNITY_COMPILE_CACHE: повторный прогон
пересобирает только проект.

Чего он НЕ делает: не запускает ни одной строки кода (прогоны и тесты — только с лицензией,
см. Docs/Plan/verification.md), не делает ILPostProcessing Burst и не компилирует шейдеры.
Набор define — приближение к редакторному: если пакет не собирается из-за define, это
повод уточнить список ниже, а не признак ошибки в проекте. Ошибки в сборках ПАКЕТОВ
печатаются отдельно и код возврата не портят; портят — ошибки в сборках проекта.

Запуск:  python3 Tools/CloudUnity/compile_check.py [--editor /opt/unity] [--player-only|--editor-only]
Код возврата 0 — сборки проекта компилируются в обоих проходах.
"""

import argparse
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import urllib.request

# Вставки совместимости для ПАКЕТНЫХ сборок, которые редактор собирает, а голый Roslyn — нет.
# ShaderGraph Editor в 6000.4 пишет голое «GUID» в пространстве UnityEditor.*, а тип теперь
# только UnityEngine.GUID (UnityEditor.GUID в сборках редактора нет — проверено по метаданным).
# Вставка нужна, чтобы собрались ShaderGraph и URP Editor, от которого зависит CatacombUrpSetup.
# Проектные сборки вставок НЕ получают никогда: их ошибки должны быть настоящими.
PACKAGE_SHIMS = {
    "Unity.ShaderGraph.Editor": "global using GUID = global::UnityEngine.GUID;",
}

PROJECT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CACHE = os.environ.get("UNITY_COMPILE_CACHE", "/opt/unity-work/compile-cache")

# --------------------------------------------------------------------------- define

def unity_version_defines(version):
    """UNITY_6000_4_2, UNITY_6000_4, UNITY_6000 и вся лестница *_OR_NEWER, как у редактора."""
    major, minor, patch = [int(x) for x in re.match(r"(\d+)\.(\d+)\.(\d+)", version).groups()]
    out = [f"UNITY_{major}_{minor}_{patch}", f"UNITY_{major}_{minor}", f"UNITY_{major}"]
    ladder = [(5, m) for m in range(3, 7)] + [(2017, m) for m in range(1, 5)]
    ladder += [(y, m) for y in range(2018, 2024) for m in range(1, 5)]
    ladder += [(6000, m) for m in range(0, minor + 1)]
    for y, m in ladder:
        if (y, m) <= (major, minor):
            out.append(f"UNITY_{y}_{m}_OR_NEWER")
    return out


COMMON_DEFINES = [
    # общие для редактора и плеера; разрядность, отладка и проверки коллекций — в EDITOR/PLAYER ниже
    "ENABLE_AUDIO", "ENABLE_CACHING", "ENABLE_CLOTH", "ENABLE_MICROPHONE", "ENABLE_MULTIPLE_DISPLAYS",
    "ENABLE_PHYSICS", "ENABLE_TEXTURE_STREAMING", "ENABLE_LZMA", "ENABLE_UNITYEVENTS", "ENABLE_VR",
    "ENABLE_WEBCAM", "ENABLE_UNITYWEBREQUEST", "ENABLE_WWW", "ENABLE_CLOUD_SERVICES",
    "ENABLE_CLOUD_SERVICES_ADS", "ENABLE_CLOUD_SERVICES_USE_WEBREQUEST", "ENABLE_UNITY_CONSENT",
    "ENABLE_UNITY_CLOUD_IDENTIFIERS", "ENABLE_CLOUD_SERVICES_CRASH_REPORTING", "ENABLE_CLOUD_SERVICES_PURCHASING",
    "ENABLE_CLOUD_SERVICES_ANALYTICS", "ENABLE_CLOUD_SERVICES_BUILD", "ENABLE_EDITOR_GAME_SERVICES",
    "ENABLE_UNITY_GAME_SERVICES_ANALYTICS_SUPPORT", "ENABLE_CLOUD_LICENSE", "ENABLE_EDITOR_HUB_LICENSE",
    "ENABLE_WEBSOCKET_CLIENT", "ENABLE_GENERATE_NATIVE_PLUGINS_FOR_ASSEMBLIES_API", "ENABLE_DIRECTOR_AUDIO",
    "ENABLE_DIRECTOR_TEXTURE", "ENABLE_MANAGED_JOBS", "ENABLE_MANAGED_TRANSFORM_JOBS",
    "ENABLE_MANAGED_ANIMATION_JOBS", "ENABLE_MANAGED_AUDIO_JOBS", "ENABLE_MANAGED_UNITYTLS",
    "INCLUDE_DYNAMIC_GI", "ENABLE_SCRIPTING_GC_WBARRIERS",
    "RENDER_SOFTWARE_CURSOR", "ENABLE_MARSHALLING_TESTS", "ENABLE_VIDEO", "ENABLE_NAVIGATION_OFFMESHLINK_TO_NAVMESHLINK",
    "ENABLE_ACCELERATOR_CLIENT_DEBUGGING", "TEXTCORE_1_0_OR_NEWER", "ENABLE_BURST_AOT", "UNITY_TEAM_LICENSE", "ENABLE_CUSTOM_RENDER_TEXTURE", "ENABLE_DIRECTOR",
    "ENABLE_LOCALIZATION", "ENABLE_SPRITES", "ENABLE_TERRAIN", "ENABLE_TILEMAP", "ENABLE_TIMELINE",
    "ENABLE_LEGACY_INPUT_MANAGER", "CSHARP_7_OR_LATER", "CSHARP_7_3_OR_NEWER", "UNITY_POST_PROCESSING_STACK_V2_OFF",
    "NET_STANDARD_2_0", "NET_STANDARD", "NET_STANDARD_2_1",
    "NETSTANDARD", "NETSTANDARD2_1",
]

EDITOR_DEFINES = ["UNITY_64", "PLATFORM_ARCH_64", "UNITY_ASSERTIONS", "ENABLE_PROFILER", "DEBUG", "TRACE",
                  "ENABLE_MONO", "PLATFORM_SUPPORTS_MONO", "UNITY_EDITOR", "UNITY_EDITOR_64", "UNITY_EDITOR_LINUX", "ENABLE_UNITY_COLLECTIONS_CHECKS",
                  "UNITY_STANDALONE_LINUX", "UNITY_STANDALONE", "ENABLE_RUNTIME_GI", "ENABLE_GAMECENTER",
                  "ENABLE_NETWORK", "ENABLE_CRUNCH_TEXTURE_COMPRESSION", "ENABLE_OUT_OF_PROCESS_CRASH_HANDLER",
                  "ENABLE_CLUSTER_SYNC", "ENABLE_CLUSTERINPUT", "ENABLE_SPATIALTRACKING",
                  "ENABLE_MODULAR_UNITYENGINE_ASSEMBLIES"]

# Проход «как в выпускной сборке WebGL»: без UNITY_EDITOR и без того, что есть только в редакторе
# и development-сборке (ENABLE_UNITY_COLLECTIONS_CHECKS — AtomicSafetyHandle, DEBUG, профайлер).
# WebGL — wasm32 и IL2CPP: ни UNITY_64, ни ENABLE_MONO.
PLAYER_DEFINES = ["UNITY_WEBGL", "UNITY_WEBGL_API", "ENABLE_MODULAR_UNITYENGINE_ASSEMBLIES", "ENABLE_IL2CPP",
                  "PLATFORM_ARCH_32", "UNITY_IL2CPP"]

# --------------------------------------------------------------------------- пакеты

def load_json(path):
    with open(path, encoding="utf-8-sig") as f:
        text = f.read()
    try:
        return json.loads(text, strict=False)
    except json.JSONDecodeError:
        pass
    # asmdef бывают с комментариями и висячими запятыми; комментарий — только вне строк,
    # иначе «https://…» в описании пакета отрежется вместе с концом строки
    text = re.sub(r'("(?:\\.|[^"\\])*")|//[^\n]*|/\*.*?\*/', lambda m: m.group(1) or "", text, flags=re.S)
    text = re.sub(r",(\s*[}\]])", r"\1", text)
    return json.loads(text, strict=False)


def semver_key(v):
    nums = re.findall(r"\d+", v.split("-")[0])
    nums = [int(n) for n in nums] + [0, 0, 0]
    pre = 0 if "-" in v else 1
    return (nums[0], nums[1], nums[2], pre)


def version_matches(version, expr):
    """Диапазоны versionDefines: «1.2.3» (≥), «[1.0,2.0)», «(,3.0]», «[1.0]»."""
    expr = expr.strip()
    if not expr:
        return True
    if expr[0] not in "[(":
        return semver_key(version) >= semver_key(expr)
    lo_inc, hi_inc = expr[0] == "[", expr[-1] == "]"
    body = expr[1:-1]
    if "," not in body:
        return semver_key(version) == semver_key(body)
    lo, hi = [p.strip() for p in body.split(",", 1)]
    v = semver_key(version)
    if lo:
        if v < semver_key(lo) or (v == semver_key(lo) and not lo_inc):
            return False
    if hi:
        if v > semver_key(hi) or (v == semver_key(hi) and not hi_inc):
            return False
    return True


class Packages:
    def __init__(self, editor_data):
        self.editor_data = editor_data
        self.builtin = os.path.join(editor_data, "Resources", "PackageManager", "BuiltInPackages")
        self.offline = os.path.join(editor_data, "Resources", "PackageManager", "Editor")
        self.versions = {}   # имя → версия
        self.paths = {}      # имя → папка с исходниками
        self.missing = []

    def locate(self, name, version):
        if version == "default":
            version = self.default_version(name)
            if version is None:
                return None, "default"
        builtin = os.path.join(self.builtin, name)
        if os.path.isfile(os.path.join(builtin, "package.json")):
            return builtin, load_json(os.path.join(builtin, "package.json")).get("version", version)

        target = os.path.join(CACHE, "packages", f"{name}@{version}")
        if os.path.isfile(os.path.join(target, "package.json")):
            return target, version

        archive = os.path.join(self.offline, f"{name}-{version}.tgz")
        if not os.path.isfile(archive):
            archive = self.download(name, version)
        if archive is None:
            return None, version

        tmp = target + ".tmp"
        shutil.rmtree(tmp, ignore_errors=True)
        os.makedirs(tmp)
        with tarfile.open(archive) as tar:
            tar.extractall(tmp)
        os.replace(os.path.join(tmp, "package"), target)
        shutil.rmtree(tmp, ignore_errors=True)
        return target, version

    def default_version(self, name):
        """«default» у feature-пакетов — версия, рекомендованная редактором: офлайн-архив или последняя в реестре."""
        offline = sorted(glob.glob(os.path.join(self.offline, f"{name}-*.tgz")),
                         key=lambda p: semver_key(p[len(os.path.join(self.offline, name)) + 1:-4]))
        if offline:
            return offline[-1][len(os.path.join(self.offline, name)) + 1:-4]
        try:
            with urllib.request.urlopen(f"https://packages.unity.com/{name}", timeout=60) as r:
                return json.load(r)["dist-tags"]["latest"]
        except Exception as e:
            print(f"  ! нет версии по умолчанию для {name}: {e}")
            return None

    def download(self, name, version):
        url = f"https://download.packages.unity.com/{name}/-/{name}-{version}.tgz"
        out = os.path.join(CACHE, "downloads", f"{name}-{version}.tgz")
        if os.path.isfile(out):
            return out
        os.makedirs(os.path.dirname(out), exist_ok=True)
        try:
            with urllib.request.urlopen(url, timeout=60) as r, open(out + ".part", "wb") as f:
                shutil.copyfileobj(r, f)
            os.replace(out + ".part", out)
            return out
        except Exception as e:  # сеть закрыта или пакета нет в реестре
            print(f"  ! не скачать {name}@{version}: {e}")
            return None

    def resolve(self, manifest):
        # packages-lock.json — ровно те версии, что разрешил Package Manager у пользователя;
        # без него версии выводятся из манифеста и зависимостей, как это делает UPM.
        lock = os.path.join(os.path.dirname(manifest), "packages-lock.json")
        if os.path.isfile(lock):
            for name, entry in load_json(lock)["dependencies"].items():
                if name.startswith("com.unity.modules."):
                    self.versions[name] = "1.0.0"
                    continue
                path, real = self.locate(name, entry.get("version", "default"))
                if path is None:
                    self.missing.append(f"{name}@{entry.get('version')}")
                    continue
                self.paths[name] = path
                self.versions[name] = real
            return
        queue = list(load_json(manifest)["dependencies"].items())
        while queue:
            name, version = queue.pop(0)
            if name.startswith("com.unity.modules."):
                self.versions[name] = "1.0.0"
                continue
            if name in self.paths:
                # более новая версия из зависимостей побеждает, как в UPM
                if version == "default" or semver_key(version) <= semver_key(self.versions[name]):
                    continue
            path, real = self.locate(name, version)
            if path is None:
                self.missing.append(f"{name}@{version}")
                continue
            self.paths[name] = path
            self.versions[name] = real
            for dep, dep_version in load_json(os.path.join(path, "package.json")).get("dependencies", {}).items():
                queue.append((dep, dep_version))

# --------------------------------------------------------------------------- сборки

class Assembly:
    def __init__(self, name, root, data, package):
        self.name = name
        self.root = root
        self.data = data
        self.package = package
        self.sources = []
        self.guid = None
        self.editor_only = data.get("includePlatforms") == ["Editor"]
        self.local_defines = set()


def meta_guid(path):
    meta = path + ".meta"
    if not os.path.isfile(meta):
        return None
    with open(meta, encoding="utf-8", errors="ignore") as f:
        m = re.search(r"^guid:\s*([0-9a-f]+)", f.read(), re.M)
    return m.group(1) if m else None


def hidden(part):
    return part.startswith(".") or part.endswith("~") or part in ("Samples", "Documentation~")


def scan_tree(root, package):
    """asmdef/asmref внутри дерева и раздача .cs по ближайшему владельцу."""
    owners = {}  # папка → Assembly или имя сборки (для asmref)
    assemblies = {}
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if not hidden(d)]
        for f in filenames:
            p = os.path.join(dirpath, f)
            if f.endswith(".asmdef"):
                data = load_json(p)
                a = Assembly(data["name"], dirpath, data, package)
                a.guid = meta_guid(p)
                assemblies[a.name] = a
                owners[dirpath] = a
            elif f.endswith(".asmref"):
                owners[dirpath] = load_json(p).get("reference", "")
    loose = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if not hidden(d)]
        for f in filenames:
            if not f.endswith(".cs"):
                continue
            p = os.path.join(dirpath, f)
            d = dirpath
            owner = None
            while True:
                if d in owners:
                    owner = owners[d]
                    break
                if d == root or len(d) <= len(root):
                    break
                d = os.path.dirname(d)
            if owner is None:
                loose.append(p)
            elif isinstance(owner, Assembly):
                owner.sources.append(p)
            else:
                loose.append(("asmref", owner, p))
    return assemblies, loose


def evaluate_constraint(expr, defines):
    for alt in expr.split("||"):
        alt = alt.strip()
        neg = alt.startswith("!")
        sym = alt.lstrip("!").strip()
        if (sym in defines) != neg:
            return True
    return False


def is_managed(path):
    """Есть ли в PE-файле заголовок CLI. Нативные плагины (обёртки OIIO, sqlite) Unity
    ссылками не передаёт, а Roslyn на них падает с CS0009."""
    import struct
    try:
        with open(path, "rb") as f:
            data = f.read(4096)
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        if data[pe:pe + 4] != b"PE\0\0":
            return False
        opt = pe + 24
        magic = struct.unpack_from("<H", data, opt)[0]
        dirs = opt + (96 if magic == 0x10B else 112)
        rva, size = struct.unpack_from("<II", data, dirs + 14 * 8)
        return rva != 0 and size != 0
    except Exception:
        return False


def precompiled_dlls(roots):
    """Сборки-плагины пакетов и проекта: имя → (путь, явная ли ссылка, годится ли для редактора/плеера)."""
    found = {}
    for root in roots:
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if not hidden(d)]
            for f in filenames:
                if not f.lower().endswith(".dll"):
                    continue
                p = os.path.join(dirpath, f)
                if not is_managed(p):
                    continue
                meta = p + ".meta"
                text = open(meta, encoding="utf-8", errors="ignore").read() if os.path.isfile(meta) else ""
                explicit = "isExplicitlyReferenced: 1" in text
                analyzer = "RoslynAnalyzer" in text
                # грубо: плагин, у которого в meta нет ни одной включённой платформы, кроме Editor, — только редактор
                editor_only = bool(re.search(r"Editor:\s*\n\s*enabled: 1", text)) and \
                    not re.search(r"Any:\s*\n\s*enabled: 1", text) and \
                    not re.search(r"(WebGL|Standalone[A-Za-z0-9]*|Android):\s*\n\s*enabled: 1", text)
                found[f] = {"path": p, "explicit": explicit, "analyzer": analyzer, "editor_only": editor_only}
    return found

# --------------------------------------------------------------------------- компилятор

class Compiler:
    def __init__(self, editor_root):
        self.data = os.path.join(editor_root, "Editor", "Data")
        self.dotnet = os.path.join(self.data, "NetCoreRuntime", "dotnet")
        self.csc = os.path.join(self.data, "DotNetSdkRoslyn", "csc.dll")
        managed = os.path.join(self.data, "Managed", "UnityEngine")
        self.engine = sorted(glob.glob(os.path.join(managed, "UnityEngine*.dll")))
        # Плеерные сборки движка WebGL (если стоит модуль: setup-unity.sh --webgl) — у них нет
        # редакторных членов, и проход «плеер» по ним ближе к настоящей сборке, чем по редакторным.
        webgl = os.path.join(self.data, "PlaybackEngines", "WebGLSupport", "Managed")
        self.player_engine = sorted(glob.glob(os.path.join(webgl, "UnityEngine*.dll")))
        self.editor = sorted(glob.glob(os.path.join(managed, "UnityEditor*.dll")))
        self.editor.append(os.path.join(self.data, "Managed", "UnityEditor.Graphs.dll"))
        ns = os.path.join(self.data, "NetStandard")
        self.netstandard = [os.path.join(ns, "ref", "2.1.0", "netstandard.dll")]
        self.netstandard += glob.glob(os.path.join(ns, "compat", "2.1.0", "shims", "netfx", "*.dll"))
        self.netstandard += glob.glob(os.path.join(ns, "compat", "2.1.0", "shims", "netstandard", "*.dll"))
        self.netstandard += glob.glob(os.path.join(ns, "Extensions", "2.0.0", "*.dll"))
        fx = os.path.join(self.data, "UnityReferenceAssemblies", "unity-4.8-api")
        self.netfx = sorted(glob.glob(os.path.join(fx, "*.dll"))) + sorted(glob.glob(os.path.join(fx, "Facades", "*.dll")))
        gen = os.path.join(self.data, "Tools", "BuildPipeline", "Unity.SourceGenerators")
        self.generators = sorted(glob.glob(os.path.join(gen, "*.dll")))

    def run(self, name, sources, references, defines, out_dir, unsafe, editor, analyzers, framework_refs=None,
            cacheable=False):
        os.makedirs(out_dir, exist_ok=True)
        langversion = "9.0"
        if name in PACKAGE_SHIMS:
            shim = os.path.join(out_dir, name + ".shim.cs")
            with open(shim, "w", encoding="utf-8") as f:
                f.write(PACKAGE_SHIMS[name] + "\n")
            sources = list(sources) + [shim]
            langversion = "10.0"
        out = os.path.join(out_dir, name + ".dll")
        rsp = os.path.join(out_dir, name + ".rsp")
        if framework_refs is None:
            framework_refs = self.netfx if editor else self.netstandard
        engine = self.engine if (editor or not self.player_engine) else self.player_engine
        refs = list(framework_refs) + list(engine) + (self.editor if editor else []) + list(references)
        # одна сборка — одно имя файла: дубли (разные пути к одной сборке) Roslyn считает конфликтом
        seen, unique = set(), []
        for r in refs:
            base = os.path.basename(r).lower()
            if base in seen:
                continue
            seen.add(base)
            unique.append(r)
        lines = ["-nologo", "-target:library", f"-out:{out}", f"-langversion:{langversion}", "-nostdlib+", "-noconfig",
                 "-deterministic", "-debug-", "-optimize-", "-warn:0", "-nowarn:CS0436,CS1701,CS1702,CS8032",
                 "-define:" + ";".join(sorted(set(defines)))]
        if unsafe:
            lines.append("-unsafe")
        lines += [f'-reference:"{r}"' for r in unique]
        lines += [f'-analyzer:"{a}"' for a in list(self.generators) + list(analyzers)]
        lines += [f'"{s}"' for s in sources]
        text = "\n".join(lines)
        # Сборки пакетов неизменны между прогонами: та же командная строка — та же сборка.
        # Проектные (Assets) не кэшируются никогда — их исходники и есть то, что проверяем.
        if cacheable and os.path.isfile(out) and os.path.isfile(rsp):
            with open(rsp, encoding="utf-8") as f:
                if f.read() == text:
                    return out, []
        with open(rsp, "w", encoding="utf-8") as f:
            f.write(text)
        proc = subprocess.run([self.dotnet, self.csc, f"@{rsp}"], capture_output=True, text=True)
        errors = [l for l in (proc.stdout + proc.stderr).splitlines() if re.search(r"(^|: )error [A-Z]+\d+", l)]
        if proc.returncode != 0 and not errors:
            errors = [(proc.stdout + proc.stderr).strip()[:500] or f"csc завершился с кодом {proc.returncode}"]
        return out if proc.returncode == 0 else None, errors

# --------------------------------------------------------------------------- проход

def predefined_split(project):
    """Assembly-CSharp(-Editor) и firstpass по правилам Unity: папки Editor и Plugins."""
    assets = os.path.join(project, "Assets")
    asm_dirs = set()
    for dirpath, dirnames, filenames in os.walk(assets):
        dirnames[:] = [d for d in dirnames if not hidden(d)]
        if any(f.endswith(".asmdef") or f.endswith(".asmref") for f in filenames):
            asm_dirs.add(dirpath)
    groups = {"Assembly-CSharp-firstpass": [], "Assembly-CSharp-Editor-firstpass": [],
              "Assembly-CSharp": [], "Assembly-CSharp-Editor": []}
    for dirpath, dirnames, filenames in os.walk(assets):
        dirnames[:] = [d for d in dirnames if not hidden(d)]
        d = dirpath
        inside = False
        while len(d) >= len(assets):
            if d in asm_dirs:
                inside = True
                break
            d = os.path.dirname(d)
        if inside:
            continue
        rel = os.path.relpath(dirpath, assets).split(os.sep)
        is_editor = "Editor" in rel
        first = rel[0] in ("Plugins", "Standard Assets", "Pro Standard Assets")
        key = ("Assembly-CSharp-Editor" if is_editor else "Assembly-CSharp") + ("-firstpass" if first else "")
        groups[key] += [os.path.join(dirpath, f) for f in filenames if f.endswith(".cs")]
    return groups


def compile_pass(label, compiler, packages, project, player):
    print(f"\n=== проход: {label}")
    unity_version = open(os.path.join(project, "ProjectSettings", "ProjectVersion.txt")).read().split()[1]
    defines = set(COMMON_DEFINES + unity_version_defines(unity_version))
    defines |= set(PLAYER_DEFINES if player else EDITOR_DEFINES)
    settings = open(os.path.join(project, "ProjectSettings", "ProjectSettings.asset"), encoding="utf-8").read()
    m = re.search(r"scriptingDefineSymbols:\s*\n((?:\s+\w+: .*\n)+)", settings)
    if m:
        target = "WebGL" if player else "Standalone"
        for line in m.group(1).splitlines():
            k, _, v = line.strip().partition(": ")
            if k == target:
                defines |= set(s for s in v.split(";") if s)

    out_dir = os.path.join(CACHE, "out", "player" if player else "editor")

    # сборки пакетов
    assemblies = {}
    loose_refs = []
    for name, root in packages.paths.items():
        found, loose = scan_tree(root, name)
        assemblies.update(found)
        loose_refs += [x for x in loose if isinstance(x, tuple)]
    # asmdef внутри Assets (покупные пакеты) собираются тем же путём, но их ошибки — ошибки проекта
    project_found, project_loose = scan_tree(os.path.join(project, "Assets"), "<Assets>")
    assemblies.update(project_found)
    loose_refs += [x for x in project_loose if isinstance(x, tuple)]
    for _, owner, path in loose_refs:
        target = assemblies.get(owner) or next((a for a in assemblies.values() if owner == f"GUID:{a.guid}"), None)
        if target:
            target.sources.append(path)
    by_guid = {a.guid: a for a in assemblies.values() if a.guid}
    dlls = precompiled_dlls(list(packages.paths.values()) + [os.path.join(project, "Assets")])

    def ref_assembly(ref):
        if ref.startswith("GUID:"):
            return by_guid.get(ref[5:])
        return assemblies.get(ref)

    def active(a):
        if not a.sources:
            return False
        if player and a.editor_only:
            return False
        excl = a.data.get("excludePlatforms") or []
        incl = a.data.get("includePlatforms") or []
        if player and ("WebGL" in excl or (incl and "WebGL" not in incl)):
            return False
        local = set(defines)
        for vd in a.data.get("versionDefines", []):
            res = vd.get("name", "")
            ver = packages.versions.get(res)
            if res == "Unity":
                ver = re.match(r"\d+\.\d+\.\d+", unity_version).group(0)
            if ver and version_matches(ver, vd.get("expression", "")):
                local.add(vd["define"])
        a.local_defines = local
        return all(evaluate_constraint(c, local) for c in a.data.get("defineConstraints", []))

    package_errors = {}
    done = {}

    # Тестовые сборки пакетов Unity собирает, только если пакет в «testables»; здесь — никогда.
    def is_test(a):
        return "TestAssemblies" in (a.data.get("optionalUnityReferences") or []) or \
            any("UNITY_INCLUDE_TESTS" in c for c in a.data.get("defineConstraints", []))

    plan = {}
    for a in assemblies.values():
        if is_test(a) or not active(a):
            done[a.name] = None
            continue
        plan[a.name] = [d.name for d in (ref_assembly(r) for r in a.data.get("references", [])) if d is not None]

    # уровни зависимостей: сборки одного уровня независимы и собираются параллельно
    from concurrent.futures import ThreadPoolExecutor
    level_of = {}

    def level(name, stack=()):
        if name in level_of:
            return level_of[name]
        if name in stack or name not in plan:
            return -1
        deps = [level(d, stack + (name,)) for d in plan[name]]
        level_of[name] = 1 + max([x for x in deps if x >= 0], default=-1)
        return level_of[name]

    for name in plan:
        level(name)

    # uGUI и TextMeshPro (пакет com.unity.ugui) в 6000.4 видны ВСЕМ сборкам без ссылки в asmdef:
    # Core RP, TextMeshPro и демо Toony Colors используют UnityEngine.UI, не ссылаясь на него
    # (или ссылаясь по GUID, которого нет ни в одном .meta поставки), и у пользователя это собирается.
    # Поэтому сборки uGUI собираются первыми, по порядку, и подкладываются ссылкой всем остальным.
    implicit = []
    ugui_order = ["UnityEngine.UI", "Unity.InternalAPIEngineBridge.004", "Unity.TextMeshPro",
                  "UnityEditor.UI", "Unity.TextMeshPro.Editor"]
    ugui = [n for n in plan if assemblies[n].package == "com.unity.ugui"]
    ugui.sort(key=lambda n: ugui_order.index(n) if n in ugui_order else len(ugui_order))
    for name in ugui:
        a = assemblies[name]
        refs = list(implicit) + [i["path"] for i in dlls.values()
                                 if not (i["explicit"] or i["analyzer"] or (player and i["editor_only"]))]
        editor_only = (not player) and a.editor_only
        out, errors = compiler.run(name, a.sources, refs, a.local_defines, out_dir,
                                   a.data.get("allowUnsafeCode", False), not player, [],
                                   compiler.netfx if editor_only else compiler.netstandard, cacheable=True)
        done[name] = out
        if errors:
            package_errors[name] = errors
        if out:
            implicit.append(out)
    for name in ugui:
        plan.pop(name)
        level_of.pop(name, None)

    def compile_one(name):
        a = assemblies[name]
        refs = list(implicit) + [done[d] for d in plan[name] if done.get(d)]
        if a.data.get("overrideReferences"):
            refs += [dlls[d]["path"] for d in a.data.get("precompiledReferences", []) if d in dlls]
        else:
            refs += [i["path"] for i in dlls.values()
                     if not (i["explicit"] or i["analyzer"] or (player and i["editor_only"]))]
        editor_only = (not player) and a.editor_only
        return name, compiler.run(name, a.sources, refs, a.local_defines, out_dir,
                                  a.data.get("allowUnsafeCode", False), not player, [],
                                  compiler.netfx if editor_only else compiler.netstandard,
                                  cacheable=a.package != "<Assets>")

    with ThreadPoolExecutor(max_workers=os.cpu_count() or 2) as pool:
        for lv in range(0, 1 + max(level_of.values(), default=0)):
            names = [n for n, l in level_of.items() if l == lv]
            for name, (out, errors) in pool.map(compile_one, names):
                done[name] = out
                if errors:
                    package_errors[name] = errors

    built = {n: o for n, o in done.items() if o}
    in_assets = {n for n, a in assemblies.items() if a.package == "<Assets>"}
    print(f"  пакетов: {len(packages.paths)}, сборок собрано {len(built)}, с ошибками {len(package_errors)}")
    for name, errors in sorted(package_errors.items()):
        if name not in in_assets:
            print(f"  ~ пакет {name}: {len(errors)} ошибок (первая: {errors[0][:200]})")
    asset_failures = 0
    for name in sorted(in_assets):
        if name in plan:
            ok = bool(done.get(name))
            print(f"  {name} (asmdef в Assets): {'ok' if ok else 'ОШИБКИ: ' + str(len(package_errors.get(name, [])))}")
            for e in package_errors.get(name, [])[:20]:
                print("    " + e.replace(project + os.sep, ""))
            asset_failures += 0 if ok else 1

    # сборки проекта
    groups = predefined_split(project)
    auto = [o for n, o in built.items() if assemblies[n].data.get("autoReferenced", True)]
    auto_dlls = [i["path"] for i in dlls.values() if not i["explicit"] and not i["analyzer"] and not (player and i["editor_only"])]
    project_errors = asset_failures
    produced = {}
    order = ["Assembly-CSharp-firstpass", "Assembly-CSharp", "Assembly-CSharp-Editor-firstpass", "Assembly-CSharp-Editor"]
    for key in order:
        sources = groups[key]
        if not sources:
            continue
        editor = "Editor" in key
        if player and editor:
            continue
        refs = implicit + auto + auto_dlls + [produced[k] for k in produced]
        local = set(defines)
        if editor:
            # тесты в Assets/**/Editor без asmdef: Unity даёт этим сборкам NUnit и тест-раннер
            refs += [done[n] for n in ("UnityEngine.TestRunner", "UnityEditor.TestRunner") if done.get(n)]
            local.add("UNITY_INCLUDE_TESTS")
        out, errors = compiler.run(key, sources, refs, local, out_dir, True, not player, [],
                                   compiler.netfx if editor else compiler.netstandard)
        status = "ok" if out else f"ОШИБКИ: {len(errors)}"
        print(f"  {key}: {len(sources)} файлов — {status}")
        for e in errors[:40]:
            print("    " + e.replace(project + os.sep, ""))
        if out:
            produced[key] = out
        else:
            project_errors += 1
    return project_errors


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--editor", default=os.environ.get("UNITY_EDITOR_ROOT", "/opt/unity"))
    ap.add_argument("--player-only", action="store_true")
    ap.add_argument("--editor-only", action="store_true")
    args = ap.parse_args()

    compiler = Compiler(args.editor)
    if not os.path.isfile(compiler.csc):
        print(f"нет компилятора в {compiler.csc} — сначала Tools/CloudUnity/setup-unity.sh")
        return 2

    packages = Packages(compiler.data)
    packages.resolve(os.path.join(PROJECT, "Packages", "manifest.json"))
    if packages.missing:
        print("не найдены пакеты (их сборки пропущены): " + ", ".join(packages.missing))

    failures = 0
    if not args.player_only:
        failures += compile_pass("редактор", compiler, packages, PROJECT, player=False)
    if not args.editor_only:
        source = "плеерные сборки модуля WebGL" if compiler.player_engine else "редакторные сборки движка — модуль WebGL не стоит"
        failures += compile_pass(f"плеер WebGL (без UNITY_EDITOR; {source})", compiler, packages, PROJECT, player=True)

    print("\nИТОГ: " + ("сборки проекта компилируются" if failures == 0 else f"ошибок в сборках проекта: {failures}"))
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
