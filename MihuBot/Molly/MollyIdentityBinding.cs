#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// The outcome of binding an <c>associate</c> request's Signal identity to its Molly entry
/// (FileSharing.md, "Identity binding").
/// </summary>
public enum MollyIdentityBinding
{
    /// <summary>The entry is bound to exactly this identity, possibly by this request.</summary>
    Bound,

    /// <summary>The entry was bound to a different identity too recently to rebind it, which stays bound.</summary>
    Cooldown,
}

public static class MollyIdentityBindingExtensions
{
    /// <summary>The <c>identity</c> value of <c>associate</c> responses.</summary>
    public static string ToAssociateWireValue(this MollyIdentityBinding binding) => binding switch
    {
        MollyIdentityBinding.Bound => "bound",
        MollyIdentityBinding.Cooldown => "cooldown",
        _ => throw new ArgumentOutOfRangeException(nameof(binding), binding, "Associate never reports this binding."),
    };
}