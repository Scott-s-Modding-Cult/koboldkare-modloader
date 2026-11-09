using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;

namespace KoboldKare.ModKit {
    /// <summary>
    /// Loads every type in an assembly and JIT-compiles its methods at startup. KoboldKare strips unused
    /// engine/game code, so a mod can compile fine yet hit TypeLoadException/MissingMethodException at
    /// runtime, which often shows up only as Unity's vague "script could not be instantiated". Running
    /// this from your plugin's Awake turns that into a clear log line naming the type and method.
    /// <code>SelfCheck.Run(typeof(MyPlugin).Assembly, Logger);</code>
    /// </summary>
    public static class SelfCheck {
        public static int Run(Assembly assembly, ManualLogSource log) {
            int problems = 0;
            Type[] types;
            try {
                types = assembly.GetTypes();
            } catch (ReflectionTypeLoadException e) {
                types = e.Types.Where(t => t != null).ToArray();
                foreach (var loaderException in e.LoaderExceptions.Where(x => x != null).Take(5)) {
                    log.LogError("Self-check: " + loaderException.Message);
                    problems++;
                }
            }

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                     BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var type in types) {
                if (type.ContainsGenericParameters) {
                    continue;
                }
                try {
                    RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                } catch (Exception e) {
                    log.LogError($"Self-check: type {type.FullName} failed to initialise: {e.GetBaseException().Message}");
                    problems++;
                    continue;
                }
                foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all))) {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) {
                        continue;
                    }
                    try {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    } catch (Exception e) {
                        log.LogError($"Self-check: {type.FullName}.{method.Name} can't run in this game: {e.GetBaseException().Message}");
                        problems++;
                    }
                }
            }
            log.LogInfo(problems == 0
                ? $"Self-check passed ({types.Length} types)."
                : $"Self-check found {problems} problem(s); see errors above.");
            return problems;
        }
    }
}
