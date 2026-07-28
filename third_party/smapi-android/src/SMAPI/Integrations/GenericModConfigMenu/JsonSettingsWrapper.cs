using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StardewModdingAPI.Framework.Models;

namespace StardewModdingAPI.Integrations.GenericModConfigMenu;

/// <summary>Wraps access to a JSON file which stores <see cref="SConfig"/> fields.</summary>
internal class JsonSettingsWrapper
{
    /*********
    ** Accessors
    *********/
    /// <summary>The full path to the underlying file.</summary>
    public string FilePath { get; }

    /// <summary>The loaded option properties.</summary>
    public Dictionary<string, JToken> Properties { get; }

    /// <summary>Whether the saved values have changed.</summary>
    public bool Changed { get; private set; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="filePath"><inheritdoc cref="FilePath" path="/summary" /></param>
    public JsonSettingsWrapper(string filePath)
        : this(filePath, new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase)) { }

    /// <summary>Set the value for a user settings field.</summary>
    /// <param name="fieldName">The name of the field to set.</param>
    /// <param name="newValue">The new value to set.</param>
    /// <param name="defaultSettings">The default settings.</param>
    /// <param name="overwriteOnly">Whether to only set the value if it's already set in this file.</param>
    /// <returns>Returns whether the value changed.</returns>
    public bool SetUserOption(string fieldName, object newValue, JsonSettingsWrapper defaultSettings, bool overwriteOnly)
    {
        return fieldName == nameof(SConfig.VerboseLogging)
            ? this.SetVerboseLoggingUserOption(fieldName, (HashSet<string>)newValue, defaultSettings, overwriteOnly)
            : this.SetScalarUserOption(fieldName, newValue, defaultSettings, overwriteOnly);
    }

    /// <summary>Remove a user settings field.</summary>
    /// <param name="fieldName">The name of the field to set.</param>
    /// <returns>Returns whether the value was found and removed.</returns>
    public bool RemoveUserOption(string fieldName)
    {
        bool changed = this.Properties.Remove(fieldName);
        this.Changed = this.Changed || changed;
        return changed;
    }

    /// <summary>Save the user settings to the file on disk, or delete the files if it's empty.</summary>
    public void SaveOrDeleteFile()
    {
        if (this.Properties.Count == 0)
            File.Delete(this.FilePath);
        else
        {
            string json = JsonConvert.SerializeObject(this.Properties, Formatting.Indented);
            File.WriteAllText(this.FilePath, json, Encoding.UTF8);
        }

        this.Changed = false;
    }

    /// <summary>Read a raw JSON settings file.</summary>
    /// <param name="path">The full path for the file to read.</param>
    /// <param name="monitor">The monitor with which to log errors.</param>
    /// <returns>Returns the loaded JSON wrapper, or <c>null</c> if the file doesn't exist or couldn't be loaded.</returns>
    public static JsonSettingsWrapper? TryLoadFile(string path, IMonitor monitor)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            string rawJson = File.ReadAllText(path);
            JObject? json = JsonConvert.DeserializeObject<JObject>(rawJson);
            if (json is null)
                return null;

            Dictionary<string, JToken> properties = new(StringComparer.OrdinalIgnoreCase);
            foreach (JProperty property in json.Properties())
                properties[property.Name] = property.Value;

            return new JsonSettingsWrapper(path, properties);
        }
        catch (Exception ex)
        {
            monitor.Log($"Can't read settings file at {path}.\n\nTechnical details:\n{ex}", LogLevel.Error);
            return null;
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="filePath"><inheritdoc cref="FilePath" path="/summary" /></param>
    /// <param name="properties"><inheritdoc cref="Properties" path="/summary" /></param>
    private JsonSettingsWrapper(string filePath, Dictionary<string, JToken> properties)
    {
        this.FilePath = filePath;
        this.Properties = properties;
    }

    /// <summary>Set the value for a scalar user settings field.</summary>
    /// <param name="fieldName">The name of the field to set.</param>
    /// <param name="newValue">The new value to set.</param>
    /// <param name="defaultSettings">The default settings.</param>
    /// <param name="overwriteOnly">Whether to only set the value if it's already set in this file.</param>
    /// <returns>Returns whether the value changed.</returns>
    public bool SetScalarUserOption(string fieldName, object newValue, JsonSettingsWrapper defaultSettings, bool overwriteOnly)
    {
        // remove default
        if (defaultSettings.Properties.TryGetValue(fieldName, out JToken? rawDefaultValue) && this.TokenHasValue(rawDefaultValue, newValue))
            return this.RemoveUserOption(fieldName);

        // else overwrite value
        if (this.Properties.TryGetValue(fieldName, out JToken? oldValue))
        {
            if (this.TokenHasValue(oldValue, newValue))
                return false;

            this.Properties[fieldName] = JToken.FromObject(newValue);
            this.Changed = true;
            return true;
        }

        // else add value
        if (!overwriteOnly)
        {
            this.Properties[fieldName] = JToken.FromObject(newValue);
            this.Changed = true;
            return true;
        }

        return false;
    }

    /// <summary>Set the value for the verbose logging field.</summary>
    /// <param name="fieldName">The name of the field to set.</param>
    /// <param name="newValue">The new value to set.</param>
    /// <param name="defaultSettings">The default settings.</param>
    /// <param name="overwriteOnly">Whether to only set the value if it's already set in this file.</param>
    /// <returns>Returns whether the value changed.</returns>
    public bool SetVerboseLoggingUserOption(string fieldName, HashSet<string> newValue, JsonSettingsWrapper defaultSettings, bool overwriteOnly)
    {
        // remove default
        if (newValue.Count == 0)
            return this.RemoveUserOption(fieldName);

        // else overwrite value
        if (this.Properties.GetValueOrDefault(fieldName)?.ToObject<HashSet<string>>() is { } oldSet)
        {
            oldSet = new HashSet<string>(oldSet, StringComparer.OrdinalIgnoreCase);

            bool changed = newValue.Count != oldSet.Count || !oldSet.All(newValue.Contains);
            if (changed)
            {
                this.Properties[fieldName] = JToken.FromObject(newValue);
                this.Changed = true;
            }

            return changed;
        }

        // else add value
        if (!overwriteOnly)
        {
            this.Properties[fieldName] = JToken.FromObject(newValue);
            this.Changed = true;
            return true;
        }

        return false;
    }

    /// <summary>Get whether a token has the expected value.</summary>
    /// <param name="token">The token whose value to check.</param>
    /// <param name="expectedValue">The value to match.</param>
    private bool TokenHasValue(JToken token, object expectedValue)
    {
        object? actualValue = token.Value<JValue>()?.Value;
        return expectedValue.Equals(actualValue);
    }
}
