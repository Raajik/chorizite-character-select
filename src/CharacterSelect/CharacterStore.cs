using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CharacterSelect;

/// <summary>
/// Facts learned about a character at login, persisted per character id.
/// </summary>
public sealed class CharacterInfo {
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public string Allegiance { get; set; } = "";
    public string? DebugJson { get; set; }
    public long LastSeenUtc { get; set; }
}

/// <summary>
/// Persists per-character facts (level, allegiance) learned at login so the
/// character select screen can show them before the client knows them.
/// </summary>
public sealed class CharacterStore {
    private readonly string _storePath;
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private Dictionary<uint, CharacterInfo> _characters = new();

    public CharacterStore(string dataDirectory) {
        Directory.CreateDirectory(dataDirectory);
        _storePath = Path.Combine(dataDirectory, "characters.json");
        Load();
    }

    /// <summary>All known characters, keyed by id.</summary>
    public IReadOnlyDictionary<uint, CharacterInfo> Characters => _characters;

    /// <summary>Records (or updates) the facts learned for a character at login.</summary>
    public void Record(uint id, string name, int level, string allegiance) {
        var existing = _characters.TryGetValue(id, out var info) ? info : new CharacterInfo();
        existing.Id = id;
        existing.Name = name;
        if (level > 0) existing.Level = level;
        if (!string.IsNullOrEmpty(allegiance)) existing.Allegiance = allegiance;
        existing.LastSeenUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _characters[existing.Id] = existing;
        Save();
    }

    public bool TryGet(uint id, out CharacterInfo? info) =>
        _characters.TryGetValue(id, out info!);

    private void Load() {
        try {
            if (!File.Exists(_storePath)) return;
            var decoded = JsonSerializer.Deserialize<Dictionary<uint, CharacterInfo>>(File.ReadAllText(_storePath), _jsonOptions);
            _characters = decoded ?? new Dictionary<uint, CharacterInfo>();
        }
        catch (Exception) {
            // Corrupt store: start over rather than crash the client.
            _characters = new Dictionary<uint, CharacterInfo>();
        }
    }

    private void Save() {
        try {
            File.WriteAllText(_storePath, JsonSerializer.Serialize(_characters, _jsonOptions));
        }
        catch (Exception) {
            // Never let persistence failures break the game session.
        }
    }
}
