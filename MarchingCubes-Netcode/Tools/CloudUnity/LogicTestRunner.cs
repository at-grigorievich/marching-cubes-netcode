// Раннер EditMode-тестов ЧИСТОЙ ЛОГИКИ вне Unity — для облачной сессии без лицензии.
//
// Загружает Assembly-CSharp-Editor.dll, собранную compile_check.py, находит методы с [Test]
// и [TestCase] (NUnit) и вызывает их отражением, с [SetUp]/[TearDown]. Движка нет: тест,
// который трогает объекты сцены, рендер, физику или extern-вызовы Unity, упадёт здесь
// с исключением движка — это не провал логики, такой тест помечается «нужен Unity»
// и гоняется Test Runner'ом (run-unity.sh tests) там, где есть лицензия.
//
// Собирается и запускается из run_logic_tests.py; руками не нужен.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

internal static class LogicTestRunner
{
    private static readonly List<string> SearchDirs = new List<string>();

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: LogicTestRunner <assembly.dll> <searchDir>... [--filter text]");
            return 2;
        }

        string filter = null;
        var dirs = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
            else dirs.Add(args[i]);
        }

        SearchDirs.Add(Path.GetDirectoryName(Path.GetFullPath(args[0])));
        SearchDirs.AddRange(dirs);

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var dir in SearchDirs)
            {
                var path = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }

            return null;
        };

        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.Where(t => t != null).ToArray();
        }

        int passed = 0, failed = 0, needUnity = 0;

        foreach (var type in types.OrderBy(t => t.FullName))
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var tests = methods.Where(m => Has(m, "TestAttribute") || Has(m, "TestCaseAttribute")).ToList();
            if (tests.Count == 0) continue;

            var setUp = methods.FirstOrDefault(m => Has(m, "SetUpAttribute"));
            var tearDown = methods.FirstOrDefault(m => Has(m, "TearDownAttribute"));

            foreach (var test in tests)
            {
                foreach (var arguments in Cases(test))
                {
                    var label = $"{type.Name}.{test.Name}" +
                                (arguments.Length > 0 ? "(" + string.Join(", ", arguments) + ")" : "");

                    if (filter != null && label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var outcome = Run(type, test, setUp, tearDown, arguments, out var message);

                    switch (outcome)
                    {
                        case 0:
                            passed++;
                            Console.WriteLine($"  ok   {label}");
                            break;
                        case 1:
                            failed++;
                            Console.WriteLine($"  FAIL {label}: {message}");
                            break;
                        default:
                            needUnity++;
                            Console.WriteLine($"  ---- {label}: нужен Unity ({message})");
                            break;
                    }
                }
            }
        }

        Console.WriteLine($"ИТОГ: прошло {passed}, упало {failed}, нужен Unity {needUnity}");
        return failed == 0 ? 0 : 1;
    }

    private static bool Has(MemberInfo member, string attribute) =>
        member.GetCustomAttributes(true).Any(a => a.GetType().Name == attribute);

    private static IEnumerable<object[]> Cases(MethodInfo test)
    {
        var cases = test.GetCustomAttributes(true).Where(a => a.GetType().Name == "TestCaseAttribute").ToList();

        if (cases.Count == 0)
        {
            yield return Array.Empty<object>();
            yield break;
        }

        foreach (var attribute in cases)
        {
            var raw = (object[])attribute.GetType().GetProperty("Arguments").GetValue(attribute);
            var parameters = test.GetParameters();
            var converted = new object[raw.Length];

            for (var i = 0; i < raw.Length; i++)
            {
                converted[i] = raw[i] != null && i < parameters.Length && parameters[i].ParameterType != raw[i].GetType()
                    ? Convert.ChangeType(raw[i], parameters[i].ParameterType)
                    : raw[i];
            }

            yield return converted;
        }
    }

    /// <returns>0 — прошёл, 1 — упал, 2 — нужен движок.</returns>
    private static int Run(Type type, MethodInfo test, MethodInfo setUp, MethodInfo tearDown, object[] arguments,
        out string message)
    {
        message = null;
        object instance;

        try
        {
            instance = Activator.CreateInstance(type);
        }
        catch (Exception e)
        {
            return Classify(e, out message);
        }

        var result = 0;

        try
        {
            setUp?.Invoke(instance, null);
            test.Invoke(instance, arguments);
        }
        catch (Exception e)
        {
            result = Classify(e, out message);
        }
        finally
        {
            try
            {
                tearDown?.Invoke(instance, null);
            }
            catch (Exception e)
            {
                if (result == 0) result = Classify(e, out message);
            }
        }

        return result;
    }

    private static int Classify(Exception e, out string message)
    {
        while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;

        var name = e.GetType().Name;
        message = e.Message.Replace(Environment.NewLine, " ").Trim();

        if (name == "SuccessException") return 0;

        // Вызов в движок без движка: extern-метод, нативная часть, ECall.
        if (e is MissingMethodException || e is EntryPointNotFoundException || e is DllNotFoundException ||
            e is System.Security.SecurityException || name == "UnityException" ||
            e.StackTrace != null && e.StackTrace.Contains("UnityEngine.") && e is NotSupportedException)
        {
            return 2;
        }

        return 1;
    }
}
