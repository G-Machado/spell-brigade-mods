using Il2Cpp;
using MelonLoader;
using SpellBrigadeMods.Shared;

[assembly: MelonInfo(typeof(SpellBrigadeMods.Hello.HelloMod), "Hello", "0.1.0", "spell-brigade-mods")]
[assembly: MelonGame(null, "The Spell Brigade")]

namespace SpellBrigadeMods.Hello
{
    /// <summary>P0 smoke mod: one hook that fires when the main menu loads.</summary>
    public sealed class HelloMod : MelonMod
    {
        private static MelonLogger.Instance _log;

        public override void OnInitializeMelon()
        {
            _log = LoggerInstance;
            new Hooks(HarmonyInstance, LoggerInstance)
                .Add<MainMenuManager>("Awake", postfix: typeof(HelloMod).GetMethod(nameof(AwakePostfix)))
                .Install();
        }

        public static void AwakePostfix() => _log.Msg("MainMenuManager.Awake fired");
    }
}
