using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ClaudetRelay.Models;

public enum VoiceCommandType
{
    Phrase,  // trigger on spoken/transcribed phrase only
    Noise,   // trigger on matched audio noise; may also have phrase→action mappings
}

public enum VoiceCommandAction
{
    None,               // no immediate action (used as default for noise with phrase mappings)
    NewLine,
    DeleteLastWord,
    DeleteLastSentence,
    DeleteAll,
    Send,
    Undo,
    InsertCharacter,    // shows a small input popup; whatever the user types is inserted at caret
}

/// <summary>One phrase→action pair inside a Noise command's phrase-mapping list.</summary>
public sealed class PhraseActionMapping
{
    public string             Phrase               { get; set; } = "";
    public VoiceCommandAction Action               { get; set; } = VoiceCommandAction.NewLine;
    public string             InsertCharacterValue { get; set; } = "";
}

/// <summary>
/// A single user-defined voice command. Persisted in settings.
/// </summary>
public sealed class VoiceCommand
{
    public string             Id      { get; set; } = Guid.NewGuid().ToString();
    public string             Name    { get; set; } = "";
    public bool               Enabled { get; set; } = true;
    public VoiceCommandType   Type    { get; set; } = VoiceCommandType.Phrase;

    // ── Phrase type ────────────────────────────────────────────────────────
    public string             Phrase { get; set; } = "";
    public VoiceCommandAction Action { get; set; } = VoiceCommandAction.NewLine;

    // ── Noise type ─────────────────────────────────────────────────────────
    /// <summary>
    /// Up to 3 reference recordings (PCM float[], 16 kHz mono).
    /// NOT serialised to settings.json — saved/loaded as WAV files by SettingsService.
    /// </summary>
    [JsonIgnore]
    public float[]?[] NoiseSamples { get; set; } = new float[]?[3];

    /// <summary>
    /// Words/phrases the ASR model emits when it hears this noise
    /// (semicolon-separated). Stripped from the text box after the noise fires.
    /// </summary>
    public List<string> NoiseFilterWords { get; set; } = new();

    /// <summary>
    /// Action to execute immediately when the noise fires and no phrase mapping
    /// matches within the deadline. <see cref="VoiceCommandAction.None"/> means
    /// do nothing by default (pure phrase-gated noise).
    /// </summary>
    public VoiceCommandAction DefaultAction { get; set; } = VoiceCommandAction.None;

    /// <summary>Character(s) to insert when action is InsertCharacter.</summary>
    public string InsertCharacterValue { get; set; } = "";

    /// <summary>
    /// Optional phrase→action pairs. When the noise fires and the subsequent
    /// ASR output starts with a matching phrase, that action executes instead
    /// of <see cref="DefaultAction"/>.
    /// </summary>
    public List<PhraseActionMapping> PhraseActions { get; set; } = new();
}
