using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Models;
using StardewModdingAPI.Framework.ModHelpers;
using StardewModdingAPI.Framework.ModLoading;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Internal.ConsoleWriting;
using StardewModdingAPI.Toolkit.Serialization.Models;
using StardewModdingAPI.Toolkit.Utilities;
using StardewValley.Extensions;

namespace StardewModdingAPI.Integrations.GenericModConfigMenu
{
    /// <summary>Registers the SMAPI configuration with Generic Mod Config Menu.</summary>
    internal class GenericModConfigMenuIntegration
    {
        /*********
        ** Fields
        *********/
        /// <summary>The unique mod ID for Generic Mod Config Menu.</summary>
        private const string GenericModConfigMenuModId = "spacechase0.GenericModConfigMenu";

        /// <summary>The minimum version of Generic Mod Config Menu supported by this integration.</summary>
        private const string MinVersion = "1.9.6";

        /// <summary>Encapsulates monitoring and logging.</summary>
        private readonly IMonitor Monitor;

        /// <summary>The core SMAPI translations.</summary>
        private readonly Translator Translations;

        /// <summary>Get the current SMAPI settings.</summary>
        private readonly Func<SConfig> GetConfig;

        /// <summary>Reload the SMAPI settings when they're changed through the config menu.</summary>
        private readonly Action ReloadConfig;

        /// <summary>The custom setting values which haven't been saved yet.</summary>
        private readonly Dictionary<string, object> StagedSettings = [];


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="monitor"><inheritdoc cref="Monitor" path="/summary" /></param>
        /// <param name="translations"><inheritdoc cref="Translations" path="/summary" /></param>
        /// <param name="getConfig"><inheritdoc cref="GetConfig" path="/summary" /></param>
        /// <param name="reloadConfig"><inheritdoc cref="ReloadConfig" path="/summary" /></param>
        public GenericModConfigMenuIntegration(IMonitor monitor, Translator translations, Func<SConfig> getConfig, Action reloadConfig)
        {
            this.Monitor = monitor;
            this.Translations = translations;
            this.GetConfig = getConfig;
            this.ReloadConfig = reloadConfig;
        }

