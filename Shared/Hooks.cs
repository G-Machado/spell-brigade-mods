using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
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
                             $"scanned={SharedCodeGuard.ScannedFor(target)} sharers={shared.Count}");
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
    /// method in the same native image. Folding across images is not checked.
    /// <para>
    /// The scan walks il2cpp's own class and method tables. It must not touch the interop wrapper
    /// types: a wrapper's static constructor calls il2cpp_runtime_class_init, so reading any static
    /// field on every wrapper runs every game static constructor at mod load. That broke
    /// <c>SaveMigrator</c> (its cctor threw, the save failed to load, the game wrote a fresh profile).
    /// </para>
    /// </summary>
    public static class SharedCodeGuard
    {
        // native image -> code pointer -> MethodInfo pointers with that code
        private static readonly Dictionary<IntPtr, Dictionary<IntPtr, List<IntPtr>>> ByImage =
            new Dictionary<IntPtr, Dictionary<IntPtr, List<IntPtr>>>();

        /// <summary>Other methods sharing the target's native body; empty if none or not an interop method.</summary>
        public static List<string> FindSharers(MethodBase target)
        {
            var info = MethodInfoPointer(target);
            var self = CodePointer(info);
            if (self == IntPtr.Zero) return new List<string>();

            return MapFor(ImageOf(info)).TryGetValue(self, out var infos)
                ? infos.Where(i => i != info).Select(Describe).ToList()
                : new List<string>();
        }

        /// <summary>Methods with native code found in the target's image (0 if not scanned yet).</summary>
        public static int ScannedFor(MethodBase target)
        {
            var info = MethodInfoPointer(target);
            if (info == IntPtr.Zero) return 0;
            return ByImage.TryGetValue(ImageOf(info), out var map) ? map.Values.Sum(l => l.Count) : 0;
        }

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
        public static IntPtr NativePointer(MethodBase method) => CodePointer(MethodInfoPointer(method));

        /// <summary>
        /// The il2cpp MethodInfo behind an interop method, or zero. Reading the wrapper field runs the
        /// declaring type's static constructor; only call it on types that are about to be patched
        /// (Harmony's il2cpp patcher reads the same field anyway).
        /// </summary>
        private static IntPtr MethodInfoPointer(MethodBase method)
        {
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            return field == null ? IntPtr.Zero : (IntPtr)field.GetValue(null);
        }

        // Il2CppMethodInfo.methodPointer is the first field in every metadata version.
        private static unsafe IntPtr CodePointer(IntPtr methodInfo) =>
            methodInfo == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)methodInfo;

        private static IntPtr ImageOf(IntPtr methodInfo) =>
            IL2CPP.il2cpp_class_get_image(IL2CPP.il2cpp_method_get_class(methodInfo));

        /// <summary>
        /// Every method with native code in <paramref name="image"/>, read from il2cpp's tables.
        /// il2cpp_class_get_methods sets up method metadata but never runs a static constructor.
        /// </summary>
        private static Dictionary<IntPtr, List<IntPtr>> MapFor(IntPtr image)
        {
            if (ByImage.TryGetValue(image, out var map)) return map;
            map = new Dictionary<IntPtr, List<IntPtr>>();

            var count = IL2CPP.il2cpp_image_get_class_count(image);
            for (uint i = 0; i < count; i++)
            {
                var klass = IL2CPP.il2cpp_image_get_class(image, i);
                if (klass == IntPtr.Zero) continue;

                var iter = IntPtr.Zero;
                IntPtr method;
                while ((method = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    var code = CodePointer(method);
                    if (code == IntPtr.Zero) continue;
                    if (!map.TryGetValue(code, out var list)) map[code] = list = new List<IntPtr>();
                    list.Add(method);
                }
            }

            ByImage[image] = map;
            return map;
        }

        private static string Describe(IntPtr methodInfo)
        {
            var klass = IL2CPP.il2cpp_method_get_class(methodInfo);
            var ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
            var type = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
            for (var outer = IL2CPP.il2cpp_class_get_declaring_type(klass); outer != IntPtr.Zero;
                 outer = IL2CPP.il2cpp_class_get_declaring_type(outer))
                type = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(outer)) + "+" + type;
            var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(methodInfo));
            return string.IsNullOrEmpty(ns) ? $"{type}.{name}" : $"{ns}.{type}.{name}";
        }
    }
}
