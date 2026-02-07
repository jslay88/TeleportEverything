using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LocalizationManager;
using ServerSync;
using UnityEngine;

#nullable enable
namespace TeleportEverything
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    [BepInDependency(SkyheimGuid, BepInDependency.DependencyFlags.SoftDependency)]
    internal partial class Plugin : BaseUnityPlugin
    {
        internal const string ModName = "TeleportEverything";
        internal const string ModVersion = "2.9.0";
        internal const string Author = "kpro";
        internal const string ModURL = "https://thunderstore.io/c/valheim/p/OdinPlus/TeleportEverything/";
        private const string ModGUID = "com."+ Author + "." + ModName;

        private const string ConfigFileName = ModGUID + ".cfg";
        private static readonly string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;

        private readonly Harmony _harmony = new(ModGUID);

        public static readonly ManualLogSource TeleportEverythingLogger =
            BepInEx.Logging.Logger.CreateLogSource(ModName);

        private static readonly ConfigSync ConfigSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };
        // Mod
        private static ConfigEntry<bool>? _serverConfigLocked;
        public static ConfigEntry<bool>? EnableMod;
        public static ConfigEntry<string>? TeleportMode;

        // Transport Allies
        public static ConfigEntry<bool>? ServerEnableMask;
        public static ConfigEntry<string>? ServerTransportMask;
        public static ConfigEntry<float>? TransportRadius;
        public static ConfigEntry<float>? TransportVerticalTolerance;
        public static ConfigEntry<float>? SpawnForwardOffset;
        public static ConfigEntry<bool>? TransportWolves;
        public static ConfigEntry<bool>? TransportBoar;
        public static ConfigEntry<bool>? TransportLox;

        // Transport Enemies
        public static ConfigEntry<int>? SpawnEnemiesForwardOffset;
        public static ConfigEntry<int>? EnemySpawnRadius;
        private const float UP_OFFSET = .2f;
        public static ConfigEntry<string>? EnemiesMaskMode;
        public static ConfigEntry<string>? EnemiesTransportMask;

        //Portal
        public static ConfigEntry<float>? PortalActivationRange;
        public static ConfigEntry<float>? PortalSoundVolume;
        public static ConfigEntry<bool>? ShowTransportAnimationScreen;

        //Teleport Self
        public static ConfigEntry<float>? SearchRadius;
        public static List<Character>? Enemies;
        public static List<Character>? Allies;

        //Items
        public static ConfigEntry<bool>? TransportDragonEggs;
        public static ConfigEntry<bool>? TransportOres;
        public static ConfigEntry<int>? TransportFee;
        public static ConfigEntry<string>? RemoveTransportFeeFrom;

        //User Settings
        public static ConfigEntry<string>? IncludeMode;
        public static ConfigEntry<string>? MessageMode;
        public static ConfigEntry<bool>? PlayerEnableMask;
        public static ConfigEntry<string>? PlayerTransportMask;

        //Trasport carts
        public static ConfigEntry<string>? TransportCartsMode;
        public static ConfigEntry<bool>? ShouldTaxCarts;
        public static ZDOID? attachedTeleportingCartId = null;
        internal static bool currrentCartBeingTaxed = false;
        private const float CART_SIZE = 2.5f;
        private const float CART_FORWARD_OFFSET = 0.8f;
        private const float CART_Y_OFFSET = 0.3f;

        // Include vars
        public static bool TransportAllies;
        public static bool IncludeTamed;
        public static bool IncludeNamed;
        public static bool IncludeFollow;
        public static bool ExcludeNamed;

        public static bool TeleportTriggered = false;
        public static bool IsDungeonTeleport = false;
        public static bool ShowVikingsDontRun = false;

        // constants
        private const string LOX = "lox";
        private const string BOAR = "boar";
        private const string WOLF = "wolf";
        private const string DRAGON_EGG = "DragonEgg";

        private void Awake()
        {
            // Defer localization until Valheim's PlatformPrefs are initialized (avoids NullReferenceException in PlatformPrefs.TryGetPreferencesProvider during chainload).
            StartCoroutine(LoadLocalizationWhenReady());

            CreateConfigValues();

            CheckAndPatchSkyheim();

            _harmony.PatchAll();
            SetupWatcher();

            Enemies = new List<Character>();
            Allies = new List<Character>();

            ClearIncludeVars();
            Debug.Log($"{ModName} Loaded...");
        }

        private static IEnumerator LoadLocalizationWhenReady()
        {
            const int maxAttempts = 50;
            const float delaySeconds = 0.2f;
            for (int i = 0; i < maxAttempts; ++i)
            {
                if (i > 0)
                    yield return new WaitForSeconds(delaySeconds);
                try
                {
                    Localizer.Load();
                    TeleportEverythingLogger.LogDebug("Localization loaded.");
                    yield break;
                }
                catch (System.Exception ex)
                {
                    if (i == maxAttempts - 1)
                        TeleportEverythingLogger.LogError($"Failed to load localization after {maxAttempts} attempts: {ex.Message}");
                }
            }
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
        }

        #region CreateConfigValues
        private void CreateConfigValues()
        {
            //Mod
            EnableMod = config("--- Mod ---", "Enable Mod", true, "Enable/Disable mod");
            _serverConfigLocked = config("--- Mod ---", "Force Server Config", true, "Force Server Config");
            _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);
            MessageMode = config("--- Mod ---", "Message Mode", "centered",
                new ConfigDescription("Message Mode",
                    new AcceptableValueList<string>("No messages", "top left", "centered")), false);

            //Portal
            PortalActivationRange = config("--- Portal ---", "Portal Activation Range", 5f,
                new ConfigDescription("Portal activation range in meters.",
                    new AcceptableValueRange<float>(0, 20f),
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 4 }), false);
            PortalSoundVolume = config("--- Portal ---", "Portal Sound Volume", 0.8f,
                new ConfigDescription("Portal sound effect volume (rejoin the session or teleport to a farther portal for the new value to take effect).",
                    new AcceptableValueRange<float>(0, 1),
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 3 }), false);
            ShowTransportAnimationScreen = config("--- Portal ---", "Show Transport Animation", true,
                new ConfigDescription("Toggle transport animation screen on/off.", null,
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 2 }), false);

            // Portal Behavior
            TeleportMode = config("--- Portal Behavior ---", "Teleport Mode", "Standard",
                new ConfigDescription("Teleport Mode",
                    new AcceptableValueList<string>("Standard", "Vikings Don't Run",
                        "Take Them With You"), new ConfigurationManagerAttributes { IsAdvanced = false, Order = 2 }));
            SearchRadius = config("--- Portal Behavior ---", "Search Radius", 10f,
                new ConfigDescription("Radius to search creatures in meters.", null,
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 1 }));

            //Server
            ServerEnableMask = config("--- Server ---", "Server Filter By Mask", false,
                new ConfigDescription(
                    "Enable to filter and restrict which tameable creatures can teleport on server.", null,
                    new ConfigurationManagerAttributes { IsAdvanced = true }));
            ServerTransportMask = config("--- Server ---", "Server Transport Mask", "",
                new ConfigDescription("Add the prefab names to filter and restrict which creatures can be teleportable on the server", null,
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            // Transport
            IncludeMode = config("--- Transport ---", "Ally Mode", "Only Follow",
                new ConfigDescription("Ally Mode",
                    new AcceptableValueList<string>("No Allies", "All tamed", "Only Follow",
                        "All tamed except Named", "Only Named"),
                    new ConfigurationManagerAttributes { IsAdvanced = false, Order = 7 }), false);

            TransportRadius = config("--- Transport ---", "Transport Radius", 10f,
                new ConfigDescription("", null,
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 6 }));

            TransportVerticalTolerance = config("--- Transport ---", "Vertical Tolerance", 2f,
                new ConfigDescription("", null,
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 5 }));

            SpawnForwardOffset = config("--- Transport ---", "Spawn Forward Tolerance", 1.5f,
            new ConfigDescription("Allies spawn forward offset",
                new AcceptableValueRange<float>(0, 12f),
                new ConfigurationManagerAttributes { IsAdvanced = true, Order = 4 }));

            PlayerEnableMask = config("--- Transport ---", "Player Filter By Mask", false,
                new ConfigDescription("Enable to filter and restrict which tameable creatures can teleport.", null,
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 2 }), false);

            PlayerTransportMask = config("--- Transport ---", "Player Transport Mask", "",
                new ConfigDescription("Add the prefab names to filter and restrict which creatures can be teleportable.", null,
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 1 }), false);

            TransportBoar = config("--- Transport ---", "Transport Boars", true, "", false);
            TransportLox = config("--- Transport ---", "Transport Loxes", true, "", false);
            TransportWolves = config("--- Transport ---", "Transport Wolves", true, "", false);

            //Transport Carts
            TransportCartsMode = config("--- Transport Carts ---", "Transport Carts Mode", "Enabled",
                new ConfigDescription("Allows transporting carts. (beta)",
                    new AcceptableValueList<string>("Disabled", "Enabled", "Only Dungeons"),
                    new ConfigurationManagerAttributes { Order = 2 }));

            ShouldTaxCarts = config("--- Transport Carts ---", "Transport Carts Tax Items", true,
                new ConfigDescription("Take fee from cart prohibited items.", null,
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 1 }));
            
            //Enemies
            SpawnEnemiesForwardOffset = config("--- Transport Enemies ---", "Spawn Enemies Forward Tolerance", 6,
            new ConfigDescription("Min Spawn forward in meters if Take Enemies With you is enabled.",
                new AcceptableValueRange<int>(0, 12),
                new ConfigurationManagerAttributes { IsAdvanced = true, Order = 4 }));

            EnemySpawnRadius = config("--- Transport Enemies ---", "Max Enemy Spawn Radius", 3,
            new ConfigDescription("Max Enemy Spawn Radius if Take Enemies With you is enabled.",
                new AcceptableValueRange<int>(0, 15),
                new ConfigurationManagerAttributes { IsAdvanced = true, Order = 3 }));

            EnemiesMaskMode = config("--- Transport Enemies ---", "Enemies Mask Mode", "Disabled",
                new ConfigDescription("This option changes the behavior of the Enemies Mask field.",
                    new AcceptableValueList<string>("Disabled","Block", "Allow Only"),
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 2 }));

            EnemiesTransportMask = config("--- Transport Enemies ---", "Enemies Mask", "",
                new ConfigDescription("This mask is to block or allow only specific enemies to follow the player. If Take Enemies With you is enabled.", null,
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 1 }));

            // Transport Items
            TransportDragonEggs = config("--- Transport Items ---", "Transport Dragon Eggs", true,
                new ConfigDescription("Allows transporting dragon eggs."));
            TransportOres = config("--- Transport Items ---", "Transport Ores", true,
                new ConfigDescription(
                    "Allows transporting ores, ingots and other restricted items."));
            TransportFee = config("--- Transport Items ---", "Transport fee", 0,
                new ConfigDescription("Transport Fee in (%) ore",
                    new AcceptableValueRange<int>(0, 100),
                    new ConfigurationManagerAttributes { Order = 2 }));
            RemoveTransportFeeFrom = config("--- Transport Items ---", "Remove Transport fee from", "DragonEgg",
                new ConfigDescription("Add the prefab names to remove fee from items on the server", null,
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 1 }));
        }
        #endregion

        private static void ClearIncludeVars()
        {
            TransportAllies = false;
            IncludeTamed = false;
            IncludeNamed = false;
            IncludeFollow = false;
            ExcludeNamed = false;
        }

        public static void SetIncludeMode()
        {
            ClearIncludeVars();

            if (IncludeMode is null) return;
            if (!IncludeMode.Value.Contains("No Allies"))
            {
                TransportAllies = true;
            }

            if (IncludeMode.Value.Equals("All tamed"))
            {
                IncludeTamed = true;
                IncludeNamed = true;
                IncludeFollow = true;
            }

            if (IncludeMode.Value.Contains("Only Follow"))
            {
                IncludeFollow = true;
            }

            if (IncludeMode.Value.Contains("All tamed except Named"))
            {
                IncludeTamed = true;
                ExcludeNamed = true;
                IncludeFollow = true;
            }

            if (IncludeMode.Value.Contains("Only Named"))
            {
                IncludeNamed = true;
            }
        }

        #region ConfigWatcher
        private void SetupWatcher()
        {
            FileSystemWatcher watcher = new(Paths.ConfigPath, ConfigFileName);
            watcher.Changed += ReadConfigValues;
            watcher.Created += ReadConfigValues;
            watcher.Renamed += ReadConfigValues;
            watcher.IncludeSubdirectories = true;
            watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
            watcher.EnableRaisingEvents = true;
        }

        private void ReadConfigValues(object sender, FileSystemEventArgs e)
        {
            if (!File.Exists(ConfigFileFullPath))
            {
                return;
            }

            try
            {
                TeleportEverythingLogger.LogDebug("ReadConfigValues called");
                Config.Reload();
            }
            catch
            {
                TeleportEverythingLogger.LogError(
                    $"There was an issue loading your {ConfigFileName}");
                TeleportEverythingLogger.LogError(
                    "Please check your config entries for spelling and format!");
            }
        }
        #endregion

        #region ConfigOptions

        private ConfigEntry<T> config<T>(string group, string name, T value,
            ConfigDescription description, bool synchronizedSetting = true)
        {
            ConfigDescription extendedDescription =
                new(
                    description.Description + (synchronizedSetting
                        ? " [Synced with Server]"
                        : " [Not Synced with Server]"), description.AcceptableValues,
                    description.Tags);
            var configEntry = Config.Bind(group, name, value, extendedDescription);

            var syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

            return configEntry;
        }

        private ConfigEntry<T> config<T>(string group, string name, T value, string description,
            bool synchronizedSetting = true) => config(group, name, value,
            new ConfigDescription(description), synchronizedSetting);

        #endregion
    }
}
