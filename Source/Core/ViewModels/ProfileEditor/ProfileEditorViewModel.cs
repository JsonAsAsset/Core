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

    /* Every entry, filter or no filter. This is what gets written back on save, so it must not be
     * the thing the search box narrows. */
    public ObservableCollection<PakKeyEntry> PakKeyEntries { get; set; } = [];

    /* Containers this installation has and cannot open yet */
    public ObservableCollection<PakKeyEntry> LockedContainers { get; } = [];

    /* Keys whose container is not in this installation, kept because the next build may bring it back */
    public ObservableCollection<PakKeyEntry> OrphanedKeys { get; } = [];

    [ObservableProperty] private string _keySearch = string.Empty;

    public int LockedCount => PakKeyEntries.Count(entry => entry.HasContainer);
    public int LockedWithKeyCount => PakKeyEntries.Count(entry => entry.HasContainer && entry.HasKey);
    public int OrphanedCount => PakKeyEntries.Count(entry => !entry.HasContainer);

    public bool HasLockedResults => LockedContainers.Count > 0;
    public bool HasOrphanedResults => OrphanedKeys.Count > 0;
    public bool HasNoSearchResults => !HasLockedResults && !HasOrphanedResults;

    public string LockedHeader => $"Waiting on a key ({LockedWithKeyCount}/{LockedCount})";
    public string OrphanedHeader => $"No container in this installation ({OrphanedCount})";

    public string KeySummary => LockedCount == 0
        ? "Every container in this installation is readable"
        : LockedWithKeyCount == 0
            ? $"{LockedCount} container(s) locked, none have a key yet"
            : $"{LockedCount - LockedWithKeyCount} of {LockedCount} container(s) still without a key";

    partial void OnKeySearchChanged(string value) => ApplyKeyFilter();
    
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
            entry.PropertyChanged += OnEntryChanged;
            PakKeyEntries.Add(entry);
        }

        ApplyKeyFilter();
    }

    /* A key going from blank to valid changes the counts in the section headers, so they are refreshed
     * as it is typed rather than only when the list is rebuilt. */
    private void OnEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(PakKeyEntry.HasKey)) return;

        OnPropertyChanged(nameof(LockedWithKeyCount));
        OnPropertyChanged(nameof(LockedHeader));
        OnPropertyChanged(nameof(KeySummary));
    }

    public void ApplyKeyFilter()
    {
        LockedContainers.Clear();
        OrphanedKeys.Clear();

        foreach (var entry in PakKeyEntries.Where(entry => entry.Matches(KeySearch)))
        {
            (entry.HasContainer ? LockedContainers : OrphanedKeys).Add(entry);
        }

        OnPropertyChanged(nameof(LockedCount));
        OnPropertyChanged(nameof(LockedWithKeyCount));
        OnPropertyChanged(nameof(OrphanedCount));
        OnPropertyChanged(nameof(HasLockedResults));
        OnPropertyChanged(nameof(HasOrphanedResults));
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(LockedHeader));
        OnPropertyChanged(nameof(OrphanedHeader));
        OnPropertyChanged(nameof(KeySummary));
    }
}
