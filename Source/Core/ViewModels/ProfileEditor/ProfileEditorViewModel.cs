using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

using CommunityToolkit.Mvvm.ComponentModel;

using CUE4Parse.UE4.Versions;

using Core.Models.Profiles.Paks;
using Core.ViewModels.Profiles.Framework;

namespace Core.ViewModels.ProfileEditor;

public partial class ProfileEditorViewModel : ProfileViewModelBase
{
    /* ~~~ Game Version Options ~~~ */
    public ObservableCollection<string> GameVersionOptions { get; } = new(
        Enum.GetNames(typeof(EGame))
            .Where(name => name.StartsWith("GAME_"))
            .Select(name => name[5..])
            .GroupBy(trimmed => Regex.IsMatch(trimmed, @"^UE\d+_") ? 1 : 0)
            .OrderBy(g => g.Key)
            .SelectMany(g => g)
    );
    
    [ObservableProperty]
    private string? _selectedVersionName;
   
    /* ~~~ Collections ~~~ */
    public ObservableCollection<PakKeyEntry> PakKeyEntries { get; set; } = [];
    
    public override void Initialize()
    {
        base.Initialize();
        
        if (Profile is null) return;
        
        Profile.Validate();

        SelectedVersionName = Profile.Version.ToString()[5..];

        GeneratePakFileEntries();
    }

    public void GeneratePakFileEntries()
    {
        if (Profile is null) return;
        
        PakKeyEntries.Clear();
        
        foreach (var pakFileEntry in Profile.PakFileEntries)
        {
            var key = "";

            var matchingKey = Profile.Encryption.Keys.FirstOrDefault(k => k.Guid == pakFileEntry.Guid);
            if (matchingKey is not null)
            {
                key = matchingKey.Key;
            }

            PakKeyEntries.Add(new PakKeyEntry
            {
                FileName = pakFileEntry.FileName,
                Key = key,
                Guid = pakFileEntry.Guid
            });
        }

        foreach (var aes in Profile.Encryption.Keys.Where(aes => PakKeyEntries.All(e => e.Guid != aes.Guid)))
        {
            PakKeyEntries.Add(new PakKeyEntry
            {
                Key = aes.Key,
                Guid = aes.Guid
            });
        }

        /* Entries without a container sort by guid because that is the only thing they have, and they
         * sort last: they are leftovers from older builds, not something to scroll past on the way to
         * the containers this installation is actually waiting on a key for. */
        var sorted = PakKeyEntries
            .OrderBy(e => e.HasContainer ? 0 : 1)
            .ThenBy(e => e.HasContainer ? e.FileName : e.Guid, StringComparer.OrdinalIgnoreCase)
            .ToList();

        PakKeyEntries.Clear();

        foreach (var entry in sorted)
        {
            PakKeyEntries.Add(entry);
        }
    }
}
