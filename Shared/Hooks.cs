using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Common;
using MelonLoader;

namespace SpellBrigadeMods.Shared
{
    /// <summary>
    /// Fail-soft hook installer. Each hook is resolved and patched on its own; a missing or
    /// refused target is logged and skipped. Ends with "hooks N/M"; N == 0 with M > 0 is an error.
    /// </summary>
    public sealed class Hooks
    {
        private sealed class Spec
        {
            public string Name;
            public Func<MethodBase> Resolve;
            public HarmonyMethod Prefix;
            public HarmonyMethod Postfix;
        }

        private readonly List<Spec> _specs = new List<Spec>();
        private readonly HarmonyLib.Harmony _harmony;
        private readonly MelonLogger.Instance _log;

        public int Installed { get; private set; }
        public int Total => _specs.Count;

        public Hooks(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            _harmony = harmony;
            _log = log;
        }

        /// <summary>Queue a hook. <paramref name="resolve"/> runs inside Install, so a renamed target fails soft.</summary>
        public Hooks Add(string name, Func<MethodBase> resolve, MethodInfo prefix = null, MethodInfo postfix = null)
        {
            _specs.Add(new Spec
            {
                Name = name,
                Resolve = resolve,
                Prefix = prefix == null ? null : new HarmonyMethod(prefix),
                Postfix = postfix == null ? null : new HarmonyMethod(postfix),
            });
            return this;
        }

        /// <summary>Convenience: hook <c>T.method</c> by name (optionally by parameter types).</summary>
        public Hooks Add<T>(string method, Type[] args = null, MethodInfo prefix = null, MethodInfo postfix = null)
            => Add($"{typeof(T).FullName}.{method}", () => AccessTools.Method(typeof(T), method, args), prefix, postfix);

        public int Install()
        {
            Installed = 0;
            foreach (var spec in _specs)
            {
                try
                {
                    var target = spec.Resolve();
                    if (target == null)
                    {
                        _log.Warning($"hook {spec.Name}: target not found");
                        continue;
                    }

                    var shared = SharedCodeGuard.FindSharers(target);
                    _log.Msg($"guard {spec.Name}: rva=0x{SharedCodeGuard.Rva(target):X} " +
                             $"scanned={SharedCodeGuard.ScannedIn(target.DeclaringType.Assembly)} sharers={shared.Count}");
                    if (shared.Count > 0)
                    {
                        _log.Warning($"hook {spec.Name}: refused, native body shared with {string.Join(", ", shared.Take(5))}" +
                                     (shared.Count > 5 ? $" (+{shared.Count - 5})" : ""));
                        continue;
                    }

                    _harmony.Patch(target, spec.Prefix, spec.Postfix);
                    Installed++;
                    _log.Msg($"hook {spec.Name}: ok");
                }
                catch (Exception e)
                {
                    _log.Warning($"hook {spec.Name}: failed: {e.GetType().Name}: {e.Message}");
                }
            }

            var summary = $"hooks {Installed}/{Total}";
            if (Total > 0 && Installed == 0) _log.Error(summary);
            else _log.Msg(summary);
            return Installed;
        }
    }

    /// <summary>
    /// Identical code folding guard: IL2CPP may compile distinct methods to one native body, so a
    /// patch on one changes all. Compares the target's native method pointer with every other
    /// interop method in the same assembly. Folding across assemblies is not checked.
    /// </summary>
    public static class SharedCodeGuard
    {
        private const string PtrFieldPrefix = "NativeMethodInfoPtr_";
        private static readonly Dictionary<Assembly, Dictionary<IntPtr, List<string>>> ByAssembly =
            new Dictionary<Assembly, Dictionary<IntPtr, List<string>>>();

        /// <summary>Other methods sharing the target's native body; empty if none or not an interop method.</summary>
        public static List<string> FindSharers(MethodBase target)
        {
            var self = NativePointer(target);
            if (self == IntPtr.Zero) return new List<string>();

            var map = MapFor(target.DeclaringType.Assembly);
            var selfName = Describe(target);
            return map.TryGetValue(self, out var names)
                ? names.Where(n => n != selfName).ToList()
                : new List<string>();
        }

        /// <summary>Interop methods with a native pointer found in <paramref name="asm"/> (0 if not scanned yet).</summary>
        public static int ScannedIn(Assembly asm) =>
            ByAssembly.TryGetValue(asm, out var map) ? map.Values.Sum(l => l.Count) : 0;

        /// <summary>Native pointer minus the GameAssembly.dll base: comparable to the game map's RVA.</summary>
        public static long Rva(MethodBase method)
        {
            var ptr = NativePointer(method).ToInt64();
            if (ptr == 0) return 0;
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
                if (string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                    return ptr - m.BaseAddress.ToInt64();
            return ptr;
        }

        /// <summary>The compiled code address behind an interop method, or zero.</summary>
        public static unsafe IntPtr NativePointer(MethodBase method)
        {
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            if (field == null) return IntPtr.Zero;
            var methodInfo = (IntPtr)field.GetValue(null);
            // Il2CppMethodInfo.methodPointer is the first field in every metadata version.
            return methodInfo == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)methodInfo;
        }

        private static Dictionary<IntPtr, List<string>> MapFor(Assembly asm)
        {
            if (ByAssembly.TryGetValue(asm, out var map)) return map;
            map = new Dictionary<IntPtr, List<string>>();

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                     BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var type in types)
            {
                // Cheap filter: only generated types carry NativeMethodInfoPtr_ fields.
                if (!type.GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                        .Any(f => f.Name.StartsWith(PtrFieldPrefix, StringComparison.Ordinal)))
                    continue;

                foreach (var m in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
                {
                    IntPtr ptr;
                    try { ptr = NativePointer(m); }
                    catch { continue; }
                    if (ptr == IntPtr.Zero) continue;
                    if (!map.TryGetValue(ptr, out var list)) map[ptr] = list = new List<string>();
                    list.Add(Describe(m));
                }
            }

            ByAssembly[asm] = map;
            return map;
        }

        private static string Describe(MethodBase m) =>
            $"{m.DeclaringType?.FullName}.{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})";
    }
}
