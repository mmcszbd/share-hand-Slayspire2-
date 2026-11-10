namespace SharedHandPrototype;

// The entire value is included in the host's existing reliable run snapshot.
// Legacy saves omit this property and continue to use the ordinary mode rules.
public enum AdvancedDeckSync { None, Full, AcquisitionsOnly }
public sealed record AdvancedRules(
    bool Enabled = false,
    bool SharedDraw = false,
    bool SharedDiscard = false,
    bool SharedExhaust = false,
    bool UniqueMirroredCards = false,
    bool PersonalHandEffects = false,
    AdvancedDeckSync DeckSync = AdvancedDeckSync.None,
    bool GeneratedForOriginalHolder = false,
    bool PrivateHands = false);