        /// <summary>Register the mod config.</summary>
        /// <param name="internalModRegistry">An API for fetching metadata about loaded mods.</param>
        public void Register(ModRegistry internalModRegistry)
        {
            try
            {
                // init
                ModMetadata smapiMod = this.CreateFakeMod();
                IModRegistry modRegistry = this.CreateModRegistryHelper(internalModRegistry, this.Monitor, smapiMod);
                IGenericModConfigMenuApi? api = this.GetGenericModConfigMenuApi(modRegistry, this.Monitor);
                if (api is null)
                    return;

                // register config
                api.Register(smapiMod.Manifest, this.OnReset, this.Save);
                var getConfig = this.GetConfig;

                // add 'SMAPI features' section
                api.AddSectionTitle(smapiMod.Manifest, () => this.Translations.Get("config.section.features"));
                api.AddBoolOption(
                    mod: smapiMod.Manifest,
                    name: () => this.Translations.Get("config.check-for-updates.name"),
                    tooltip: () => this.Translations.Get("config.check-for-updates.desc"),
                    getValue: () => getConfig().CheckForUpdates,
                    setValue: value => this.StageOption(nameof(SConfig.CheckForUpdates), value)
                );
                api.AddBoolOption(
                    mod: smapiMod.Manifest,
                    name: () => this.Translations.Get("config.check-content-integrity.name"),
                    tooltip: () => this.Translations.Get("config.check-content-integrity.desc"),
                    getValue: () => getConfig().CheckContentIntegrity,
                    setValue: value => this.StageOption(nameof(SConfig.CheckContentIntegrity), value)
                );
                api.AddBoolOption(
                    mod: smapiMod.Manifest,
                    name: () => this.Translations.Get("config.read-console-input.name"),
                    tooltip: () => this.Translations.Get("config.read-console-input.desc"),
                    getValue: () => getConfig().ListenForConsoleInput,
                    setValue: value => this.StageOption(nameof(SConfig.ListenForConsoleInput), value)
                );

                // add 'console window' section
                api.AddSectionTitle(smapiMod.Manifest, () => this.Translations.Get("config.section.console-window"));
                api.AddBoolOption(
                    mod: smapiMod.Manifest,
                    name: () => this.Translations.Get("config.developer-mode.name"),
                    tooltip: () => this.Translations.Get("config.developer-mode.desc"),
                    getValue: () => getConfig().DeveloperMode,
                    setValue: value => this.StageOption(nameof(SConfig.DeveloperMode), value)
                );
                api.AddTextOption(
                    mod: smapiMod.Manifest,
                    name: () => this.Translations.Get("config.color-scheme.name"),
                    tooltip: () => this.Translations.Get("config.color-scheme.desc"),
                    getValue: () => getConfig().ConsoleColorScheme.ToString(),
                    setValue: value => this.StageOption(nameof(SConfig.ConsoleColorScheme), value),
                    allowedValues: Enum.GetValues<MonitorColorScheme>().Select(p => p.ToString()).ToArray(),
                    formatAllowedValue: value => this.Translations.Get(
                        value is nameof(MonitorColorScheme.AutoDetect) && Constants.Platform == Platform.Windows
                            ? $"config.color-scheme.options.{value}.on-windows"
                            : $"config.color-scheme.options.{value}"
                    ));

                // add 'verbose logging' section
                api.AddSectionTitle(smapiMod.Manifest, () => this.Translations.Get("config.section.verbose-logs"));
                api.AddParagraph(smapiMod.Manifest, () => this.Translations.Get("config.section.verbose-logs.explanation"));
                api.AddTextOption(
                    mod: smapiMod.Manifest,
                    name: () => this.Translations.Get("config.enable-for.name"),
                    tooltip: () => this.Translations.Get("config.enable-for.desc"),
                    getValue: () => getConfig().VerboseLogging.Contains("*").ToString(),
                    setValue: value => this.StageVerboseLoggingOption("*", value == bool.TrueString),
                    allowedValues: [bool.TrueString, bool.FalseString],
                    formatAllowedValue: value => this.Translations.Get($"config.enable-for.options.{(value == bool.TrueString ? "all" : "selected")}")
                );
                api.AddBoolOption(
                    mod: smapiMod.Manifest,
                    name: () => this.Translations.Get("config.enable-for-smapi.name"),
                    tooltip: () => this.Translations.Get("config.enable-for-smapi.desc"),
                    getValue: () => getConfig().VerboseLogging.Contains("SMAPI"),
                    setValue: value => this.StageVerboseLoggingOption("SMAPI", value)
                );
                foreach (IModInfo mod in modRegistry.GetAll().OrderBy(p => p.Manifest.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (mod.IsContentPack)
                        continue;

                    api.AddBoolOption(
                        mod: smapiMod.Manifest,
                        name: () => this.Translations.Get("config.enable-for-mod.name", new { modName = mod.Manifest.Name }),
                        tooltip: () => this.Translations.Get("config.enable-for-mod.desc", new { modName = mod.Manifest.Name }),
                        getValue: () => getConfig().VerboseLogging.Contains(mod.Manifest.UniqueID),
                        setValue: value => this.StageVerboseLoggingOption(mod.Manifest.UniqueID, value)
                    );
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log("Couldn't register the SMAPI settings with Generic Mod Config Menu. This has no effect on SMAPI, but the in-game config menu won't be available.", LogLevel.Warn);
                this.Monitor.Log(ex.ToString());
            }
        }


        /*********
        ** Private methods
        *********/
        /****
        ** Save events
        ****/
        /// <summary>Reset the SMAPI configuration to the default values.</summary>
        private void OnReset()
        {
            try
            {
                foreach (string customPath in new[] { Constants.ApiUserConfigPath, Constants.ApiModGroupConfigPath })
                {
                    if (File.Exists(customPath))
                        File.Delete(customPath);
                }

                this.ReloadConfig();
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't reset SMAPI to the default settings from the Generic Mod Config Menu UI.\n\nTechnical details:\n{ex}", LogLevel.Error);
            }
        }

        /// <summary>Save the mod configuration.</summary>
        private void Save()
        {
            try
            {
                // load settings
                JsonSettingsWrapper? defaultSettings = JsonSettingsWrapper.TryLoadFile(Constants.ApiConfigPath, this.Monitor);
                JsonSettingsWrapper? userSettings = JsonSettingsWrapper.TryLoadFile(Constants.ApiUserConfigPath, this.Monitor);
                JsonSettingsWrapper? modGroupSettings = JsonSettingsWrapper.TryLoadFile(Constants.ApiModGroupConfigPath, this.Monitor);

                // skip if invalid
                if (defaultSettings is null)
                {
                    this.Monitor.Log("Couldn't save the SMAPI settings from the Generic Mod Config Menu UI. The default settings could not be loaded.", LogLevel.Error);
                    return;
                }

                // apply edited options
                if (this.StagedSettings.Count > 0)
                {
                    // create settings file if needed
                    if (userSettings is null && modGroupSettings is null)
                        userSettings = new JsonSettingsWrapper(Constants.ApiUserConfigPath);

                    // save simple options
                    foreach ((string fieldName, object newValue) in this.StagedSettings)
                    {
                        // replace existing property if possible
                        if (modGroupSettings?.SetUserOption(fieldName, newValue, defaultSettings, overwriteOnly: true) is true)
                            continue;
                        if (userSettings?.SetUserOption(fieldName, newValue, defaultSettings, overwriteOnly: true) is true)
                            continue;

                        // else add new property
                        (modGroupSettings ?? userSettings)?.SetUserOption(fieldName, newValue, defaultSettings, overwriteOnly: false);
                    }
                }

                // save to files
                if (modGroupSettings?.Changed is true)
                    modGroupSettings.SaveOrDeleteFile();
                if (userSettings?.Changed is true)
                    userSettings.SaveOrDeleteFile();

                // reload settings
                this.StagedSettings.Clear();
                this.ReloadConfig();
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't save the SMAPI settings from the Generic Mod Config Menu UI.\n\nTechnical details:\n{ex}", LogLevel.Error);
            }
        }

        /****
        ** Config option staging
        ****/
        /// <summary>Store a value set by the player before it's saved to the config files.</summary>
        /// <param name="fieldName">The name of the <see cref="SConfig"/> field to change.</param>
        /// <param name="value">The value to stage.</param>
        private void StageOption(string fieldName, object value)
        {
            this.StagedSettings[fieldName] = value;
        }

        /// <summary>Store a custom <see cref="SConfig.VerboseLogging"/> option before it's saved to the config files.</summary>
        /// <param name="id">The mod ID, 'SMAPI', or '*' (all) for which to toggle verbose logging.</param>
        /// <param name="enable">Whether verbose logging is enabled.</param>
        private void StageVerboseLoggingOption(string id, bool enable)
        {
            const string fieldName = nameof(SConfig.VerboseLogging);

            if (this.StagedSettings.TryGetValue(fieldName, out object? rawValue) && rawValue is HashSet<string> set)
                set.Toggle(id, enable);
            else
            {
                HashSet<string> newSet = new HashSet<string>(this.GetConfig().VerboseLogging, StringComparer.OrdinalIgnoreCase);
                newSet.Toggle(id, enable);
                this.StagedSettings[fieldName] = newSet;
            }
        }

        /****
        ** Initialization
        ****/
        /// <summary>Get the public API for Generic Mod Config Menu, if it's available.</summary>
        /// <param name="modRegistry">The mod registry to query.</param>
        /// <param name="monitor">The monitor with which to log issues loading the API.</param>
        /// <returns>Returns the API if it's available, else <c>null</c>.</returns>
        private IGenericModConfigMenuApi? GetGenericModConfigMenuApi(IModRegistry modRegistry, IMonitor monitor)
        {
            // check mod is installed
            if (modRegistry.Get(GenericModConfigMenuIntegration.GenericModConfigMenuModId) is not { } configMod)
                return null;

            // check min version
            if (configMod.Manifest.Version.IsOlderThan(GenericModConfigMenuIntegration.MinVersion))
            {
                monitor.Log("Can't register the SMAPI settings with Generic Mod Config Menu, because you need version 1.9.6 or later of that mod.", LogLevel.Debug);
                return null;
            }

            // check API
            if (modRegistry.GetApi<IGenericModConfigMenuApi>(GenericModConfigMenuIntegration.GenericModConfigMenuModId) is not { } api)
            {
                monitor.Log("Can't register the SMAPI settings with Generic Mod Config Menu, because it unexpectedly had no API.", LogLevel.Debug);
                return null;
            }

            return api;
        }

        /// <summary>Create a fake mod entry to represent SMAPI itself.</summary>
        private ModMetadata CreateFakeMod()
        {
            IManifest manifest = new Manifest(
                uniqueId: "SMAPI",
                name: "SMAPI",
                author: "Pathoschild",
                description: string.Empty,
                version: Constants.ApiVersion,
                minimumApiVersion: Constants.ApiVersion,
                minimumGameVersion: Constants.MinimumGameVersion,
                entryDll: "StardewModdingAPI.exe",
                contentPackFor: null,
                dependencies: [],
                updateKeys: []
            );

            return new ModMetadata(
                displayName: "SMAPI",
                directoryPath: Constants.GamePath,
                rootPath: Constants.ModsPath,
                manifest: manifest,
                dataRecord: null,
                isIgnored: false
            );
        }

        /// <summary>Create a mod registry helper.</summary>
        /// <param name="internalRegistry">The internal mod registry.</param>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        /// <param name="smapiMod">The fake mod entry representing SMAPI itself.</param>
        private IModRegistry CreateModRegistryHelper(ModRegistry internalRegistry, IMonitor monitor, ModMetadata smapiMod)
        {
            return new ModRegistryHelper(
                mod: smapiMod,
                registry: internalRegistry,
                proxyFactory: new InterfaceProxyFactory(),
                monitor: monitor
            );
        }
    }
}
