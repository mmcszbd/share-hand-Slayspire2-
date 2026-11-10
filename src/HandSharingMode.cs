using MegaCrit.Sts2.Core.Combat;
namespace SharedHandPrototype;

// Gameplay authority comes from the run's synchronized host rules, never the
// local menu. Keep stock topology separate from hand semantics and UI.
internal static class HandSharingMode
{
    internal static bool Private => RuleSession.Ready && RuleSession.Current.PrivateHands &&
        RuleSession.SupportedParty;
    internal static bool Shared => !Private;
}

