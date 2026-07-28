namespace StardewModdingAPI.Toolkit.Framework.ModBlacklistData;

/// <summary>A list of malicious mods which should be blocked by SMAPI.</summary>
public class ModBlacklistModel
{
    /*********
    ** Accessors
    *********/
    /// <summary>Metadata about malicious or harmful SMAPI mods which are disabled by default.</summary>
    public ModBlacklistEntryModel[] Blacklist { get; }

    /// <summary>Metadata about individual files which are known to be malicious, and should be blocked. If any file in a folder matches an entry, the entire folder is considered malicious.</summary>
    public LooseFileBlacklistEntryModel[] LooseFileBlacklist { get; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="blacklist"><inheritdoc cref="Blacklist" path="/summary"/></param>
    /// <param name="looseFileBlacklist"><inheritdoc cref="LooseFileBlacklist" path="/summary"/></param>
    public ModBlacklistModel(ModBlacklistEntryModel[]? blacklist, LooseFileBlacklistEntryModel[]? looseFileBlacklist)
    {
        this.Blacklist = blacklist ?? [];
        this.LooseFileBlacklist = looseFileBlacklist ?? [];
    }
}
